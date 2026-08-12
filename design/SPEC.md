# Exact-value spec

The code in this folder IS the spec. Every number below already appears in
`Theme.swift` — this table exists so you can verify a built screen against the
design without re-reading the mock.

## Grounds

| Surface | Token | Hex |
| --- | --- | --- |
| Sidebar column | `PT.C.sidebar` | `#191813` |
| Detail pane / window | `PT.C.window` | `#1C1B17` |
| Toolbar, settings header/footer | `PT.C.chrome` | `#201F1A` |
| "me" turn ground | `PT.C.surface` | `#211E1A` |
| Search field, toggle rows, menus | `PT.C.raised` | `#24231D` |
| Text inputs in settings | `PT.C.sunken` | `#17160F` |

Note `surface` (#211E1A) and `raised` (#24231D) are different and not
interchangeable — the me-bubble is deliberately dimmer than a control.

## Text

`#F2F0EA` primary · `#D8D4CA` them-body & secondary controls · `#A29E93`
captions/icons · `#6E6B60` placeholders & section headers · `#57544C` transcript
timestamps · `#C9C4B9` subtitle inside a selected row.

## Accent (Signal red, unchanged)

| Use | Token | Hex |
| --- | --- | --- |
| Primary button fill, record dot | `signal` | `#D2371B` |
| Split-button chevron half | `signalDim` | `#B72E14` |
| Red **text/icons** on dark | `signalLit` | `#E4633F` |
| Text on a red fill | `onSignal` | `#FFF9F4` |
| Selected-row fill / ring | `selectFill` / `selectStroke` | 15% / 32% of signal |

Never use `#D2371B` for text on a dark ground — it fails contrast. Use
`signalLit`. Red is allowed on exactly four things: primary patch button,
selected session, `me` label, recording state. Nothing else.

## Type — sizes are fractional on purpose

| Element | Size | Weight |
| --- | --- | --- |
| Transcript body | 14.5 | regular, `lineSpacing 4` |
| Session time (selected / not) | 13 | semibold / medium |
| Session first line | 12 | regular |
| Button label | 13 | semibold |
| Settings row title | 13.5 | regular |
| Caption / setting subtitle | 11.5 | regular |
| Speaker label, section header | 10.5 | semibold, uppercase, tracking 0.95 |
| Session id, repo (mono) | 13 / 11.5 | regular |
| Transcript timestamp (mono) | 10.5 | regular |

Rounding 14.5 → 14 or 12.5 → 12 is the most common source of drift.

## Metrics

Sidebar **252**. Toolbar **52**. Window min **860 × 660**. Settings width **560**.
Transcript padding **22**, turn gap **22**. Bubble radius **11**, padding **13 / 16**.
Row radius **7**. Control radius **7**. Menu radius **9**.

## The three rules that break silently

1. **`turnMaxWidthFraction = 0.78`.** Trailing alignment only offsets an element
   narrower than its line. At 1.0 both speakers fill the column and the
   me-right / them-left structure vanishes with no visible error.
2. **No leading edge bars, anywhere.** Selection is a filled row with a ring.
   Every `border-left` accent was removed from this design deliberately.
3. **Body text is left-aligned for both speakers.** Only the block is
   right-aligned for `me`. Right-aligned paragraphs measurably slow reading.

## Where each file goes

| File | Purpose |
| --- | --- |
| `Theme.swift` | all tokens. Add nothing to the UI that isn't in here |
| `TranscriptView.swift` | turn grouping + me/them layout |
| `SessionRow.swift` | two-line sidebar row |
| `PatchThroughButton.swift` | toolbar split button |
| `DestinationRanking.swift` | new: persisted use counts |

`SessionStore.Item` needs one addition:

```swift
/// First thing said — the only human-readable identifier a session has.
var firstLine: String { segments.first?.text ?? "" }
```

Types referenced but not defined here (`SessionStore.Item`, `.Segment`,
`.Destination`, `.status`) are yours — adapt the property names, keep the values.
