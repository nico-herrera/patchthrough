import Foundation
import Security

/// The trust boundary for a downloaded update, mirroring ModelIntegrity's
/// role for models: everything here runs after download and before the
/// bundle swap. The sidecar checksum travels over the same channel as the
/// DMG, so it only catches corruption and operator error. The signature,
/// team, Gatekeeper, and identity checks are what actually gate the swap,
/// and none of them can be relaxed in a release build.
enum UpdateVerifier {
    enum VerifyError: Error, CustomStringConvertible {
        case badSidecar
        case checksumMismatch
        case signatureInvalid(OSStatus)
        case wrongTeam(String?)
        case rejectedByGatekeeper
        case wrongBundleID(String?)
        case unreadableVersion
        case versionMismatch(bundle: String, announced: String)
        case notNewer(String)

        var description: String {
            switch self {
            case .badSidecar:
                return "The release checksum file is unreadable"
            case .checksumMismatch:
                return "The download does not match the release checksum"
            case .signatureInvalid(let status):
                return "The downloaded app's signature failed to verify (\(status))"
            case .wrongTeam(let team):
                return "The downloaded app is signed by \(team ?? "no team"), not the expected developer"
            case .rejectedByGatekeeper:
                return "Gatekeeper rejected the downloaded app"
            case .wrongBundleID(let id):
                return "The downloaded app is \(id ?? "unidentified"), not this app"
            case .unreadableVersion:
                return "The downloaded app has no readable version"
            case .versionMismatch(let bundle, let announced):
                return "The downloaded app is \(bundle) but the release says \(announced)"
            case .notNewer(let version):
                return "Version \(version) is not newer than this build"
            }
        }
    }

    /// Sidecar format: "<64 hex>  <filename>". The oldest releases carried
    /// a bare hash with no filename column, so only the first token counts.
    static func parseSidecar(_ text: String) -> String? {
        guard let token = text.split(whereSeparator: \.isWhitespace).first else { return nil }
        let hash = token.lowercased()
        guard hash.count == 64, hash.allSatisfy(\.isHexDigit) else { return nil }
        return hash
    }

    static func verifyChecksum(dmg: URL, sidecar: URL) throws {
        let text = try String(contentsOf: sidecar, encoding: .utf8)
        guard let expected = parseSidecar(text) else { throw VerifyError.badSidecar }
        guard try ModelIntegrity.sha256(dmg) == expected else {
            throw VerifyError.checksumMismatch
        }
    }

    /// Static-code validation pinned to one team: the signature must be
    /// valid under strict rules, chain to Apple's Developer ID anchor, and
    /// name `team` as the TeamIdentifier. Gatekeeper assessment follows,
    /// which is where notarization gets enforced.
    static func verifyCodeSignature(app: URL, team: String) throws {
        var staticCode: SecStaticCode?
        var status = SecStaticCodeCreateWithPath(app as CFURL, [], &staticCode)
        guard status == errSecSuccess, let code = staticCode else {
            throw VerifyError.signatureInvalid(status)
        }

        let requirementText =
            "anchor apple generic and certificate leaf[subject.OU] = \"\(team)\"" as CFString
        var requirement: SecRequirement?
        status = SecRequirementCreateWithString(requirementText, [], &requirement)
        guard status == errSecSuccess, let req = requirement else {
            throw VerifyError.signatureInvalid(status)
        }

        let flags = SecCSFlags(
            rawValue: kSecCSStrictValidate | kSecCSCheckAllArchitectures | kSecCSCheckNestedCode
        )
        status = SecStaticCodeCheckValidity(code, flags, req)
        guard status == errSecSuccess else { throw VerifyError.signatureInvalid(status) }

        var infoCF: CFDictionary?
        status = SecCodeCopySigningInformation(
            code, SecCSFlags(rawValue: kSecCSSigningInformation), &infoCF
        )
        guard status == errSecSuccess,
              let info = infoCF as? [String: Any],
              let signedTeam = info[kSecCodeInfoTeamIdentifier as String] as? String else {
            throw VerifyError.wrongTeam(nil)
        }
        guard signedTeam == team else { throw VerifyError.wrongTeam(signedTeam) }

        try assessWithGatekeeper(app: app)
    }

    private static func assessWithGatekeeper(app: URL) throws {
        #if DEBUG
        // Local iteration against un-notarized DMGs. Debug builds only;
        // a release binary has no way to skip this assessment.
        if ProcessInfo.processInfo.environment["PATCHTHROUGH_UPDATE_SKIP_GATEKEEPER"] == "1" {
            // Say so. A skipped check that stays quiet turns into a claim
            // the caller repeats ("verified and notarized") without ground.
            FileHandle.standardError.write(Data(
                "warning: skipping the Gatekeeper assessment (debug build, PATCHTHROUGH_UPDATE_SKIP_GATEKEEPER=1)\n".utf8
            ))
            return
        }
        #endif
        let task = Process()
        task.executableURL = URL(fileURLWithPath: "/usr/sbin/spctl")
        task.arguments = ["--assess", "--type", "execute", app.path]
        task.standardOutput = FileHandle.nullDevice
        task.standardError = FileHandle.nullDevice
        try task.run()
        task.waitUntilExit()
        guard task.terminationStatus == 0 else { throw VerifyError.rejectedByGatekeeper }
    }

    /// Identity predicates against the downloaded bundle's Info.plist.
    /// The bundle id must match the running build, which blocks installing
    /// the upstream app over the fork and the reverse. The version must
    /// equal what the release announced (nothing ties a tag to a plist at
    /// release time, so the client refuses the mismatch) and must be
    /// strictly newer than the running build (no downgrades, no replays).
    static func verifyIdentity(
        downloadedInfo: [String: Any],
        expectedBundleID: String,
        announced: SemVer,
        current: SemVer
    ) throws {
        let bundleID = downloadedInfo["CFBundleIdentifier"] as? String
        guard bundleID == expectedBundleID else {
            throw VerifyError.wrongBundleID(bundleID)
        }
        guard let raw = downloadedInfo["CFBundleShortVersionString"] as? String,
              let version = SemVer(raw) else {
            throw VerifyError.unreadableVersion
        }
        guard version == announced else {
            throw VerifyError.versionMismatch(
                bundle: version.description, announced: announced.description
            )
        }
        guard version > current else {
            throw VerifyError.notNewer(version.description)
        }
    }
}
