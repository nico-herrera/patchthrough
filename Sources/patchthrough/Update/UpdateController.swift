import AppKit
import Foundation

/// Owns the update check schedule and the install sequence for the running
/// app. AppController holds one of these and wires the closures, the same
/// shape as MenuBarController.
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
    private var pending: UpdatePipeline.Available?
    /// A verified image handed to the user to drag, when this copy of the app
    /// is not ours to replace.
    private var manualDMG: URL?
    private var missedCheck = false
    private var busy = false

    private let checkInterval: TimeInterval = 6 * 60 * 60
    private let store = UpdateState()

    /// PATCHTHROUGH_DEBUG_UPDATE traces the updater to stderr, the way
    /// PATCHTHROUGH_DEBUG_WINDOW traces the window frame.
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
            Task { await check(userAsked: true) }
        }

        let sinceLast = Date().timeIntervalSince(store.lastCheckedAt ?? .distantPast)
        if sinceLast > checkInterval {
            // Let launch settle first. The delay is jittered so a fleet of
            // machines that all start at 9 a.m. does not arrive together.
            schedule(after: 60 + Double.random(in: 0...120))
        }
        let timer = Timer(timeInterval: checkInterval, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.timerFired() }
        }
        timer.tolerance = 30 * 60
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
    }

    func stop() {
        timer?.invalidate()
        timer = nil
    }

    private func schedule(after delay: TimeInterval) {
        Task { @MainActor [weak self] in
            try? await Task.sleep(for: .seconds(delay))
            self?.timerFired()
        }
    }

    private func timerFired() {
        // A GET would not disturb a recording, but the recording path is
        // the one that must never compete for anything.
        guard !isRecording() else {
            missedCheck = true
            return
        }
        Task { await check(userAsked: false) }
    }

    /// Runs deferred work once a recording ends: an install the user asked
    /// for, or a check the timer skipped.
    func recordingDidStop() {
        if case .waitingForRecordingEnd = state, pending != nil {
            Task { await install() }
            return
        }
        if missedCheck {
            missedCheck = false
            Task { await check(userAsked: false) }
        }
    }

    // MARK: - Check

    @discardableResult
    func check(userAsked: Bool) async -> State {
        log("check requested (userAsked: \(userAsked))")
        guard !busy else {
            log("check skipped, already working")
            return state
        }
        guard let current = SemVer(Patchthrough.releaseVersion) else {
            // A source build has no version to compare.
            log("check skipped, no release version")
            return state
        }
        busy = true
        state = .checking
        defer { busy = false }

        do {
            let result = try await UpdatePipeline.check(
                current: current, etag: userAsked ? nil : store.feedETag
            )
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
        } catch {
            store.record(outcome: UpdateState.outcome(for: error))
            log("check failed: \(error)")
            // A failed check stays quiet: the user did not ask for it, and
            // the current version keeps working.
            state = pending == nil ? .idle : state
        }
        return state
    }

    // MARK: - Install

    /// The menu item and the notification both land here.
    func updateClicked() {
        switch state {
        case .available, .waitingForRecordingEnd:
            Task { await install() }
        case .failed:
            Task { await check(userAsked: true) }
        case .manualInstall:
            // The image is verified and still on disk, so reopen it rather
            // than download it a second time.
            if let dmg = manualDMG, FileManager.default.fileExists(atPath: dmg.path) {
                UpdateInstaller.openForManualInstall(dmg: dmg)
            } else {
                manualDMG = nil
                Task { await install() }
            }
        case .idle:
            Task { await check(userAsked: true) }
        case .checking, .downloading, .verifying, .installing:
            break
        }
    }

    private func install() async {
        guard let available = pending else { return }
        guard !busy else { return }
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
        defer { busy = false }
        state = .downloading
        do {
            let staged = try await UpdatePipeline.downloadAndVerify(
                available, current: current, expectedBundleID: bundleID
            )
            state = .verifying

            // A recording can start while the download runs, and the swap
            // must not land under one.
            guard !isRecording() else {
                UpdatePipeline.abort(staged)
                state = .waitingForRecordingEnd(available.version)
                return
            }
            guard UpdateInstaller.destinationIsWritable(dest) else {
                // Verified, but this copy is not ours to replace.
                UpdateInstaller.openForManualInstall(dmg: staged.dmg)
                // Kept so a second click reopens this image instead of
                // downloading it again. The image stays on disk; the janitor
                // collects it once it is an hour old.
                manualDMG = staged.dmg
                UpdateInstaller.detach(staged.mounted)
                state = .manualInstall(available.version)
                store.record(outcome: "manual:\(available.version)")
                notifyUser(
                    title: "Patchthrough: Update downloaded",
                    body: "Drag Patchthrough to Applications to finish. The download is verified.",
                    identifier: "update.manual.\(available.version)"
                )
                return
            }

            state = .installing
            try UpdatePipeline.install(staged, into: dest)
            store.record(outcome: "installed:\(available.version)")
            // kickstart kills this process without ceremony, so every
            // durable write has to be on disk before the call.
            store.flush()
            UpdateInstaller.relaunch(dest: dest, agentLabel: LaunchAtLogin.label)
            NSApp.terminate(nil)
        } catch {
            let reason = Self.reason(for: error)
            store.record(outcome: UpdateState.outcome(for: error))
            state = .failed(reason)
            notifyUser(
                title: "Patchthrough: Update failed",
                body: "\(reason). The current version keeps running.",
                identifier: "update.failed"
            )
        }
    }

    /// A sentence the user reads. Only the updater's own errors carry copy
    /// written for that; anything else gets a plain fallback rather than a
    /// framework message.
    private static func reason(for error: Error) -> String {
        switch error {
        case let feed as UpdateFeed.FeedError: return feed.description
        case let verify as UpdateVerifier.VerifyError: return verify.description
        case let install as UpdateInstaller.InstallError: return install.description
        case let pipeline as UpdatePipeline.PipelineError: return pipeline.description
        default: return "The update could not be downloaded"
        }
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
