import AppKit
import Foundation

/// Mounts a verified DMG, swaps the bundle, and restarts the app. Nothing
/// here escalates privileges: a destination the user cannot write falls
/// back to opening the DMG for a manual drag.
enum UpdateInstaller {
    enum InstallError: Error, CustomStringConvertible {
        case mountFailed(String)
        case noSingleApp(Int)
        case stagingFailed(String)
        case swapFailed(String)

        var description: String {
            switch self {
            case .mountFailed(let detail):
                return "The update image failed to mount (\(detail))"
            case .noSingleApp(let count):
                return "The update image holds \(count) apps, expected one"
            case .stagingFailed(let detail):
                return "The update could not be staged (\(detail))"
            case .swapFailed(let detail):
                return "The update could not replace the app (\(detail))"
            }
        }
    }

    struct Mounted {
        let point: URL
        let app: URL
    }

    /// The .app bundle this process runs from, resolved the way
    /// `releaseVersion` resolves it: Bundle.main when the executable lives
    /// in a bundle, else argv[0] through symlinks. Nil for source builds.
    static func currentAppBundle() -> URL? {
        let main = Bundle.main.bundleURL
        if main.pathExtension.lowercased() == "app" { return main }
        let executable = URL(fileURLWithPath: CommandLine.arguments[0])
            .resolvingSymlinksInPath()
        let app = executable
            .deletingLastPathComponent() // MacOS
            .deletingLastPathComponent() // Contents
            .deletingLastPathComponent() // patchthrough.app
        return app.pathExtension.lowercased() == "app" ? app : nil
    }

    /// Mounts read-only under the DMG's own directory (-mountrandom), so
    /// the volume never appears in /Volumes or Finder.
    static func mount(dmg: URL) throws -> Mounted {
        let out = Pipe()
        let task = Process()
        task.executableURL = URL(fileURLWithPath: "/usr/bin/hdiutil")
        task.arguments = [
            "attach", dmg.path, "-nobrowse", "-readonly", "-plist",
            "-mountrandom", dmg.deletingLastPathComponent().path,
        ]
        task.standardOutput = out
        task.standardError = FileHandle.nullDevice
        try task.run()
        let data = out.fileHandleForReading.readDataToEndOfFile()
        task.waitUntilExit()
        guard task.terminationStatus == 0 else {
            throw InstallError.mountFailed("hdiutil exit \(task.terminationStatus)")
        }
        guard
            let plist = try? PropertyListSerialization.propertyList(from: data, format: nil)
                as? [String: Any],
            let entities = plist["system-entities"] as? [[String: Any]],
            let mountPoint = entities.compactMap({ $0["mount-point"] as? String }).first
        else {
            throw InstallError.mountFailed("no mount point reported")
        }
        let point = URL(fileURLWithPath: mountPoint, isDirectory: true)
        let apps = (try? FileManager.default.contentsOfDirectory(
            at: point, includingPropertiesForKeys: nil
        ))?.filter { $0.pathExtension == "app" } ?? []
        guard apps.count == 1, let app = apps.first else {
            detach(point)
            throw InstallError.noSingleApp(apps.count)
        }
        return Mounted(point: point, app: app)
    }

    static func detach(_ mounted: Mounted) { detach(mounted.point) }

    private static func detach(_ point: URL) {
        let task = Process()
        task.executableURL = URL(fileURLWithPath: "/usr/bin/hdiutil")
        task.arguments = ["detach", point.path, "-quiet", "-force"]
        task.standardOutput = FileHandle.nullDevice
        task.standardError = FileHandle.nullDevice
        try? task.run()
        task.waitUntilExit()
    }

    /// A drag-installed /Applications copy may be admin-owned; the swap
    /// needs to rename both the bundle and a sibling inside its parent.
    static func destinationIsWritable(_ dest: URL) -> Bool {
        let fm = FileManager.default
        return fm.isWritableFile(atPath: dest.path)
            && fm.isWritableFile(atPath: dest.deletingLastPathComponent().path)
    }

    /// Copies the verified app to a hidden staged sibling of the
    /// destination (same volume), then swaps with two renames. Two renames
    /// beat `replaceItemAt`, whose safe-save machinery misbehaves on a
    /// running app bundle. The running process keeps executing from the
    /// renamed-away inodes; the new instance's janitor deletes them.
    static func swap(verifiedApp: URL, into dest: URL) throws {
        let fm = FileManager.default
        let parent = dest.deletingLastPathComponent()
        let name = dest.lastPathComponent
        let tag = UUID().uuidString
        let staged = parent.appendingPathComponent(".\(name).staged-\(tag)")
        let old = parent.appendingPathComponent(".\(name).old-\(tag)")

        do {
            try fm.copyItem(at: verifiedApp, to: staged)
        } catch {
            try? fm.removeItem(at: staged)
            throw InstallError.stagingFailed(error.localizedDescription)
        }
        do {
            try fm.moveItem(at: dest, to: old)
        } catch {
            try? fm.removeItem(at: staged)
            throw InstallError.swapFailed(error.localizedDescription)
        }
        do {
            try fm.moveItem(at: staged, to: dest)
        } catch {
            // Put the original back so a failed update leaves a working app.
            try? fm.moveItem(at: old, to: dest)
            try? fm.removeItem(at: staged)
            throw InstallError.swapFailed(error.localizedDescription)
        }
    }

    /// A swapped bundle can silently lose its Notification Center
    /// registration; a forced LaunchServices re-registration fixes it.
    static func reregister(_ dest: URL) {
        let lsregister = "/System/Library/Frameworks/CoreServices.framework/Frameworks/"
            + "LaunchServices.framework/Support/lsregister"
        let task = Process()
        task.executableURL = URL(fileURLWithPath: lsregister)
        task.arguments = ["-f", dest.path]
        task.standardOutput = FileHandle.nullDevice
        task.standardError = FileHandle.nullDevice
        try? task.run()
        task.waitUntilExit()
    }

    /// True when launchd actually has the agent bootstrapped this session.
    /// LaunchAtLogin.isEnabled only proves the plist file exists.
    static func agentIsLoaded(label: String) -> Bool {
        let task = Process()
        task.executableURL = URL(fileURLWithPath: "/bin/launchctl")
        task.arguments = ["print", "gui/\(getuid())/\(label)"]
        task.standardOutput = FileHandle.nullDevice
        task.standardError = FileHandle.nullDevice
        do { try task.run() } catch { return false }
        task.waitUntilExit()
        return task.terminationStatus == 0
    }

    /// Restarts the app after a swap. With the agent loaded, kickstart
    /// kills this process and starts the new binary, so the call does not
    /// return; a clean self-exit would NOT be respawned (the agent keeps
    /// alive on SuccessfulExit:false only). Otherwise a detached helper
    /// waits for this pid to fully exit and opens the new bundle; waiting
    /// matters because a new instance that starts early would see the old
    /// one, signal it, and quit (the single-instance handshake). The
    /// caller must terminate promptly after this returns.
    static func relaunch(dest: URL, agentLabel: String) {
        if agentIsLoaded(label: agentLabel) {
            let task = Process()
            task.executableURL = URL(fileURLWithPath: "/bin/launchctl")
            task.arguments = ["kickstart", "-k", "gui/\(getuid())/\(agentLabel)"]
            try? task.run()
            task.waitUntilExit()
            if task.terminationStatus == 0 { return }
            // Kickstart refused; fall through to the helper.
        }
        let pid = ProcessInfo.processInfo.processIdentifier
        let script = "while kill -0 \(pid) 2>/dev/null; do sleep 0.3; done; "
            + "sleep 0.5; open \"\(dest.path)\""
        let helper = Process()
        helper.executableURL = URL(fileURLWithPath: "/bin/sh")
        helper.arguments = ["-c", script]
        helper.standardOutput = FileHandle.nullDevice
        helper.standardError = FileHandle.nullDevice
        try? helper.run()
    }

    /// Fallback when the destination is not writable: the DMG is already
    /// verified, so hand it to the user to drag. No privilege escalation.
    static func openForManualInstall(dmg: URL) {
        NSWorkspace.shared.open(dmg)
    }

    /// Name of a scratch download directory. The UUID is what makes a
    /// leftover identifiable later; see `cleanupLeftovers`.
    static func downloadDirectoryName() -> String {
        "patchthrough-update-\(UUID().uuidString)"
    }

    /// Startup janitor. Deletes swap leftovers next to the bundle and stale
    /// download directories, which also heals an update that died between
    /// its two renames.
    ///
    /// Both patterns match on an exact `<prefix>-<UUID>` shape rather than a
    /// bare prefix, and downloads must also be an hour old. A prefix alone
    /// would delete anything a person or another tool happened to name that
    /// way, and, worse, would delete a concurrent updater's download while
    /// it was still verifying it.
    static func cleanupLeftovers(near dest: URL) {
        let fm = FileManager.default
        let name = dest.lastPathComponent
        let parent = dest.deletingLastPathComponent()
        for entry in (try? fm.contentsOfDirectory(atPath: parent.path)) ?? [] {
            for prefix in [".\(name).old-", ".\(name).staged-"] where entry.hasPrefix(prefix) {
                guard UUID(uuidString: String(entry.dropFirst(prefix.count))) != nil else { continue }
                try? fm.removeItem(at: parent.appendingPathComponent(entry))
            }
        }

        let temp = fm.temporaryDirectory
        let prefix = "patchthrough-update-"
        let staleBefore = Date().addingTimeInterval(-60 * 60)
        for entry in (try? fm.contentsOfDirectory(atPath: temp.path)) ?? [] {
            guard entry.hasPrefix(prefix),
                  UUID(uuidString: String(entry.dropFirst(prefix.count))) != nil else { continue }
            let url = temp.appendingPathComponent(entry)
            let modified = (try? url.resourceValues(forKeys: [.contentModificationDateKey]))?
                .contentModificationDate
            guard let modified, modified < staleBefore else { continue }
            try? fm.removeItem(at: url)
        }
    }
}
