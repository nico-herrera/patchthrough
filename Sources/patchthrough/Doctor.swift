import AVFoundation
import FluidAudio
import Foundation

enum CheckStatus {
    case ok
    case warn(String)
    case fail(String)
}

struct Check {
    let name: String
    let status: CheckStatus
    let remediation: String?
}

enum DoctorReport {
    static func run(recordingsRoot: URL) -> [Check] {
        [
            checkMicrophone(),
            checkSystemAudio(),
            checkRecordingsRoot(recordingsRoot),
            checkTranscription(),
            checkUpdates(),
            checkInstallLocation(),
        ]
    }

    /// Two installed copies confuse an update: the updater replaces the one
    /// that is running, and LaunchServices can still resolve the app by
    /// bundle id to the other, older copy.
    static func checkInstallLocation() -> Check {
        let name = "install location"
        let home = FileManager.default.homeDirectoryForCurrentUser
        let candidates = [
            home.appendingPathComponent("Applications/patchthrough.app"),
            URL(fileURLWithPath: "/Applications/patchthrough.app"),
        ]
        let installed = candidates.filter { FileManager.default.fileExists(atPath: $0.path) }
        guard installed.count > 1 else {
            return Check(name: name, status: .ok, remediation: nil)
        }
        return Check(
            name: name,
            status: .warn("two copies are installed"),
            remediation: "keep one: delete either \(installed[0].path) or \(installed[1].path)"
        )
    }

    /// Reads what the last check recorded. Never asks the network: this
    /// runs at every app launch, and a launch must not wait on GitHub.
    static func checkUpdates() -> Check {
        let name = "updates"
        guard UpdateSource.hasFeed else {
            return Check(
                name: name,
                status: .warn("this build has no update feed"),
                remediation: nil
            )
        }
        // A source build has no release version to compare, so it can
        // neither check nor install. Say so instead of asking for a check
        // that would report nothing.
        guard SemVer(Patchthrough.releaseVersion) != nil else {
            return Check(
                name: name,
                status: .warn("source build, so updates do not apply"),
                remediation: nil
            )
        }
        // The accessor already forces true on a build that forbids disabling.
        guard Config.updateCheckEnabled() else {
            return Check(
                name: name,
                status: .warn("checks are off"),
                remediation: "turn them on in Settings, or run: patchthrough update"
            )
        }
        let state = UpdateState()
        guard let outcome = state.lastOutcome, let at = state.lastOutcomeAt else {
            return Check(
                name: name,
                status: .warn("no check has run yet"),
                remediation: "run: patchthrough update"
            )
        }
        // A feed that rejects this build's credentials means updates have
        // stopped arriving, which is otherwise invisible. Fail loudly.
        if outcome == "failed:unauthorized" {
            return Check(
                name: name,
                status: .fail("the update feed rejected this build's credentials"),
                remediation: "this build can no longer see updates. Get the current build from IT"
            )
        }
        if Date().timeIntervalSince(at) > 48 * 60 * 60 {
            return Check(
                name: name,
                status: .warn("no successful check in the last two days"),
                remediation: "run: patchthrough update"
            )
        }
        if outcome.hasPrefix("available:") {
            let version = String(outcome.dropFirst("available:".count))
            return Check(
                name: name,
                status: .warn("version \(version) is available"),
                remediation: "click Update to \(version) in the menu bar"
            )
        }
        if outcome.hasPrefix("failed:") {
            return Check(
                name: name,
                status: .warn("the last check did not finish (\(outcome))"),
                remediation: "run: patchthrough update"
            )
        }
        return Check(name: name, status: .ok, remediation: nil)
    }

    static func checkMicrophone() -> Check {
        let status = AVCaptureDevice.authorizationStatus(for: .audio)
        switch status {
        case .authorized:
            return Check(name: "microphone", status: .ok, remediation: nil)
        case .notDetermined:
            return Check(
                name: "microphone",
                status: .warn("not yet requested. macOS prompts on the first recording"),
                remediation: "start a recording once; macOS will prompt"
            )
        case .denied, .restricted:
            return Check(
                name: "microphone",
                status: .fail("denied"),
                remediation: "System Settings → Privacy & Security → Microphone → enable for Patchthrough (or your terminal)"
            )
        @unknown default:
            return Check(name: "microphone", status: .fail("unknown state"), remediation: nil)
        }
    }

    /// There is no public API to query the system-audio-capture TCC state
    /// without side effects, so all we can do is describe the flow.
    static func checkSystemAudio() -> Check {
        Check(
            name: "system audio",
            status: .warn("state unknowable until first use. macOS prompts on the first recording"),
            remediation: "if recordings come out silent: System Settings → Privacy & Security → Screen & System Audio Recording"
        )
    }

    static func checkRecordingsRoot(_ root: URL) -> Check {
        do {
            try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        } catch {
            return Check(
                name: "recordings folder",
                status: .fail("can't create \(root.path)"),
                remediation: "check permissions on the parent directory"
            )
        }
        guard FileManager.default.isWritableFile(atPath: root.path) else {
            return Check(
                name: "recordings folder",
                status: .fail("\(root.path) is not writable"),
                remediation: "check permissions on the directory"
            )
        }
        return Check(name: "recordings folder", status: .ok, remediation: nil)
    }

    /// Never discover a missing model after an important meeting: report
    /// whether the parakeet models are already in FluidAudio's cache.
    static func checkTranscription() -> Check {
        guard Config.transcriptionEnabled() else {
            return Check(
                name: "transcription",
                status: .warn("disabled in config"),
                remediation: nil
            )
        }
        let cache = AsrModels.defaultCacheDirectory(for: .v2)
        if AsrModels.modelsExist(at: cache, version: .v2) {
            return Check(name: "transcription", status: .ok, remediation: nil)
        }
        return Check(
            name: "transcription",
            status: .warn("parakeet models not downloaded (~600 MB)"),
            remediation: "the models download automatically on the first transcription. Record a short test session while online"
        )
    }

    static func print(_ checks: [Check]) {
        for c in checks {
            let (mark, label): (String, String) = {
                switch c.status {
                case .ok: return ("✓", "ok")
                case .warn(let msg): return ("!", msg)
                case .fail(let msg): return ("✗", msg)
                }
            }()
            Swift.print("\(mark) \(c.name): \(label)")
            if let r = c.remediation {
                Swift.print("    → \(r)")
            }
        }
    }

    /// True if no checks are in a hard-fail state. Warnings don't block.
    static func allOK(_ checks: [Check]) -> Bool {
        checks.allSatisfy {
            if case .fail = $0.status { return false }
            return true
        }
    }
}
