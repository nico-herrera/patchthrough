import Foundation

/// A release version, parsed from a tag ("v1.6.0") or a bundle version
/// ("1.6.0"). Exactly three numeric components, nothing else: prerelease
/// suffixes are rejected on purpose. `/releases/latest` never returns a
/// prerelease, but a test feed can, and refusing one is safer than
/// inventing an ordering for it. "development" (a bare `swift build`
/// binary) also fails to parse, which is what keeps source builds from
/// ever self-updating.
struct SemVer: Comparable, Equatable, CustomStringConvertible, Sendable {
    let major: Int
    let minor: Int
    let patch: Int

    init?(_ raw: String) {
        var text = raw.trimmingCharacters(in: .whitespaces)
        if text.hasPrefix("v") || text.hasPrefix("V") {
            text = String(text.dropFirst())
        }
        let parts = text.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 3 else { return nil }
        let numbers = parts.map { part -> Int? in
            guard !part.isEmpty, part.allSatisfy(\.isNumber) else { return nil }
            return Int(part)
        }
        guard let major = numbers[0], let minor = numbers[1], let patch = numbers[2] else {
            return nil
        }
        self.major = major
        self.minor = minor
        self.patch = patch
    }

    static func < (lhs: SemVer, rhs: SemVer) -> Bool {
        if lhs.major != rhs.major { return lhs.major < rhs.major }
        if lhs.minor != rhs.minor { return lhs.minor < rhs.minor }
        return lhs.patch < rhs.patch
    }

    var description: String { "\(major).\(minor).\(patch)" }
}
