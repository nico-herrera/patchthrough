import SwiftUI

/// Toolbar split button. Primary half fires the top-ranked destination;
/// the chevron half opens the full ranked menu. Replaces the old grid of
/// ten equal-weight destination buttons.
struct PatchThroughButton: View {
    @ObservedObject var store: SessionStore

    var body: some View {
        Menu {
            Section("Most used") {
                ForEach(store.rankedDestinations.prefix(3)) { d in
                    Button { store.send(to: d) } label: {
                        Label(d.label, systemImage: d.isCLI ? "terminal" : "app")
                        if let n = store.useCount(d), n > 0 { Text("\(n)×") }
                    }
                }
            }
            Section("Terminal") {
                ForEach(store.rankedDestinations.dropFirst(3).filter(\.isCLI)) { d in
                    Button(d.label) { store.send(to: d) }
                }
            }
            Section("App") {
                ForEach(store.rankedDestinations.dropFirst(3).filter { !$0.isCLI }) { d in
                    Button(d.label) { store.send(to: d) }
                }
            }
        } label: {
            Label("Patch through to \(store.topDestination?.shortLabel ?? "…")",
                  systemImage: "arrow.right")
        } primaryAction: {
            if let d = store.topDestination { store.send(to: d) }
        }
        .menuStyle(.borderlessButton)
        .buttonStyle(.borderedProminent)
        .tint(PT.C.signal)
        .disabled(store.selected?.status != .ready)
        .help("Patch this session through to your agent")
    }
}
