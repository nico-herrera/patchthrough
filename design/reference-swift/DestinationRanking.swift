import Foundation

/// Persisted per-destination use counts, so the split button's primary action
/// is whatever you actually use. This is the one piece of NEW behaviour in the
/// redesign — nothing in the app tracks this today.
struct DestinationRanking {
    private static let key = "handoff.useCounts"

    static func counts() -> [String: Int] {
        UserDefaults.standard.dictionary(forKey: key) as? [String: Int] ?? [:]
    }

    static func record(_ id: String) {
        var c = counts()
        c[id, default: 0] += 1
        UserDefaults.standard.set(c, forKey: key)
    }

    /// Most-used first. Ties preserve discovery order from Handoff.installedAgents().
    static func rank(_ dests: [SessionStore.Destination]) -> [SessionStore.Destination] {
        let c = counts()
        return dests.enumerated()
            .sorted { (c[$0.element.id] ?? 0, -$0.offset) > (c[$1.element.id] ?? 0, -$1.offset) }
            .map(\.element)
    }
}

// Call DestinationRanking.record(dest.id) at the END of SessionStore.send(_:to:),
// after a successful launch only. Cold start with no history falls back to the
// first installed terminal agent.
