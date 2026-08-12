# Design rules

Rules for building UI in this app. Applies to every new feature, not just the
redesign. Tokens live in `Theme.swift` (`PT.C`, `PT.F`, `PT.M`) — that file is the
only place raw values are allowed to appear.

If a rule below blocks something the feature genuinely needs, say so and ask.
Don't quietly work around it.

---

## 1. Never write a raw value

No hex literals, no `.font(.system(size: 15))`, no magic padding numbers in a view.
Every colour, size and spacing comes from `PT`. If the value you need doesn't
exist, add it to `Theme.swift` with a comment saying what it's for — then use it.

```swift
// NO
.foregroundStyle(Color(hex: 0xA29E93))
.font(.system(size: 14))
.padding(20)

// YES
.foregroundStyle(PT.C.text3)
.font(PT.F.transcript)
.padding(PT.M.transcriptPad)
```

Corollary: don't use `.secondary`, `.tertiary`, or `Color.gray`. They're system
greys and read cold against these warm neutrals.

## 2. Red means recording, or destruction. Nothing else.

`PT.C.signal` is allowed on exactly five things:

- the primary "Patch through" button
- the selected session row (15% fill + 32% ring)
- the `me` speaker label
- recording state (dot, timer, Stop control)
- a genuine destructive confirmation (delete a session)

A new feature does **not** get to introduce a sixth. If something new needs
emphasis, use weight, size, or a raised ground — not colour.

Red **text or icons** on a dark ground use `PT.C.signalLit` (#E4633F), never
`PT.C.signal` (#D2371B) — the fill colour fails contrast as text.

## 3. One accent, one hue

Don't add a second accent colour for a new feature — no blue for "info", no green
for "success", no purple for "AI". Status that isn't recording is expressed with
text and iconography in the neutral ramp. Two exceptions already exist and are
enough: red, and the yellow/green/red of the window controls (system-drawn).

## 4. No leading edge bars

Selection, focus, and emphasis are never a coloured strip on an element's left
edge. Selection = filled row + ring (`PT.C.selectFill` / `selectStroke`).
This was removed from the whole app deliberately; don't reintroduce it.

## 5. Dark only

The app is `.preferredColorScheme(.dark)`. Don't add light-mode variants,
`@Environment(\.colorScheme)` branches, or semantic system colours that flip.
The one exception is the app icon, which has a light "Paper" variant for
marketing surfaces outside the app.

## 6. Layout is flex-and-gap, not margins

Use `VStack`/`HStack`/`Grid` with `spacing:`. Don't space siblings with
per-element `.padding(.bottom, n)` — it breaks the moment something is inserted
or reordered.

## 7. Type sizes are fractional on purpose

14.5, 13.5, 12.5, 11.5, 10.5. Do not round them to integers. If you're adding a
new size, check whether an existing `PT.F` case already covers it — the ramp is
deliberately short.

Uppercase micro-labels (speaker tags, section headers) are always 10.5pt
semibold with `tracking(PT.F.labelTracking)`. Uppercase without tracking looks
cramped and is the tell that a label was added ad hoc.

## 8. Copy rules

- The verb is **"Patch through to"**. Not "hand off", not "send", not "export".
- Sentence case for buttons and labels: "Patch through to claude", "Stop &
  transcribe". Not Title Case.
- Agent names keep their real casing: `claude`, `opencode`, `cursor-agent`
  lowercase (they're binaries); "Claude app", "Copilot — VS Code" as written.
- No exclamation marks, no emoji, no "Oops". Failure states say what happened
  and what to do: "Transcription failed — the audio is still in the session
  folder."
- Every toggle gets a subtitle stating the tradeoff, not a restatement of the
  label. "On-device, ~20s per hour of audio" — not "Enables transcription".

## 9. Destructive and permission-gated actions

Anything irreversible needs a confirm step. Anything that needs a macOS
permission states which permission, inline, attached to the control that needs
it — not in a footnote at the bottom of the sheet. Include the affordance that
opens the relevant System Settings pane.

## 10. Recording is a menu-bar concern

The window is for review. Don't add Record/Stop controls to the main window;
they live in the status item and its menu. The window may *display* recording
state, but not initiate it.

## 11. New surfaces inherit the existing patterns

Before designing a new panel, sheet, or popover, reuse:

- **Sheet** → settings sheet shape: fixed width `PT.M.settingsWidth`, sectioned
  with 10.5pt uppercase headers, footer with Cancel + prominent primary.
- **List row** → `SessionRow` shape: two lines, primary + mono metadata on line
  one, truncated secondary on line two.
- **Menu** → ranked-destination menu shape: `Section` headers, most-used first,
  counts as trailing text.
- **Empty state** → centred icon + one line of what's missing + the action that
  fixes it.

## 12. Don't invent the states nobody designed

These have no mock: empty/first-run, transcribing, interrupted session, search
results, light mode. If a feature lands you in one of them, build the minimum
that follows these rules and flag it — don't improvise a whole visual language.

## 13. Accessibility floor

- Interactive targets ≥ 28×28pt (menu bar rows may be shorter — system-drawn).
- Body text ≥ 11.5pt. Never below.
- Text on a coloured fill ≥ 4.5:1. `onSignal` on `signal` passes; check anything new.
- Every icon-only control gets `.help()` and an accessibility label.
- Never encode meaning in colour alone — the recording state has a dot *and* a
  timer *and* a changed verb.

## 14. Motion

One pattern: the recording dot pulses 1.6s ease-in-out. That's it. No spinners
on the patch action (it's near-instant), no slide transitions between sessions,
no spring animations on hover. If something genuinely needs a progress
indication, ask first.

---

## Checklist before you call a UI change done

- [ ] No raw hex, font size, or spacing literal outside `Theme.swift`
- [ ] No new accent colour; red only on its five permitted uses
- [ ] No leading edge bar
- [ ] Text on colour ≥ 4.5:1
- [ ] Buttons sentence case; verb is "Patch through to"
- [ ] Any new toggle has a tradeoff subtitle
- [ ] Any permission requirement stated inline at the control
- [ ] Ran in dark mode only; no colourScheme branches added
