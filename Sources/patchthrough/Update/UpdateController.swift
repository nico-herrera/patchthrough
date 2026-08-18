import AppKit
import Foundation

/// Owns the update check schedule and the install sequence for the running
/// app. AppController holds one of these and wires the closures, the same
/// shape as MenuBarController.
///
/// **Why this file schedules the way it does.** `run` enters
/// `NSApplication.run()` from inside the async main task and never returns,
/// so the main dispatch queue is occupied for the life of the process.
/// Anything queued behind it never executes: a MainActor `Task`, a
/// `DispatchQueue.main.async`, or an `await MainActor.run` from a background
/// task all hang forever. Measured, not assumed. So this file schedules with
/// `Timer`, does its waiting and its network work in detached tasks, and
/// returns to the main actor through `RunLoop.main.perform`, which is
/// run-loop based rather than queue based. AppController's elapsed ticker
/// copes the same way, with `MainActor.assumeIsolated` inside a Timer.
///
/// An install can run while a transcription is in flight: the new instance
/// re-queues pending work at startup, though a session interrupted
/// mid-write may transcribe again from the start. A recording is different
/// and always blocks the swap.
@MainActor
final class UpdateController {

    enum State: Equatable {
        case idle
        case checking
        case available(SemVer)
        case downloading
        case verifying
        case waitingForRecordingEnd(SemVer)
        case installing
        case manualInstall(SemVer)
        case failed(String)
    }

    private(set) var state: State = .idle {
        didSet {
            guard state != oldValue else { return }
            onStateChange?(state)
        }
    }

    var onStateChange: ((State) -> Void)?
    var isRecording: () -> Bool = { false }

    private var timer: Timer?
    private var settleTimer: Timer?
    private var pending: UpdatePipeline.Available?
    /// A verified image handed to the user to drag, when this copy of the app
    /// is not ours to replace.
    private var manualDMG: URL?
    private var missedCheck = false
    private var busy = false

    private let checkInterval: TimeInterval = 6 * 60 * 60
    private let store = UpdateState()

    /// PATCHTHROUGH_DEBUG_UPDATE traces the updater to stderr, the way
    /// PATCHTHROUGH_DEBUG_WINDOW traces the window frame. The LaunchAgent
    /// sends stderr to /tmp/patchthrough.err.log, so a daemon run is
    /// readable too.
    private let debug = ProcessInfo.processInfo.environment["PATCHTHROUGH_DEBUG_UPDATE"] != nil

    private func log(_ message: String) {
        guard debug else { return }
        FileHandle.standardError.write(Data("update: \(message)\n".utf8))
    }

    // MARK: - Schedule

    /// Starts checking. Safe to call again; the schedule is rebuilt.
    func start() {
        stop()
        guard UpdateSource.hasFeed else {
            log("no feed configured for this build")
            return
        }
        UpdateInstaller.cleanupLeftovers(near: UpdateInstaller.currentAppBundle() ?? URL(fileURLWithPath: "/"))
        log("start, feed \(UpdateSource.feedURL.absoluteString)")

        // PATCHTHROUGH_DEBUG_UPDATE checks at once, so a verification run
        // does not wait out the settle delay below.
        if debug {
            check(userAsked: true)
        }

        let sinceLast = Date().timeIntervalSince(store.lastCheckedAt ?? .distantPast)
        if sinceLast > checkInterval {
            // Let launch settle first. The delay is jittered so a fleet of
            // machines that all start at 9 a.m. does not arrive together.
            settleTimer = mainTimer(after: 60 + Double.random(in: 0...120)) { [weak self] in
                self?.timerFired()
            }
        }
        timer = mainTimer(after: checkInterval, repeats: true, tolerance: 30 * 60) { [weak self] in
            self?.timerFired()
        }
    }

    func stop() {
        timer?.invalidate()
        timer = nil
        settleTimer?.invalidate()
        settleTimer = nil
    }

    /// A run-loop timer, which fires even though the main queue is blocked.
    private func mainTimer(
        after delay: TimeInterval,
        repeats: Bool = false,
        tolerance: TimeInterval = 0,
        _ body: @escaping @MainActor () -> Void
    ) -> Timer {
        let timer = Timer(timeInterval: delay, repeats: repeats) { _ in
            MainActor.assumeIsolated { body() }
        }
        timer.tolerance = tolerance
        RunLoop.main.add(timer, forMode: .common)
        return timer
    }

    private func timerFired() {
        // A GET would not disturb a recording, but the recording path is
        // the one that must never compete for anything.
        guard !isRecording() else {
            missedCheck = true
            return
        }
        check(userAsked: false)
    }

    /// Runs deferred work once a recording ends: an install the user asked
    /// for, or a check the timer skipped.
    func recordingDidStop() {
        if case .waitingForRecordingEnd = state, pending != nil {
            install()
            return
        }
        if missedCheck {
            missedCheck = false
            check(userAsked: false)
        }
    }

    // MARK: - Check

    /// Starts a check and returns. The answer arrives on the main actor
    /// later, through `checkFinished`.
    func check(userAsked: Bool) {
        log("check requested (userAsked: \(userAsked))")
        guard !busy else {
            log("check skipped, already working")
            return
        }
        guard let current = SemVer(Patchthrough.releaseVersion) else {
            // A source build has no version to compare.
            log("check skipped, no release version")
            return
        }
        busy = true
        state = .checking
        let etag = userAsked ? nil : store.feedETag

        offMain { [weak self] in
            do {
                let result = try await UpdatePipeline.check(current: current, etag: etag)
                onMainThread { self?.checkFinished(result) }
            } catch {
                let outcome = UpdateState.outcome(for: error)
                let described = "\(error)"
                onMainThread { self?.checkFailed(outcome: outcome, described: described) }
            }
        }
    }

    private func checkFinished(_ result: UpdatePipeline.CheckResult) {
        busy = false
        store.lastCheckedAt = Date()
        log("check finished: \(result)")
        switch result {
        case .unchanged:
            state = pending == nil ? .idle : state
        case .upToDate:
            store.record(outcome: "upToDate")
            pending = nil
            state = .idle
        case .available(let available):
            store.feedETag = available.etag
            store.record(outcome: "available:\(available.version)")
            pending = available
            state = .available(available.version)
            notifyUser(
                title: "Patchthrough: Update available",
                body: "Version \(available.version) is ready. Install it from the menu bar, or click here.",
                identifier: "update.available.\(available.version)"
            )
        }
    }

    private func checkFailed(outcome: String, described: String) {
        busy = false
        store.record(outcome: outcome)
        log("check failed: \(described)")
        // A failed check stays quiet: the user did not ask for it, and the
        // current version keeps working.
        state = pending == nil ? .idle : state
    }

    // MARK: - Install

    /// The menu item, the Settings button, and the notification all land here.
    func updateClicked() {
        switch state {
        case .available, .waitingForRecordingEnd:
            install()
        case .failed:
            check(userAsked: true)
        case .manualInstall:
            // The image is verified and still on disk, so reopen it rather
            // than download it a second time.
            if let dmg = manualDMG, FileManager.default.fileExists(atPath: dmg.path) {
                UpdateInstaller.openForManualInstall(dmg: dmg)
            } else {
                manualDMG = nil
                install()
            }
        case .idle:
            check(userAsked: true)
        case .checking, .downloading, .verifying, .installing:
            break
        }
    }

    private func install() {
        guard let available = pending, !busy else { return }
        if isRecording() {
            state = .waitingForRecordingEnd(available.version)
            notifyUser(
                title: "Patchthrough: Update waiting",
                body: "The update installs after this recording ends.",
                identifier: "update.deferred.\(available.version)"
            )
            return
        }
        guard let dest = UpdateInstaller.currentAppBundle(),
              let bundleID = Bundle.main.bundleIdentifier,
              let current = SemVer(Patchthrough.releaseVersion) else { return }

        busy = true
        state = .downloading
        // The download, the mount, and the signature checks all happen here,
        // off the main thread: several seconds of work that would otherwise
        // freeze the interface.
        offMain { [weak self] in
            do {
                let staged = try await UpdatePipeline.downloadAndVerify(
                    available, current: current, expectedBundleID: bundleID
                )
                onMainThread { self?.verified(staged, dest: dest, version: available.version) }
            } catch {
                let outcome = UpdateState.outcome(for: error)
                let reason = reasonText(for: error)
                onMainThread { self?.installFailed(outcome: outcome, reason: reason) }
            }
        }
    }

    /// Everything past verification: fast, local, and finished by quitting,
    /// so it stays on the main actor.
    private func verified(_ staged: UpdatePipeline.Staged, dest: URL, version: SemVer) {
        state = .verifying

        // A recording can start while the download runs, and the swap must
        // not land under one.
        guard !isRecording() else {
            UpdatePipeline.abort(staged)
            busy = false
            state = .waitingForRecordingEnd(version)
            return
        }
        guard UpdateInstaller.destinationIsWritable(dest) else {
            // Verified, but this copy is not ours to replace.
            UpdateInstaller.openForManualInstall(dmg: staged.dmg)
            manualDMG = staged.dmg
            UpdateInstaller.detach(staged.mounted)
            busy = false
            state = .manualInstall(version)
            store.record(outcome: "manual:\(version)")
            notifyUser(
                title: "Patchthrough: Update downloaded",
                body: "Drag Patchthrough to Applications to finish. The download is verified.",
                identifier: "update.manual.\(version)"
            )
            return
        }

        state = .installing
        do {
            try UpdatePipeline.install(staged, into: dest)
            store.record(outcome: "installed:\(version)")
            log("installed \(version), restarting")
            // kickstart kills this process without ceremony, so every
            // durable write has to be on disk before the call.
            store.flush()
            UpdateInstaller.relaunch(dest: dest, agentLabel: LaunchAtLogin.label)
            NSApp.terminate(nil)
        } catch {
            installFailed(outcome: UpdateState.outcome(for: error), reason: reasonText(for: error))
        }
    }

    private func installFailed(outcome: String, reason: String) {
        busy = false
        store.record(outcome: outcome)
        state = .failed(reason)
        log("install failed: \(reason)")
        notifyUser(
            title: "Patchthrough: Update failed",
            body: "\(reason). The current version keeps running.",
            identifier: "update.failed"
        )
    }
}

/// Runs async work off the main thread. A detached task is deliberate: a
/// MainActor task would sit behind `NSApplication.run()` forever.
private func offMain(_ work: @escaping @Sendable () async -> Void) {
    Task.detached(priority: .utility) { await work() }
}

/// Returns to the main actor through the run loop, which keeps working while
/// the main dispatch queue is blocked.
private func onMainThread(_ work: @escaping @Sendable @MainActor () -> Void) {
    RunLoop.main.perform { MainActor.assumeIsolated { work() } }
}

/// A sentence the user reads. Only the updater's own errors carry copy
/// written for that; anything else gets a plain fallback rather than a
/// framework message.
private func reasonText(for error: Error) -> String {
    switch error {
    case let feed as UpdateFeed.FeedError: return feed.description
    case let verify as UpdateVerifier.VerifyError: return verify.description
    case let install as UpdateInstaller.InstallError: return install.description
    case let pipeline as UpdatePipeline.PipelineError: return pipeline.description
    default: return "The update could not be downloaded"
    }
}

/// Updater bookkeeping. UserDefaults, not config.json: this is app state,
/// not user intent, and the preference domain is already per bundle id.
struct UpdateState {
    private let defaults = UserDefaults.standard

    var lastCheckedAt: Date? {
        get { defaults.object(forKey: "update.lastCheckedAt") as? Date }
        nonmutating set { defaults.set(newValue, forKey: "update.lastCheckedAt") }
    }

    var feedETag: String? {
        get { defaults.string(forKey: "update.feedETag") }
        nonmutating set { defaults.set(newValue, forKey: "update.feedETag") }
    }

    var lastOutcome: String? { defaults.string(forKey: "update.lastOutcome") }
    var lastOutcomeAt: Date? { defaults.object(forKey: "update.lastOutcomeAt") as? Date }

    /// Classifies a failure for the record. Doctor reads these strings, and
    /// `failed:unauthorized` is the one it escalates: a feed that rejects a
    /// build's credentials stops updates with no other symptom.
    static func outcome(for error: Error) -> String {
        switch error {
        case UpdateFeed.FeedError.unauthorized: return "failed:unauthorized"
        case UpdateFeed.FeedError.rateLimited: return "failed:rateLimited"
        case is UpdateVerifier.VerifyError: return "failed:verify"
        case is UpdateInstaller.InstallError: return "failed:install"
        default: return "failed:network"
        }
    }

    func record(outcome: String) {
        defaults.set(outcome, forKey: "update.lastOutcome")
        defaults.set(Date(), forKey: "update.lastOutcomeAt")
    }

    func flush() {
        defaults.synchronize()
    }
}
