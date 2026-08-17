import Foundation

/// The check-download-verify-install sequence shared by the menu-bar
/// updater and the CLI. The pieces live in UpdateFeed, UpdateVerifier,
/// and UpdateInstaller; this file only orders them.
enum UpdatePipeline {
    enum PipelineError: Error, CustomStringConvertible {
        case insufficientSpace

        var description: String {
            "There is not enough free disk space for the update"
        }
    }

    struct Available: Sendable {
        let release: UpdateRelease
        let version: SemVer
        let etag: String?
    }

    enum CheckResult: Sendable {
        /// 304: nothing changed since the caller's ETag.
        case unchanged
        case upToDate
        case available(Available)
    }

    static func check(current: SemVer, etag: String?) async throws -> CheckResult {
        guard let (release, newETag) = try await UpdateFeed.latest(etag: etag) else {
            return .unchanged
        }
        // /releases/latest never serves drafts or prereleases; seeing one
        // means a test feed, and it does not count as an update.
        guard !release.draft, !release.prerelease,
              let version = SemVer(release.tagName), version > current else {
            return .upToDate
        }
        return .available(Available(release: release, version: version, etag: newETag))
    }

    struct Staged: Sendable {
        let version: SemVer
        let tempDir: URL
        let dmg: URL
        let mounted: UpdateInstaller.Mounted
    }

    /// Downloads the DMG and its sidecar, checks the checksum, mounts,
    /// and runs the whole trust boundary. On success the image stays
    /// mounted for the swap: the caller finishes with install(_:into:)
    /// or abort(_:).
    static func downloadAndVerify(
        _ available: Available, current: SemVer, expectedBundleID: String
    ) async throws -> Staged {
        let assets = try UpdateFeed.selectAssets(from: available.release)
        let fm = FileManager.default
        let tempDir = fm.temporaryDirectory
            .appendingPathComponent(UpdateInstaller.downloadDirectoryName(), isDirectory: true)
        try fm.createDirectory(at: tempDir, withIntermediateDirectories: true)
        do {
            try requireFreeSpace(bytes: Int64(assets.dmg.size) * 3, at: tempDir)
            let dmg = try await UpdateFeed.download(assets.dmg, to: tempDir)
            let sidecar = try await UpdateFeed.download(assets.sha, to: tempDir)
            try UpdateVerifier.verifyChecksum(dmg: dmg, sidecar: sidecar)
            let mounted = try UpdateInstaller.mount(dmg: dmg)
            do {
                try UpdateVerifier.verifyCodeSignature(
                    app: mounted.app, team: UpdateSource.expectedTeamID
                )
                let infoURL = mounted.app.appendingPathComponent("Contents/Info.plist")
                let info = try PropertyListSerialization.propertyList(
                    from: Data(contentsOf: infoURL), format: nil
                ) as? [String: Any] ?? [:]
                try UpdateVerifier.verifyIdentity(
                    downloadedInfo: info,
                    expectedBundleID: expectedBundleID,
                    announced: available.version,
                    current: current
                )
                return Staged(
                    version: available.version, tempDir: tempDir, dmg: dmg, mounted: mounted
                )
            } catch {
                UpdateInstaller.detach(mounted)
                throw error
            }
        } catch {
            try? fm.removeItem(at: tempDir)
            throw error
        }
    }

    /// Swaps the bundle and re-registers it, then detaches and removes the
    /// download. Relaunching is the caller's move: the CLI has no process
    /// to restart, the app does.
    static func install(_ staged: Staged, into dest: URL) throws {
        defer {
            UpdateInstaller.detach(staged.mounted)
            try? FileManager.default.removeItem(at: staged.tempDir)
        }
        try requireFreeSpace(
            bytes: Int64(staged.dmg.fileSize ?? 0) * 3, at: dest.deletingLastPathComponent()
        )
        try UpdateInstaller.swap(verifiedApp: staged.mounted.app, into: dest)
        UpdateInstaller.reregister(dest)
    }

    static func abort(_ staged: Staged) {
        UpdateInstaller.detach(staged.mounted)
        try? FileManager.default.removeItem(at: staged.tempDir)
    }

    private static func requireFreeSpace(bytes: Int64, at url: URL) throws {
        let values = try? url.resourceValues(
            forKeys: [.volumeAvailableCapacityForImportantUsageKey]
        )
        if let free = values?.volumeAvailableCapacityForImportantUsage, free < bytes {
            throw PipelineError.insufficientSpace
        }
    }
}

private extension URL {
    var fileSize: Int? {
        (try? resourceValues(forKeys: [.fileSizeKey]))?.fileSize
    }
}
