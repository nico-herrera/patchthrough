# Patchthrough — app redesign handoff

Redesign of the main window, settings sheet and menu bar dropdown.
**Design source of truth:** `Patchthrough App Redesign.dc.html` — round **11a** (window),
**10d** (settings), **10e** (menu bar). Rounds 10a–10c are rejected alternates; ignore them.

Logo/mark specs live in `CLAUDE_CODE_HANDOFF.md`. Nothing here changes the mark.

Files this touches:
`Sources/patchthrough/UI/PatchthroughWindow.swift`, `UI/MenuBarController.swift`,
and one new value type for destination ranking.

---

## 0. What changed, in one list

| Change | Why |
| --- | --- |
| The 10-button destination grid is gone | The README promises one click; a flat grid of ten makes every handoff a decision |
| Replaced by a **split button in the toolbar**, ranked by use | Primary action for the 90% case, menu for the rest |
| Bottom handoff bar deleted entirely | Returns ~150pt of height to the transcript |
| Transcript is **me right / them left** | Two-track diarization is the only structure the transcript has; use it |
| **All left-edge accent bars removed** | Replaced by filled selection and by side-position for speakers |
| Sidebar rows are **time + first transcript line** | "9:45 PM" doesn't tell you which meeting |
| **Refresh** button removed | The list already reloads on `refresh()`; wire it to an FSEvents watcher instead |
| **Record** button removed from the window | Recording starts in the menu bar; the window is for review |
| Verb is **"Patch through to"** everywhere | Was "Hand off to" |
| Settings sheet: 4 sections, no scrolling | The old 540×560 sheet clipped its last section |

---

## 1. Palette

Dark only. Warm neutrals derived from the brand Ink/Paper, one accent.

```swift
extension Color {
    static let ptWindow    = Color(red: 0x1C/255, green: 0x1B/255, blue: 0x17/255) // #1C1B17
    static let ptSidebar   = Color(red: 0x19/255, green: 0x18/255, blue: 0x13/255) // #191813
    static let ptRaised    = Color(red: 0x24/255, green: 0x23/255, blue: 0x1D/255) // #24231D
    static let ptSurface   = Color(red: 0x21/255, green: 0x1E/255, blue: 0x1A/255) // #211E1A  me-turn ground
    static let ptHairline  = Color(red: 0x2C/255, green: 0x2A/255, blue: 0x23/255) // #2C2A23
    static let ptBorder    = Color(red: 0x3A/255, green: 0x37/255, blue: 0x30/255) // #3A3730
    static let ptText      = Color(red: 0xF2/255, green: 0xF0/255, blue: 0xEA/255) // #F2F0EA
    static let ptText2     = Color(red: 0xD8/255, green: 0xD4/255, blue: 0xCA/255) // #D8D4CA
    static let ptText3     = Color(red: 0xA2/255, green: 0x9E/255, blue: 0x93/255) // #A29E93
    static let ptText4     = Color(red: 0x6E/255, green: 0x6B/255, blue: 0x60/255) // #6E6B60
    static let ptSignal    = Color(red: 0xD2/255, green: 0x37/255, blue: 0x1B/255) // #D2371B
    static let ptSignalLit = Color(red: 0xE4/255, green: 0x63/255, blue: 0x3F/255) // #E4633F  on dark text
}
```

Set `.tint(.ptSignal)` once at the window root — `List` selection, `borderedProminent`
buttons and toggles all inherit it, which is most of the accent work done for free.

### Where red is allowed

The primary patch button, the selected session row, the `me` speaker tag, and recording
state. **Nothing else.** `them` stays neutral on purpose — giving it a second colour spends
the one accent you have. Force dark: `.preferredColorScheme(.dark)`.

---

## 2. Window — round 11a

`NavigationSplitView`, sidebar 252pt, no bottom bar. Frame stays `minWidth: 860, minHeight: 660`.

### Toolbar

Left: mark + "Patchthrough". Right: **Drag** chip, the split button, gear.
The recording pill and Copy/Refresh/Folder buttons come out.

```swift
ToolbarItemGroup {
    // Drag chip — keep the existing .onDrag payload from dragHeader()
    Button { } label: { Label("Drag", systemImage: "arrow.up.doc.on.clipboard") }
        .onDrag { NSItemProvider(contentsOf: store.dragFile(for: item)) ?? NSItemProvider() }

    // Split button: primaryAction fires the top-ranked destination
    Menu {
        Section("Most used") { /* top 3, with use counts */ }
        Section("Terminal")  { /* remaining CLI agents */ }
        Section("App")       { /* remaining GUI targets */ }
    } label: {
        Label("Patch through to \(store.topDestination?.shortLabel ?? "…")",
              systemImage: "arrow.right")
    } primaryAction: {
        if let d = store.topDestination { store.send(item, to: d) }
    }
    .buttonStyle(.borderedProminent)
    .disabled(store.selected?.status != .ready)

    Button { store.showSettings = true } label: { Label("Settings", systemImage: "gearshape") }
        .keyboardShortcut(",", modifiers: .command)
}
```

Keep `⌘⇧C` for Copy as a **menu-only** command (`.commands`) — it doesn't need a toolbar slot.

### Sidebar rows

Two lines. Row 1: time (semibold) + duration (mono, secondary). Row 2: the first transcript
line, single-line truncated. Sections stay Today / Yesterday / weekday / date as
`groupedItems` already produces.

`SessionStore.Item` needs one addition:

```swift
/// First thing said — the only human-readable identifier a session has.
var firstLine: String { segments.first?.text ?? "" }
```

Row body:

```swift
VStack(alignment: .leading, spacing: 3) {
    HStack(alignment: .firstTextBaseline, spacing: 7) {
        Text(timeLabel(item)).font(.system(size: 13, weight: .semibold))
        Text(item.duration).font(.system(size: 11, design: .monospaced))
            .foregroundStyle(.secondary)
    }
    Text(item.firstLine)
        .font(.system(size: 12))
        .foregroundStyle(.secondary)
        .lineLimit(1)
        .truncationMode(.tail)
}
.padding(.vertical, 3)
.tag(item.id)
```

**Selection is a filled row, not an edge bar.** Native `List(selection:)` with
`.tint(.ptSignal)` gives exactly the specced look; do not draw a leading rectangle.
Pending/broken sessions keep their existing status glyph and `subtitle`.

### Transcript — the important part

Turns are grouped by consecutive speaker (`them` blocks in the mock hold two utterances).
Layout rules, all four load-bearing:

1. `me` → `.frame(alignment: .trailing)`, on a `Color.ptSurface` ground, `cornerRadius 11`, padding 13/16.
2. `them` → leading, **no ground, no border** — bare on the window colour.
3. Both wrappers cap at **78% of the column width**. This is the one that breaks if you
   change it: trailing alignment can only offset an item narrower than the line, so a
   larger cap makes both speakers span the full column and the left/right rule vanishes.
4. Body text is **left-aligned in both**. Right-aligned paragraphs read measurably slower.

Speaker header above each turn: `me` shows `0:00` then `ME` (trailing edge);
`them` shows `THEM` then `0:00` (leading edge). Labels are 10.5pt semibold, tracking
0.09em, uppercase — `.ptSignalLit` for `me`, `#8C887E` for `them`. Times are 10.5pt mono.

```swift
ForEach(groupedTurns(item.segments)) { turn in
    let isMe = turn.speaker == "me"
    VStack(alignment: isMe ? .trailing : .leading, spacing: isMe ? 7 : 8) {
        HStack(spacing: 8) {
            if isMe { Text(turn.time).monoCaption() }
            Text(turn.speaker.uppercased())
                .font(.system(size: 10.5, weight: .semibold))
                .tracking(0.9)
                .foregroundStyle(isMe ? Color.ptSignalLit : Color(hex: 0x8C887E))
            if !isMe { Text(turn.time).monoCaption() }
        }
        ForEach(turn.lines, id: \.self) { line in
            Text(line)
                .font(.system(size: 14.5))
                .lineSpacing(4)
                .foregroundStyle(isMe ? Color.ptText : Color.ptText2)
                .textSelection(.enabled)
                .multilineTextAlignment(.leading)
                .padding(isMe ? EdgeInsets(top: 13, leading: 16, bottom: 13, trailing: 16) : EdgeInsets())
                .background(isMe ? Color.ptSurface : .clear,
                            in: RoundedRectangle(cornerRadius: 11))
        }
    }
    .frame(maxWidth: .infinity, alignment: isMe ? .trailing : .leading)
    // 78% cap — see rule 3
    .padding(isMe ? .leading : .trailing, 22)
}
```

Keep the existing search-term highlighting (`highlighted(_:)`) — it still applies per line.

### Detail header

One row: session id (mono), `0m42s · 118 words · 7 segments`, and the target repo as
**just the folder name** (`patchthrough`, not `~/Developer/patchthrough`) on the trailing
edge. The full path belongs in the tooltip. This replaces the old `Project` text field —
picking a repo moves into `pickRepo()` triggered from that chip.

---

## 3. Ranked destinations (new behaviour)

The only real logic addition. Persist a use count per destination id and sort by it.

```swift
/// Destination use counts, so the split button's primary action is whatever
/// you actually use. Keyed by Destination.id ("cli:claude", "gui:claude-cowork").
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
    /// Installed destinations, most-used first; ties keep discovery order.
    static func rank(_ dests: [SessionStore.Destination]) -> [SessionStore.Destination] {
        let c = counts()
        return dests.enumerated()
            .sorted { (c[$0.element.id] ?? 0, -$0.offset) > (c[$1.element.id] ?? 0, -$1.offset) }
            .map(\.element)
    }
}
```

Call `DestinationRanking.record(dest.id)` at the end of `SessionStore.send(_:to:)`, and
expose `var topDestination: Destination? { DestinationRanking.rank(destinations).first }`.

Menu structure: **Most used** (top 3, each with a trailing `14×` count), then **Terminal**,
then **App** for the remainder. Never show a count of 0 — omit the suffix instead.

Cold start (no history): fall back to the first installed terminal agent, matching today's
discovery order in `Handoff.installedAgents()`.

---

## 4. Settings sheet — round 10d

Fixed frame, **no scrolling**: `width 560`, height fits the four sections. Four groups:
**Recordings**, **Transcription**, **Patch through**, **After each transcript**.

Two changes beyond styling:

1. Every toggle gets a one-line subtitle stating the tradeoff, instead of a grey footnote
   below the whole section: "On-device, ~20s per hour of audio" /
   "Cleaner on speakers, thinner on headphones" / "Types ⌘N then ⌘V. Never presses send."
2. Auto-paste's Accessibility requirement becomes an **inline strip attached to that row**,
   tinted `rgba(210,55,27,0.10)`, with a **Grant now** action that opens
   `x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility`.
   It currently sits in small print where nobody connects it to the switch.

Footer: "Reveal config file" (leading), Cancel, **Save** (`borderedProminent`).
Header carries the mark and `~/.config/patchthrough/config.json` on the trailing edge, so
the file being edited is never a mystery. `save()` logic is unchanged — keep writing only
non-default keys.

---

## 5. Menu bar — round 10e

`MenuBarController`'s status-item art needs **no change**: idle regular (1.6), recording
regular + pulsing Signal dot, patching heavy (2.1). That's already correct.

The menu changes:

```
┌──────────────────────────────┐
│ ● Idle · 2 sessions          │  state header, disabled
├──────────────────────────────┤
│ ● Start recording        ⌘R  │  ← leads with the verb
├──────────────────────────────┤
│ ⌨ Patch 9:45 PM to claude    │  ← NEW: top-ranked, one click, no submenu
│   Patch through to        ▸  │
├──────────────────────────────┤
│   Open Patchthrough…     ⌘B  │
│   Recordings folder      ⌘O  │
├──────────────────────────────┤
│   Quit                   ⌘Q  │
└──────────────────────────────┘
```

While recording, the header goes `● Recording  1:04` (Signal, monospaced digits), the
toggle becomes **Stop & transcribe** with a subtitle line "mic + system audio · 2 tracks",
and **Patch through to** disables — there's nothing finished to patch.

Rename: `handoffItem.title` becomes `"Patch through to"`, and the promoted item is
`"Patch \(sessionTimeLabel) to \(topAgent)"`. Keep `representedObject` as
`"cli:<name>"` / `"gui:<id>"`; the existing `onHandoff` handler is fine.

---

## 6. Not designed yet

Ask before inventing these — they're real states with no mock:

- **Empty / first run** (no sessions). Today it's a `waveform.badge.mic` placeholder.
- **Transcribing** (`status == .pending`) and **interrupted** (`.broken`) detail panes.
- **Light mode.** The palette above is dark-only by decision; a light build needs its own pass.
- **Search results** styling with the new row layout.
- Recording state *inside* the window — 11a assumes you start from the menu bar, so the
  window has no recording affordance at all. If that turns out wrong in use, the state
  header pattern from 10e is the place to start.
