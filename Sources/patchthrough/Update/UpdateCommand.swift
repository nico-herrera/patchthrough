import AppKit
import ArgumentParser
import Foundation

/// `patchthrough update` checks the release feed from the terminal;
/// `--install` runs the same download, verify, and swap pipeline the
/// menu-bar updater uses.
struct Update: AsyncParsableCommand {
    static let configuration = CommandConfiguration(
        commandName: "update",
        abstract: "Check the release feed for a newer version, and install it with --install."
    )

    @Flag(help: "Download, verify, and install the newer release.")
    var install = false

    func run() async throws {
        guard UpdateSource.hasFeed else {
            print("This build has no update feed. Get new builds the way your team ships them.")
            return
        }
        let current = SemVer(Patchthrough.releaseVersion)
        let state = UpdateState()
        let release: UpdateRelease
        do {
            guard let fetched = try await UpdateFeed.latest(etag: nil) else {
                // 304 cannot happen without an ETag; treat it as a feed fault.
                throw UpdateFeed.FeedError.http(304)
            }
            release = fetched.release
            state.lastCheckedAt = Date()
        } catch {
            // Doctor points the user at this command, so what happens here
            // has to show up there.
            state.record(outcome: UpdateState.outcome(for: error))
            throw error
        }
        guard !release.draft, !release.prerelease, let latest = SemVer(release.tagName) else {
            print("The newest release (\(release.tagName)) is not a stable version. Nothing to do.")
            return
        }
        guard let current else {
            print("""
            The latest release is \(latest). This is a source build with no \
            release version, so there is nothing to compare or install. Build \
            the app with packaging/make-app.sh.
            """)
            if install { throw ExitCode(1) }
            return
        }
        guard latest > current else {
            state.record(outcome: "upToDate")
            print("Up to date (\(current)).")
            return
        }
        guard install else {
            state.record(outcome: "available:\(latest)")
            print("\(latest) is available. This build is \(current). Run: patchthrough update --install")
            return
        }
        do {
            try await performInstall(
                available: .init(release: release, version: latest, etag: nil), current: current
            )
            state.record(outcome: "installed:\(latest)")
        } catch {
            state.record(outcome: UpdateState.outcome(for: error))
            throw error
        }
    }

    private func performInstall(
        available: UpdatePipeline.Available, current: SemVer
    ) async throws {
        guard let dest = UpdateInstaller.currentAppBundle() else {
            throw ValidationError(
                "This binary does not live in an app bundle. Build the app with packaging/make-app.sh."
            )
        }
        guard let bundleID = Bundle.main.bundleIdentifier else {
            throw ValidationError("This build has no bundle identifier to verify against.")
        }
        // Two processes must never race through the swap, and a running
        // app must restart itself so its state survives.
        let running = NSRunningApplication.runningApplications(withBundleIdentifier: bundleID)
        guard running.isEmpty else {
            throw ValidationError("Patchthrough is running. Use the update item in its menu bar.")
        }
        guard UpdateInstaller.destinationIsWritable(dest) else {
            throw ValidationError("""
            \(dest.path) is not writable by this user. Download the DMG and \
            drag it to Applications instead.
            """)
        }

        print("Downloading \(available.version)…")
        let staged = try await UpdatePipeline.downloadAndVerify(
            available, current: current, expectedBundleID: bundleID
        )
        print("Verified: signed by the expected developer and notarized.")
        try UpdatePipeline.install(staged, into: dest)
        UpdateInstaller.cleanupLeftovers(near: dest)
        print("Installed \(available.version) to \(dest.path).")
    }
}
