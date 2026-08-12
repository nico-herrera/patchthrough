# Patchthrough — Claude Code handoff

macOS menu bar app. One mark, two weights, everything else is derived.
Design source of truth: `Patchthrough Logo.dc.html` (round 8, top section).
Design rationale and rules: `HANDOFF.md`.

## What is in this folder

```
patchthrough-mark.svg              regular weight (1.6), 24×24
patchthrough-mark-heavy.svg        heavy weight (2.1), 24×24
patchthrough-icon-1024.svg         dock icon, ink variant
assets/
  menubar-idle.svg                 template art, regular
  menubar-patching.svg             template art, heavy
  menubar-recording-template.svg   template art, regular (pair with the dot)
  menubar-recording-dot.svg        #D2371B dot, NOT part of the template
  icon-ink-1024.svg                dock, ink ground        <- default
  icon-signal-1024.svg             dock, red ground
  icon-paper-1024.svg              dock, light ground
```

Approved treatments: **Signal** (red ground) is the shipping dock icon; **Paper**
(light ground) is its light-context counterpart — website, docs, App Store listing
on light backgrounds. **Ink** is a fallback, not for release.

## Geometry

24 × 24 grid, stroke-based, round caps, no fill.

```
transform  translate(0, -0.45)
circle     cx=12 cy=12 r=6.3
path       M2.8 19.2 C 5.2 14.8 7.8 10.9 10.3 9.6 C 12.8 8.3 16.8 7.2 21.2 6.4
weights    regular 1.6   heavy 2.1     (ring and cord ALWAYS match)
```

### Rules that will break the logo if ignored

1. The cord must never cross the centre of the ring — a straight line through a
   circle is the universal "disabled" sign. Closest approach is 2.9 units, upper left.
2. Ring and cord are always the same stroke weight. Unequal weights make it read as
   a magnifying glass.
3. Do not thicken strokes to "help" small sizes. Use the regular weight at 16–22pt
   and let the system antialias; the counter is tuned to stay open at 16pt.
4. Never rotate, mirror, or set the mark upright/axially symmetric.

## Mark in SwiftUI

Draw it as a Shape so it is crisp at every size and follows the foreground colour.

```swift
import SwiftUI

/// Patchthrough mark on its native 24×24 grid.
struct PatchthroughMark: Shape {
    /// Stroke weight in grid units: 1.6 regular, 2.1 heavy.
    var weight: CGFloat = 1.6

    func path(in rect: CGRect) -> Path {
        let s = min(rect.width, rect.height) / 24
        func p(_ x: CGFloat, _ y: CGFloat) -> CGPoint {
            CGPoint(x: rect.minX + x * s, y: rect.minY + (y - 0.45) * s)
        }

        var path = Path()
        path.addEllipse(in: CGRect(
            x: rect.minX + (12 - 6.3) * s,
            y: rect.minY + (12 - 6.3 - 0.45) * s,
            width: 12.6 * s, height: 12.6 * s
        ))
        path.move(to: p(2.8, 19.2))
        path.addCurve(to: p(10.3, 9.6), control1: p(5.2, 14.8), control2: p(7.8, 10.9))
        path.addCurve(to: p(21.2, 6.4), control1: p(12.8, 8.3), control2: p(16.8, 7.2))
        return path
    }
}

struct PatchthroughMarkView: View {
    var weight: CGFloat = 1.6
    var body: some View {
        GeometryReader { geo in
            let s = min(geo.size.width, geo.size.height) / 24
            PatchthroughMark(weight: weight)
                .stroke(style: StrokeStyle(lineWidth: weight * s, lineCap: .round))
        }
        .aspectRatio(1, contentMode: .fit)
    }
}
```

## Menu bar item

```swift
let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
let image = NSImage(named: "MenubarIdle")!
image.isTemplate = true          // required: macOS then handles light/dark + accessibility
image.size = NSSize(width: 18, height: 18)
item.button?.image = image
```

Ship template PNGs at **18×18 @1x/@2x/@3x** (rasterise from the SVGs above, black
art on transparent). Also supply 16 and 22 if you expose a size preference.

### States

| State | Art | Colour |
| --- | --- | --- |
| Idle | `menubar-idle` | template |
| Recording | `menubar-recording-template` + dot overlay | dot `#D2371B`, 7px at 18pt, lower right, pulse 1.6s ease-in-out |
| Patching | `menubar-patching` (heavy) | template |

The recording dot is the **only** colour in the entire product. Composite it as a
separate non-template layer over the template image, or draw it in SwiftUI — do not
bake it into the template PNG or macOS will flatten it to black.

## Dock icon

- Squircle, corner radius **22.4%** of the tile (1024 → 229.4).
- Art at **64%** of the tile, inset 184 at 1024, heavy weight.
- Flat fill only: no bevel, no gradient, no inner shadow.
- Generate `AppIcon.appiconset` at 1024, 512, 256, 128, 64, 32, 16 from the chosen
  1024 SVG. Render each size from vector, do not downscale the 1024 PNG.

## Rasterised PNGs (`assets/png/`)

Rendered from vector at each size — nothing is downscaled from a larger PNG.

```
menubar-idle-{16,18,22}.png            template art, regular
menubar-idle-18-{2x,3x}.png            36 / 54 px
menubar-patching-{16,18,22}.png        template art, heavy
menubar-patching-18-{2x,3x}.png        36 / 54 px
menubar-recording-18{,-2x,-3x}.png     template art; composite the dot yourself
appicon-signal-{16,32,64,128,256,512,1024}.png   <- APPROVED, ships as the app icon
appicon-paper-{16,32,64,128,256,512,1024}.png    <- APPROVED, light contexts
appicon-ink-{16,32,64,128,256,512,1024}.png      fallback only
```

**Rename `-2x` / `-3x` to `@2x` / `@3x` when importing into the asset catalog** —
the `@` could not be written here. Xcode expects `menubar-idle-18@2x.png`.

All menu bar PNGs are black art on transparent, ~25% semi-transparent pixels at the
stroke edges. Set `isTemplate = true` and let macOS tint them; do not pre-colour.

## Colour

| Token | Hex | Use |
| --- | --- | --- |
| Ink | `#16150F` | mark on light, dock ground |
| Paper | `#F2F0EA` | mark on ink |
| Signal | `#D2371B` | recording dot only |
| Paper icon ground | `#F2EFE7` / hairline `#E2DED4` | light icon variant |

## Wordmark

Instrument Sans 600. Tracking −0.035em display, −0.03em in the lockup. Horizontal
lockup: 34px mark, 13px gap, 32px text. Reversed uses Paper on Ink. There is an
optional variant where the second "o" of "through" becomes the socket ring — not
approved, do not ship without sign-off.

## Open items

- Menu bar and appicon PNGs are rasterised (see above). Still needs wrapping into an
  `AppIcon.appiconset` with a `Contents.json`, and the `@2x`/`@3x` rename.
- **Name not cleared.** The previous name, Baton, turned out to be crowded — an
  existing macOS presentation-handoff app plus live registered software marks.
  "Patchthrough" has not been searched.
- Dead ends, do not revive: the upright plug silhouette (rounds 5–6) and 7a, which
  reads as a magnifying glass.
