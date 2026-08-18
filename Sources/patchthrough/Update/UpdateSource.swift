import Foundation

/// Where updates come from and how this build behaves about them.
///
/// The Fusion92 fork replaces this file wholesale, the same way it adds
/// Brand.swift: one file carries every constant that distinguishes the
/// builds, so the merge conflict here stays mechanical. Keep every
/// `static let` on one line with the literal in double quotes; packaging
/// scripts sed values out of this file.
enum UpdateSource {
    /// False when this build has no release channel to poll. The fork ships
    /// that way until its releases repository and token exist: a build that
    /// cannot reach a feed should say so, rather than report a failing check
    /// on every launch.
    static let isConfigured = true

    /// GitHub repo slug the release feed reads from.
    static let feedRepo = "nico-herrera/patchthrough"

    /// Fine-grained read-only PAT for a private feed. Nil means anonymous.
    static let feedToken: String? = nil

    /// Whether Settings offers a toggle. False means always on (the fork).
    static let allowsDisabling = true

    /// DMG asset name in each release. Must match make-dist.sh output.
    static let dmgAssetName = "Patchthrough-arm64.dmg"

    /// TeamIdentifier the downloaded bundle must be signed with. If this
    /// team ever changes, the transition release must be signed by the
    /// old team while carrying the new constant here, or every existing
    /// install refuses the update that would have moved it over.
    static let expectedTeamID = "U3W37KR29G"

    /// The release feed. `PATCHTHROUGH_UPDATE_FEED` overrides it for
    /// testing against a scratch repo or a local server. The override
    /// changes what is offered, never what is accepted: UpdateVerifier
    /// still requires this team's signature, this bundle id, and a newer
    /// version, so the worst a hostile feed can deliver is a genuine,
    /// newer Patchthrough build.
    static var feedURL: URL {
        if let raw = ProcessInfo.processInfo.environment["PATCHTHROUGH_UPDATE_FEED"],
           let url = URL(string: raw), url.scheme == "https" || url.scheme == "http" {
            return url
        }
        return URL(string: "https://api.github.com/repos/\(feedRepo)/releases/latest")!
    }

    /// Asset-by-id endpoint for authenticated downloads, derived from the
    /// feed URL so the test override redirects the whole pipeline. Nil when
    /// the feed URL has no `/releases/latest` suffix to derive from; the
    /// caller falls back to the asset's browser URL.
    static func assetURL(id: Int) -> URL? {
        let feed = feedURL.absoluteString
        let suffix = "/releases/latest"
        guard feed.hasSuffix(suffix) else { return nil }
        return URL(string: feed.dropLast(suffix.count) + "/releases/assets/\(id)")
    }
}
