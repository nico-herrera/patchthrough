# Patchthrough — design handoff

Everything needed to build the redesign. Give this folder to Claude Code.

**Start with `swift/`.** The Swift files are the spec — exact tokens and view
bodies, so nothing has to be inferred from a picture. `swift/SPEC.md` is the
value table to check a built screen against.

```
swift/
  DESIGN_RULES.md           ← commit this to the repo. 14 rules + a checklist.
  Theme.swift               ← ALL tokens. Nothing in the UI that isn't in here.
  TranscriptView.swift        turn grouping + me-right / them-left layout
  SessionRow.swift            two-line sidebar row
  PatchThroughButton.swift    toolbar split button (replaces the 10-button grid)
  DestinationRanking.swift    NEW behaviour: persisted use counts
  SPEC.md                     exact-value tables + the 3 rules that break silently

CLAUDE.md-snippet.md        paste into the repo's CLAUDE.md so Claude Code
                            picks the rules up on every future request
APP_REDESIGN_HANDOFF.md     the prose spec: what changed and why, per surface
Patchthrough App Redesign.dc.html   visual source of truth — open in a browser
support.js                  needed for the .dc.html to render (keep alongside)

screenshots/                PNGs of every approved surface at 2x
logo/                       the mark: SVGs, Signal appiconset, menu bar PNGs
```

## Which sections are approved

Open the `.dc.html` in a browser. It holds several rounds; only these ship:

| Section | What it is |
| --- | --- |
| **11a** | main window — **build this one** |
| **10d** | settings sheet |
| **10e** | menu bar dropdown, idle + recording |
| 12a–12e | accent-colour exploration. **Not adopted** — the app stays Signal red. |
| 10a, 10b, 10c | rejected window alternates. Do not build. |

## Three gotchas

1. **`turnMaxWidthFraction = 0.78` is load-bearing.** Trailing alignment only
   offsets an element narrower than its line, so raising it makes both speakers
   span the full column and the me/them structure disappears with no error.
2. **No leading edge bars anywhere.** Selection is a filled row with a ring.
3. In `logo/appicon/`, **rename `-at-2x` to `@2x`** — the `@` couldn't be written
   by the generating tool. Xcode expects `icon_16x16@2x.png`. Same for the
   `-2x`/`-3x` files in `logo/menubar/`.

## Keeping it consistent later

Two files do this, and both belong **in the repo**, not in this folder:

1. `swift/DESIGN_RULES.md` → commit to the repo root (or `docs/`). It's written
   as decidable rules with a pre-merge checklist, so it can be enforced rather
   than admired.
2. `CLAUDE.md-snippet.md` → paste into the repo's `CLAUDE.md`. That's what makes
   Claude Code apply the rules automatically on future feature requests instead
   of re-deriving a style each time.

## Still open

- The name "Patchthrough" has not been trademark-cleared.
- No mocks for: empty/first-run, transcribing, interrupted session, light mode.
  `APP_REDESIGN_HANDOFF.md` §6 lists them — ask before inventing them.
