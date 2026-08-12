import SwiftUI

// Generated from "Patchthrough App Redesign.dc.html" round 11a / 10d / 10e.
// These are the EXACT values in the design. Do not substitute system colors,
// .secondary, or SF-default sizes — every number here is deliberate.

enum PT {

    // MARK: - Color

    enum C {
        // Grounds, darkest to lightest
        static let sidebar  = hex(0x191813)  // #191813 sidebar column
        static let window   = hex(0x1C1B17)  // #1C1B17 detail pane, window body
        static let chrome   = hex(0x201F1A)  // #201F1A toolbar, settings header
        static let surface  = hex(0x211E1A)  // #211E1A "me" turn ground  (NOT raised)
        static let raised   = hex(0x24231D)  // #24231D search field, toggle rows, menus
        static let sunken   = hex(0x17160F)  // #17160F text inputs in settings

        // Lines
        static let hairline = hex(0x2C2A23)  // #2C2A23 pane dividers
        static let border   = hex(0x3A3730)  // #3A3730 control borders
        static let border2  = hex(0x302E27)  // #302E27 quieter control borders
        static let menuEdge = hex(0x454138)  // #454138 popover border

        // Text, brightest to dimmest
        static let text     = hex(0xF2F0EA)  // primary
        static let text2    = hex(0xD8D4CA)  // "them" body, secondary controls
        static let text3    = hex(0xA29E93)  // captions, icons
        static let text4    = hex(0x6E6B60)  // placeholders, section headers
        static let text5    = hex(0x57544C)  // timestamps in transcript
        static let textSel  = hex(0xC9C4B9)  // subtitle inside a selected row

        // Accent — Signal red
        static let signal    = hex(0xD2371B)  // fills: primary button, record dot
        static let signalDim = hex(0xB72E14)  // split-button chevron half
        static let signalLit = hex(0xE4633F)  // signal-on-dark TEXT and icons
        static let onSignal  = hex(0xFFF9F4)  // text on a signal fill
        static let signalInk = hex(0xC08A78)  // mono caption inside a selected row
        static let signalWarn = hex(0xC98872)  // inline permission-warning text

        /// Selected row: fill + ring. Never a leading edge bar.
        static let selectFill   = Color(red: 0xD2/255, green: 0x37/255, blue: 0x1B/255, opacity: 0.15)
        static let selectStroke = Color(red: 0xD2/255, green: 0x37/255, blue: 0x1B/255, opacity: 0.32)
        static let warnFill     = Color(red: 0xD2/255, green: 0x37/255, blue: 0x1B/255, opacity: 0.10)
        static let menuHilite   = Color(red: 0xD2/255, green: 0x37/255, blue: 0x1B/255, opacity: 0.16)

        static let speakerThem = hex(0x8C887E)  // "THEM" label

        private static func hex(_ v: UInt32) -> Color {
            Color(red: Double((v >> 16) & 0xFF) / 255,
                  green: Double((v >> 8) & 0xFF) / 255,
                  blue: Double(v & 0xFF) / 255)
        }
    }

    // MARK: - Type
    //
    // Sizes are fractional on purpose (14.5, 12.5, 10.5). Rounding them to
    // integers is the single most common way this design drifts.

    enum F {
        static let transcript   = Font.system(size: 14.5)
        static let sessionTime  = Font.system(size: 13, weight: .semibold)
        static let sessionTime2 = Font.system(size: 13, weight: .medium)   // unselected
        static let sessionLine  = Font.system(size: 12)
        static let button       = Font.system(size: 13, weight: .semibold)
        static let control      = Font.system(size: 12.5, weight: .medium)
        static let settingRow   = Font.system(size: 13.5)
        static let caption      = Font.system(size: 11.5)
        static let mono         = Font.system(size: 13, design: .monospaced)
        static let monoSmall    = Font.system(size: 11, design: .monospaced)
        static let monoTiny     = Font.system(size: 10.5, design: .monospaced)
        static let speaker      = Font.system(size: 10.5, weight: .semibold)
        static let sectionHead  = Font.system(size: 10.5, weight: .semibold)

        /// Tracking for the uppercase speaker + section labels: 0.09em at 10.5pt.
        static let labelTracking: CGFloat = 0.95
        /// Transcript line spacing. 14.5pt * 1.62 line-height - 14.5 ≈ 9; SwiftUI
        /// lineSpacing is additive on top of the font's natural leading, so 4.
        static let transcriptLineSpacing: CGFloat = 4
    }

    // MARK: - Metrics

    enum M {
        static let sidebarWidth: CGFloat = 252
        static let toolbarHeight: CGFloat = 52
        static let windowMin = CGSize(width: 860, height: 660)
        static let settingsWidth: CGFloat = 560

        /// Transcript column padding, and the gap between turns.
        static let transcriptPad: CGFloat = 22
        static let turnGap: CGFloat = 22

        /// LOAD-BEARING. Trailing alignment can only offset an element narrower
        /// than its line. At 1.0 both speakers span the column and the
        /// me-right / them-left distinction silently disappears.
        static let turnMaxWidthFraction: CGFloat = 0.78

        static let bubbleRadius: CGFloat = 11
        static let bubblePadV: CGFloat = 13
        static let bubblePadH: CGFloat = 16
        static let rowRadius: CGFloat = 7
        static let controlRadius: CGFloat = 7
        static let menuRadius: CGFloat = 9
    }
}
