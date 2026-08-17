import Foundation

extension UpdateSource {
    /// True when this build can reach a feed at all. `isConfigured` is the
    /// declared answer, and packaging scripts read it out of the source. A
    /// `PATCHTHROUGH_UPDATE_FEED` override also counts, so a build whose
    /// channel does not exist yet can still be tested against a local one.
    /// Pointing at a feed never relaxes what UpdateVerifier requires.
    static var hasFeed: Bool {
        isConfigured || ProcessInfo.processInfo.environment["PATCHTHROUGH_UPDATE_FEED"] != nil
    }
}

/// One release from the GitHub API, reduced to what the updater needs.
struct UpdateRelease: Decodable, Sendable {
    struct Asset: Decodable, Sendable {
        let id: Int
        let name: String
        let size: Int
        let browserDownloadURL: URL

        enum CodingKeys: String, CodingKey {
            case id, name, size
            case browserDownloadURL = "browser_download_url"
        }
    }

    let tagName: String
    let draft: Bool
    let prerelease: Bool
    let assets: [Asset]

    enum CodingKeys: String, CodingKey {
        case tagName = "tag_name"
        case draft, prerelease, assets
    }
}

/// GitHub release-feed client. Every function is a plain async call so the
/// CLI can use it without an NSApplication.
enum UpdateFeed {
    enum FeedError: Error, CustomStringConvertible {
        case http(Int)
        case rateLimited
        case unauthorized
        case noAsset(String)

        var description: String {
            switch self {
            case .http(let code): return "The update feed answered HTTP \(code)"
            case .rateLimited: return "The update feed is rate limited"
            case .unauthorized: return "The update feed rejected this build's credentials"
            case .noAsset(let name): return "The release has no asset named \(name)"
            }
        }
    }

    /// GET the feed. Returns nil on 304 (nothing changed since `etag`).
    static func latest(etag: String?) async throws -> (release: UpdateRelease, etag: String?)? {
        var request = URLRequest(url: UpdateSource.feedURL)
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        request.setValue("2022-11-28", forHTTPHeaderField: "X-GitHub-Api-Version")
        if let token = UpdateSource.feedToken {
            request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        }
        if let etag {
            request.setValue(etag, forHTTPHeaderField: "If-None-Match")
        }

        let (data, response) = try await URLSession.shared.data(for: request)
        let http = response as? HTTPURLResponse
        switch http?.statusCode ?? 0 {
        case 200:
            break
        case 304:
            return nil
        case 401, 404:
            // A private feed answers 404, not 401, to a bad or expired
            // token; both mean this build can no longer see updates.
            throw FeedError.unauthorized
        case 403 where http?.value(forHTTPHeaderField: "x-ratelimit-remaining") == "0":
            throw FeedError.rateLimited
        case let code:
            throw FeedError.http(code)
        }
        let release = try JSONDecoder().decode(UpdateRelease.self, from: data)
        return (release, http?.value(forHTTPHeaderField: "ETag"))
    }

    /// Picks the DMG and its .sha256 sidecar out of a release.
    static func selectAssets(
        from release: UpdateRelease
    ) throws -> (dmg: UpdateRelease.Asset, sha: UpdateRelease.Asset) {
        func asset(_ name: String) throws -> UpdateRelease.Asset {
            guard let found = release.assets.first(where: { $0.name == name }) else {
                throw FeedError.noAsset(name)
            }
            return found
        }
        let name = UpdateSource.dmgAssetName
        return (try asset(name), try asset(name + ".sha256"))
    }

    /// Downloads an asset into `dir` and returns the file URL. Anonymous
    /// feeds use the asset's browser URL. A token feed must use the
    /// asset-by-id API endpoint instead: browser URLs on a private repo
    /// answer 404 to everyone.
    static func download(_ asset: UpdateRelease.Asset, to dir: URL) async throws -> URL {
        var request: URLRequest
        if UpdateSource.feedToken != nil, let byID = UpdateSource.assetURL(id: asset.id) {
            request = URLRequest(url: byID)
            request.setValue("application/octet-stream", forHTTPHeaderField: "Accept")
            request.setValue("Bearer \(UpdateSource.feedToken!)", forHTTPHeaderField: "Authorization")
        } else {
            request = URLRequest(url: asset.browserDownloadURL)
        }

        let (temp, response) = try await URLSession.shared.download(
            for: request, delegate: AuthStrippingRedirects()
        )
        guard let code = (response as? HTTPURLResponse)?.statusCode, code == 200 else {
            throw FeedError.http((response as? HTTPURLResponse)?.statusCode ?? 0)
        }
        let destination = dir.appendingPathComponent(asset.name)
        try? FileManager.default.removeItem(at: destination)
        try FileManager.default.moveItem(at: temp, to: destination)
        return destination
    }
}

/// Strips Authorization when a redirect leaves the original host. GitHub
/// 302s asset downloads to objects.githubusercontent.com, which rejects a
/// request that still carries the Bearer header: the redirect URL brings
/// its own signature, and URLSession forwards the header by default.
private final class AuthStrippingRedirects: NSObject, URLSessionTaskDelegate, @unchecked Sendable {
    func urlSession(
        _ session: URLSession,
        task: URLSessionTask,
        willPerformHTTPRedirection response: HTTPURLResponse,
        newRequest request: URLRequest
    ) async -> URLRequest? {
        var next = request
        if request.url?.host() != task.originalRequest?.url?.host() {
            next.setValue(nil, forHTTPHeaderField: "Authorization")
        }
        return next
    }
}
