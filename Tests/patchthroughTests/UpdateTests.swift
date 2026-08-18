import Foundation
import Testing
@testable import patchthrough

// MARK: - SemVer

@Test func semVerParsesPlainAndTaggedForms() throws {
    let plain = try #require(SemVer("1.6.0"))
    #expect(plain.major == 1 && plain.minor == 6 && plain.patch == 0)
    #expect(SemVer("v1.6.0") == plain)
    #expect(SemVer(" 1.6.0 ") == plain)
}

@Test func semVerOrdersNumericallyNotLexically() throws {
    // 1.10.0 sorts after 1.9.0; a lexical compare would invert them.
    #expect(try #require(SemVer("1.9.0")) < #require(SemVer("1.10.0")))
    #expect(try #require(SemVer("1.6.0")) < #require(SemVer("1.7.0")))
    #expect(try #require(SemVer("1.7.0")) < #require(SemVer("2.0.0")))
    let same = try #require(SemVer("1.6.0"))
    #expect(!(same < same) && !(same > same))
}

// MARK: - Update feed

/// The shape /releases/latest actually returns, reduced to the fields the
/// updater decodes. The DMG name comes from UpdateSource, so this fixture
/// holds in the fork too, whose asset carries a different name. The Windows
/// asset is here so selection has to skip it.
private let dmgName = UpdateSource.dmgAssetName
private let feedFixture = """
{
  "tag_name": "v1.7.0",
  "draft": false,
  "prerelease": false,
  "assets": [
    {"id": 101, "name": "\(dmgName)", "size": 4600000,
     "browser_download_url": "https://example.invalid/releases/download/v1.7.0/\(dmgName)"},
    {"id": 102, "name": "\(dmgName).sha256", "size": 87,
     "browser_download_url": "https://example.invalid/releases/download/v1.7.0/\(dmgName).sha256"},
    {"id": 103, "name": "Patchthrough-windows-x64.zip", "size": 98000000,
     "browser_download_url": "https://example.invalid/releases/download/v1.7.0/Patchthrough-windows-x64.zip"}
  ]
}
"""

@Test func feedDecodesTheReleaseShape() throws {
    let release = try JSONDecoder().decode(UpdateRelease.self, from: Data(feedFixture.utf8))
    #expect(release.tagName == "v1.7.0")
    #expect(!release.draft && !release.prerelease)
    #expect(release.assets.count == 3)
    #expect(release.assets[0].id == 101)
    #expect(release.assets[0].browserDownloadURL.host() == "example.invalid")
}

@Test func feedSelectsTheDmgAndItsSidecarOnly() throws {
    let release = try JSONDecoder().decode(UpdateRelease.self, from: Data(feedFixture.utf8))
    let picked = try UpdateFeed.selectAssets(from: release)
    #expect(picked.dmg.name == dmgName)
    #expect(picked.sha.name == dmgName + ".sha256")
}

@Test func feedThrowsWhenAnAssetIsMissing() throws {
    // A release whose sidecar upload was forgotten must fail asset
    // selection, not sail on to an unverifiable download.
    let stripped = feedFixture.replacingOccurrences(
        of: dmgName + ".sha256", with: "wrong-name.sha256"
    )
    let release = try JSONDecoder().decode(UpdateRelease.self, from: Data(stripped.utf8))
    #expect(throws: UpdateFeed.FeedError.self) {
        try UpdateFeed.selectAssets(from: release)
    }
}

@Test func feedAssetURLDerivesFromTheFeedURL() {
    // Asset-by-id must live on the same API base as the feed, so a test
    // override redirects downloads too, not just the check.
    let url = UpdateSource.assetURL(id: 101)
    #expect(url?.absoluteString.hasSuffix("/releases/assets/101") == true)
}

// MARK: - Verifier

@Test func sidecarParsesBothHistoricalFormats() {
    let hash = String(repeating: "ab", count: 32)
    // shasum output: hash, two spaces, filename.
    #expect(UpdateVerifier.parseSidecar("\(hash)  Patchthrough-arm64.dmg\n") == hash)
    // The oldest releases shipped a bare hash with no filename column.
    #expect(UpdateVerifier.parseSidecar("\(hash)\n") == hash)
    #expect(UpdateVerifier.parseSidecar(hash.uppercased()) == hash)
    #expect(UpdateVerifier.parseSidecar("not a hash") == nil)
    #expect(UpdateVerifier.parseSidecar("abc123  file.dmg") == nil)
    #expect(UpdateVerifier.parseSidecar("") == nil)
}

@Test func checksumVerificationCatchesTampering() throws {
    let dir = FileManager.default.temporaryDirectory
        .appendingPathComponent("patchthrough-update-test-\(UUID().uuidString)", isDirectory: true)
    try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    defer { try? FileManager.default.removeItem(at: dir) }

    let dmg = dir.appendingPathComponent("fake.dmg")
    try Data("hello\n".utf8).write(to: dmg)
    let knownHash = "5891b5b522d5df086d0ff0b110fbd9d21bb4fc7163af34d08286a2e846f6be03"

    let good = dir.appendingPathComponent("good.sha256")
    try Data("\(knownHash)  fake.dmg\n".utf8).write(to: good)
    try UpdateVerifier.verifyChecksum(dmg: dmg, sidecar: good)

    let bad = dir.appendingPathComponent("bad.sha256")
    try Data("\(String(repeating: "00", count: 32))  fake.dmg\n".utf8).write(to: bad)
    #expect(throws: UpdateVerifier.VerifyError.self) {
        try UpdateVerifier.verifyChecksum(dmg: dmg, sidecar: bad)
    }
}

@Test func identityPredicatesGateTheSwap() throws {
    let current = try #require(SemVer("1.6.0"))
    let announced = try #require(SemVer("1.7.0"))
    func info(_ id: String, _ version: String) -> [String: Any] {
        ["CFBundleIdentifier": id, "CFBundleShortVersionString": version]
    }
    let mine = "com.example.patchthrough"

    // Happy path.
    try UpdateVerifier.verifyIdentity(
        downloadedInfo: info(mine, "1.7.0"),
        expectedBundleID: mine, announced: announced, current: current
    )
    // Another build's bundle must never install over this one, which is what
    // keeps the public app and the Fusion92 fork out of each other's way.
    #expect(throws: UpdateVerifier.VerifyError.self) {
        try UpdateVerifier.verifyIdentity(
            downloadedInfo: info("com.example.patchthrough.other", "1.7.0"),
            expectedBundleID: mine, announced: announced, current: current
        )
    }
    // A bundle that does not match the announced tag is refused: nothing
    // at release time ties the tag to the plist, so the client has to.
    #expect(throws: UpdateVerifier.VerifyError.self) {
        try UpdateVerifier.verifyIdentity(
            downloadedInfo: info(mine, "1.6.9"),
            expectedBundleID: mine, announced: announced, current: current
        )
    }
    // Downgrades are refused even when tag and plist agree.
    #expect(throws: UpdateVerifier.VerifyError.self) {
        try UpdateVerifier.verifyIdentity(
            downloadedInfo: info(mine, "1.5.0"),
            expectedBundleID: mine,
            announced: #require(SemVer("1.5.0")), current: current
        )
    }
    // No version at all.
    #expect(throws: UpdateVerifier.VerifyError.self) {
        try UpdateVerifier.verifyIdentity(
            downloadedInfo: ["CFBundleIdentifier": mine],
            expectedBundleID: mine, announced: announced, current: current
        )
    }
}

@Test func semVerRejectsWhatMustNeverInstall() {
    // "development" is the bare `swift build` fallback; a source build
    // must never parse as updatable.
    #expect(SemVer("development") == nil)
    // Prereleases: /releases/latest never serves one, so one appearing
    // means a test or hostile feed. Refuse rather than order it.
    #expect(SemVer("1.7.0-beta1") == nil)
    #expect(SemVer("1.7") == nil)
    #expect(SemVer("1.7.0.1") == nil)
    #expect(SemVer("") == nil)
    #expect(SemVer("abc") == nil)
    #expect(SemVer("1..0") == nil)
    #expect(SemVer("1.6.-1") == nil)
}

// MARK: - The janitor

@Test func theJanitorSparesWhatIsNotItsOwnLeftover() throws {
    let fm = FileManager.default
    let temp = fm.temporaryDirectory
    let parent = temp.appendingPathComponent("janitor-\(UUID().uuidString)", isDirectory: true)
    try fm.createDirectory(at: parent, withIntermediateDirectories: true)
    defer { try? fm.removeItem(at: parent) }
    let dest = parent.appendingPathComponent("patchthrough.app")
    try fm.createDirectory(at: dest, withIntermediateDirectories: true)

    // A leftover from a real swap, and a directory that merely starts the
    // same way. Only the first is the janitor's to remove.
    let leftover = parent.appendingPathComponent(".patchthrough.app.old-\(UUID().uuidString)")
    let lookalike = parent.appendingPathComponent(".patchthrough.app.old-notes")
    for url in [leftover, lookalike] {
        try fm.createDirectory(at: url, withIntermediateDirectories: true)
    }

    // A download directory in the shape the installer makes. It is new, so a
    // concurrent updater could still be verifying it: it must survive.
    let liveDownload = temp.appendingPathComponent(UpdateInstaller.downloadDirectoryName())
    try fm.createDirectory(at: liveDownload, withIntermediateDirectories: true)
    defer { try? fm.removeItem(at: liveDownload) }

    // The same shape, but stale, so nobody is using it.
    let staleDownload = temp.appendingPathComponent(UpdateInstaller.downloadDirectoryName())
    try fm.createDirectory(at: staleDownload, withIntermediateDirectories: true)
    try fm.setAttributes(
        [.modificationDate: Date().addingTimeInterval(-3 * 60 * 60)], ofItemAtPath: staleDownload.path
    )
    defer { try? fm.removeItem(at: staleDownload) }

    UpdateInstaller.cleanupLeftovers(near: dest)

    #expect(!fm.fileExists(atPath: leftover.path))
    #expect(fm.fileExists(atPath: lookalike.path))
    #expect(fm.fileExists(atPath: liveDownload.path))
    #expect(!fm.fileExists(atPath: staleDownload.path))
    #expect(fm.fileExists(atPath: dest.path))
}

// MARK: - Mount and verify against a real release image

/// Runs only when a release DMG is present at PATCHTHROUGH_TEST_DMG. It
/// mounts the image, verifies the signature, team, and Gatekeeper status,
/// and reads the bundle version back: the whole trust boundary except the
/// swap. CI has no signed DMG, so the test skips there rather than failing.
@Test func mountAndVerifyARealReleaseImage() throws {
    guard let path = ProcessInfo.processInfo.environment["PATCHTHROUGH_TEST_DMG"],
          FileManager.default.fileExists(atPath: path) else { return }
    let mounted = try UpdateInstaller.mount(dmg: URL(fileURLWithPath: path))
    defer { UpdateInstaller.detach(mounted) }

    // Not under /Volumes: -mountrandom keeps the image out of Finder.
    #expect(!mounted.point.path.hasPrefix("/Volumes/"))
    #expect(mounted.app.pathExtension == "app")
    try UpdateVerifier.verifyCodeSignature(app: mounted.app, team: UpdateSource.expectedTeamID)

    let info = try #require(PropertyListSerialization.propertyList(
        from: Data(contentsOf: mounted.app.appendingPathComponent("Contents/Info.plist")),
        format: nil) as? [String: Any])
    let raw = try #require(info["CFBundleShortVersionString"] as? String)
    let version = try #require(SemVer(raw))
    let floor = try #require(SemVer("1.0.0"))
    #expect(version > floor)

    // A wrong team must fail even though the signature itself is valid.
    #expect(throws: UpdateVerifier.VerifyError.self) {
        try UpdateVerifier.verifyCodeSignature(app: mounted.app, team: "WRONGTEAM1")
    }
}

// MARK: - What Settings shows

/// Settings is the only place a user can read their version and reach a
/// check, so every state has to produce a line, and the busy ones must not
/// offer a button that does nothing.
@Test @MainActor func settingsDescribesEveryUpdateState() throws {
    let version = try #require(SemVer("1.7.0"))
    func shown(_ state: UpdateController.State) -> SettingsUpdateDisplay {
        SettingsUpdateDisplay(
            state: state, releaseVersion: "1.6.0", hasFeed: true, lastChecked: nil
        )
    }
    let cases: [(UpdateController.State, String, String?)] = [
        (.idle, "Not checked yet", "Check now"),
        (.checking, "Checking for updates…", nil),
        (.available(version), "Version 1.7.0 is ready to install", "Install 1.7.0"),
        (.downloading, "Downloading the update…", nil),
        (.verifying, "Checking the download's signature…", nil),
        (.installing, "Installing…", nil),
        (.waitingForRecordingEnd(version), "Version 1.7.0 installs after this recording", nil),
        (.manualInstall(version), "Version 1.7.0 is downloaded and waiting in Finder", "Show in Finder"),
        (.failed("The download does not match the release checksum"),
         "The download does not match the release checksum", "Check now"),
    ]
    for (state, line, action) in cases {
        let display = shown(state)
        #expect(display.statusLine == line)
        #expect(display.actionTitle == action)
        // Design rules: a capital first letter, and no em dash anywhere a
        // user reads. Both are easy to break in a later edit.
        #expect(display.statusLine.first?.isUppercase == true)
        #expect(!display.statusLine.contains("\u{2014}"))
    }
    #expect(shown(.idle).versionTitle == "Version 1.6.0")
}

/// A build that cannot update says so and offers no button, rather than
/// inviting a check that would report nothing.
@Test @MainActor func settingsHidesTheButtonWhenNothingCanUpdate() {
    let sourceBuild = SettingsUpdateDisplay(
        state: .idle, releaseVersion: "development", hasFeed: true, lastChecked: nil
    )
    #expect(sourceBuild.versionTitle == "Source build")
    #expect(sourceBuild.statusLine == "Updates do not apply to a build made from source")
    #expect(sourceBuild.actionTitle == nil)

    // The Fusion92 build until its feed exists.
    let noFeed = SettingsUpdateDisplay(
        state: .idle, releaseVersion: "1.6.0", hasFeed: false, lastChecked: nil
    )
    #expect(noFeed.statusLine == "This build has no update feed")
    #expect(noFeed.actionTitle == nil)
}

// MARK: - How the updater is allowed to schedule

/// Upstream enters `NSApplication.run()` from inside a work item on the main
/// queue, which blocks that queue for the life of the process: a MainActor
/// `Task` or a `DispatchQueue.main.async` enqueued afterwards never executes.
/// The Fusion92 fork fixed the cause in its entry point, but this file is
/// shared, so it keeps the shape that works in both.
///
/// This is a source check rather than a behaviour test because the failure is
/// invisible: the wrong code compiles, reads correctly, and silently does
/// nothing. The first version of the updater scheduled that way and checked
/// for updates exactly never, while every unit test and the CLI passed,
/// because the CLI has no blocked queue.
@Test func theUpdaterNeverSchedulesOnTheBlockedMainQueue() throws {
    let controller = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent()   // patchthroughTests
        .deletingLastPathComponent()   // Tests
        .deletingLastPathComponent()   // repo root
        .appendingPathComponent("Sources/patchthrough/Update/UpdateController.swift")
    let source = try String(contentsOf: controller, encoding: .utf8)
    // Comments name these APIs on purpose, to warn the next reader off them,
    // so only real code counts.
    let code = source
        .split(separator: "\n", omittingEmptySubsequences: false)
        .filter { !$0.trimmingCharacters(in: .whitespaces).hasPrefix("//") }
        .joined(separator: "\n")

    #expect(!code.contains("DispatchQueue.main.async"))
    #expect(!code.contains("MainActor.run("))
    // `Task.detached` is the allowed form, so this looks for the bare one.
    #expect(!code.contains("Task {"))
    #expect(!code.contains("Task { @MainActor"))
    // And the two mechanisms that do work must still be the ones in use.
    #expect(code.contains("RunLoop.main.perform"))
    #expect(code.contains("Task.detached"))
}

// MARK: - What the window strip shows

/// The strip is the only update surface a user sees without opening a menu or
/// the settings sheet, so it has to speak for every state it appears in, and
/// stay hidden for the ones it should not.
@Test @MainActor func theWindowStripSpeaksForEveryStateItShows() throws {
    let version = try #require(SemVer("1.7.1"))

    // Nothing to say, so nothing on screen.
    #expect(UpdateBannerDisplay(state: .idle) == nil)
    #expect(UpdateBannerDisplay(state: .checking) == nil)

    let cases: [(UpdateController.State, String, String?)] = [
        (.available(version), "Version 1.7.1 is ready to install", "Install"),
        (.downloading, "Downloading the update…", nil),
        (.verifying, "Checking the download's signature…", nil),
        (.installing, "Installing the update. Patchthrough will restart", nil),
        (.waitingForRecordingEnd(version), "Version 1.7.1 installs after this recording", nil),
        (.manualInstall(version),
         "Version 1.7.1 is downloaded. Drag Patchthrough to Applications to finish", "Show in Finder"),
        // A failure stays on screen: a strip that vanished after a click would
        // leave the user guessing.
        (.failed("The download does not match the release checksum"),
         "The download does not match the release checksum. The current version keeps running",
         "Try again"),
    ]
    for (state, message, action) in cases {
        let shown = try #require(UpdateBannerDisplay(state: state))
        #expect(shown.message == message)
        #expect(shown.actionTitle == action)
        // Design rules: a capital first letter, and no em dash in anything a
        // user reads.
        #expect(shown.message.first?.isUppercase == true)
        #expect(!shown.message.contains("\u{2014}"))
    }
}
