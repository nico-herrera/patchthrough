using System.Windows;
using System.Windows.Media;

namespace Patchthrough.App.Theme;

/// <summary>
/// Design tokens, ported value for value from
/// Sources/patchthrough/UI/Theme.swift. Per design/DESIGN_RULES.md rule 1 this
/// file is the only place a raw value may appear: views and styles reference
/// PT.C, PT.F and PT.M and nothing else.
///
/// Two things about this port are worth knowing.
///
/// **Sizes carry over one to one.** A macOS point and a WPF device-independent
/// pixel are both a logical pixel at 96 DPI, so there is no 72-to-96 conversion
/// to apply. The fractional sizes are deliberate (rule 7) and rounding them is
/// the most common way this design drifts. FontSize is a double, so they cost
/// nothing to keep exact.
///
/// **The colours are sRGB.** WPF's Color.FromRgb is sRGB already, so the
/// AppKit trap the Swift file warns about, where generic RGB renders #D2371B as
/// #DD4D22, has no equivalent here. Never route a token through the named
/// Colors class or an scRGB constructor.
/// </summary>
public static class PT
{
    /// <summary>Colour. Every brush is frozen, so it is safe to share and cheap to draw.</summary>
    public static class C
    {
        // Grounds, darkest to lightest.
        public static readonly Color Sunken = Hex(0x17160F);   // text inputs in settings
        public static readonly Color Sidebar = Hex(0x191813);  // sidebar column
        public static readonly Color Window = Hex(0x1C1B17);   // detail pane, window body
        public static readonly Color Chrome = Hex(0x201F1A);   // titlebar, settings sheet
        public static readonly Color Surface = Hex(0x211E1A);  // "me" turn ground (NOT Raised)
        public static readonly Color Raised = Hex(0x24231D);   // search field, cards, menus
        public static readonly Color Chip = Hex(0x2A2822);     // Choose and Cancel chips

        // Lines.
        public static readonly Color Hairline = Hex(0x2C2A23);       // pane dividers
        public static readonly Color Border2 = Hex(0x302E27);        // quieter control borders
        public static readonly Color Border = Hex(0x3A3730);         // control borders
        public static readonly Color MenuEdge = Hex(0x454138);       // popover border
        public static readonly Color MenuRule = Hex(0x35322A);       // divider inside the menu
        public static readonly Color SwitchOffTrack = Hex(0x35322A); // switch track, off
        public static readonly Color SwitchOffKnob = Hex(0x8C887E);  // switch knob, off

        // Text, brightest to dimmest.
        public static readonly Color Text = Hex(0xF2F0EA);     // primary
        public static readonly Color Text2 = Hex(0xD8D4CA);    // "them" body, secondary controls
        public static readonly Color TextSel = Hex(0xC9C4B9);  // subtitle inside a selected row
        public static readonly Color Text3 = Hex(0xA29E93);    // captions, icons
        public static readonly Color Label = Hex(0x7E7A70);    // section labels, detail stats
        public static readonly Color Text4 = Hex(0x6E6B60);    // placeholders, section headers
        public static readonly Color Text5 = Hex(0x57544C);    // transcript timestamps
        public static readonly Color GlyphDim = Hex(0x4A473E); // sidebar footer folder glyph
        public static readonly Color SpeakerThem = Hex(0x8C887E); // THEM label, repo chip

        // Accent: Signal red. Permitted on exactly the five uses in rule 2, and
        // nowhere else. There is no second accent colour, ever.
        public static readonly Color Signal = Hex(0xD2371B);     // fills: primary button, record dot
        public static readonly Color SignalDim = Hex(0xB72E14);  // split-button chevron half
        public static readonly Color SignalLit = Hex(0xE4633F);  // signal-on-dark TEXT and icons
        public static readonly Color SignalInk = Hex(0xC08A78);  // mono caption in a selected row
        public static readonly Color SignalWarn = Hex(0xC98872); // inline permission-warning text
        public static readonly Color OnSignal = Hex(0xFFF9F4);   // text on a signal fill

        /// <summary>Selected row: fill plus ring. Never a leading edge bar (rule 4).</summary>
        public static readonly Color SelectFill = Alpha(Signal, 0.15);
        public static readonly Color SelectStroke = Alpha(Signal, 0.32);
        public static readonly Color WarnFill = Alpha(Signal, 0.10);
        public static readonly Color MenuSelectFill = Alpha(Signal, 0.16);
        /// <summary>Divider between the split button's two halves.</summary>
        public static readonly Color OnSignalRule = Alpha(OnSignal, 0.22);
        public static readonly Color MenuShadow = Alpha(Colors.Black, 0.60);

        // Brushes. Views bind these; the Color values above exist for the few
        // places that need to interpolate or hand a colour to Win32.
        public static readonly SolidColorBrush SunkenBrush = Frozen(Sunken);
        public static readonly SolidColorBrush SidebarBrush = Frozen(Sidebar);
        public static readonly SolidColorBrush WindowBrush = Frozen(Window);
        public static readonly SolidColorBrush ChromeBrush = Frozen(Chrome);
        public static readonly SolidColorBrush SurfaceBrush = Frozen(Surface);
        public static readonly SolidColorBrush RaisedBrush = Frozen(Raised);
        public static readonly SolidColorBrush ChipBrush = Frozen(Chip);

        public static readonly SolidColorBrush HairlineBrush = Frozen(Hairline);
        public static readonly SolidColorBrush Border2Brush = Frozen(Border2);
        public static readonly SolidColorBrush BorderBrush = Frozen(Border);
        public static readonly SolidColorBrush MenuEdgeBrush = Frozen(MenuEdge);
        public static readonly SolidColorBrush MenuRuleBrush = Frozen(MenuRule);
        public static readonly SolidColorBrush SwitchOffTrackBrush = Frozen(SwitchOffTrack);
        public static readonly SolidColorBrush SwitchOffKnobBrush = Frozen(SwitchOffKnob);

        public static readonly SolidColorBrush TextBrush = Frozen(Text);
        public static readonly SolidColorBrush Text2Brush = Frozen(Text2);
        public static readonly SolidColorBrush TextSelBrush = Frozen(TextSel);
        public static readonly SolidColorBrush Text3Brush = Frozen(Text3);
        public static readonly SolidColorBrush LabelBrush = Frozen(Label);
        public static readonly SolidColorBrush Text4Brush = Frozen(Text4);
        public static readonly SolidColorBrush Text5Brush = Frozen(Text5);
        public static readonly SolidColorBrush GlyphDimBrush = Frozen(GlyphDim);
        public static readonly SolidColorBrush SpeakerThemBrush = Frozen(SpeakerThem);

        public static readonly SolidColorBrush SignalBrush = Frozen(Signal);
        public static readonly SolidColorBrush SignalDimBrush = Frozen(SignalDim);
        public static readonly SolidColorBrush SignalLitBrush = Frozen(SignalLit);
        public static readonly SolidColorBrush SignalInkBrush = Frozen(SignalInk);
        public static readonly SolidColorBrush SignalWarnBrush = Frozen(SignalWarn);
        public static readonly SolidColorBrush OnSignalBrush = Frozen(OnSignal);

        public static readonly SolidColorBrush SelectFillBrush = Frozen(SelectFill);
        public static readonly SolidColorBrush SelectStrokeBrush = Frozen(SelectStroke);
        public static readonly SolidColorBrush WarnFillBrush = Frozen(WarnFill);
        public static readonly SolidColorBrush MenuSelectFillBrush = Frozen(MenuSelectFill);
        public static readonly SolidColorBrush OnSignalRuleBrush = Frozen(OnSignalRule);
        public static readonly SolidColorBrush TransparentBrush = Frozen(Colors.Transparent);

        private static Color Hex(uint value) => Color.FromRgb(
            (byte)((value >> 16) & 0xFF), (byte)((value >> 8) & 0xFF), (byte)(value & 0xFF));

        private static Color Alpha(Color color, double opacity) =>
            Color.FromArgb((byte)Math.Round(opacity * 255), color.R, color.G, color.B);

        private static SolidColorBrush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>
    /// Type. Sizes are fractional on purpose (rule 7).
    ///
    /// The weights need a decision the Swift file did not: Segoe UI has no
    /// Medium cut (Light 300, Semilight 350, Regular 400, Semibold 600, Bold
    /// 700), so SF's `.medium` has no direct counterpart. It is mapped by role.
    /// Where medium exists to sit one step *below* a semibold sibling, as an
    /// unselected session row does against a selected one, it becomes Regular so
    /// the contrast keeps its direction. Where medium is emphasis on its own, as
    /// on a control label, it becomes SemiBold. This is the one deliberate type
    /// deviation in the port.
    /// </summary>
    public static class F
    {
        /// <summary>
        /// Segoe UI, not Segoe UI Variable. Variable ships only on Windows 11 and
        /// the floor here is Windows 10 1809, and WPF does not drive its optical
        /// size axis, so a fallback stack would render differently on the two and
        /// break any screenshot comparison.
        /// </summary>
        public static readonly FontFamily Ui = new("Segoe UI");

        /// <summary>
        /// Consolas, which ships on every supported version. Cascadia Mono is
        /// absent from a base Windows 10 1809, so a stack naming it would split
        /// rendering across OS versions.
        /// </summary>
        public static readonly FontFamily Mono = new("Consolas");

        /// <summary>Segoe MDL2 Assets, for the window caption glyphs.</summary>
        public static readonly FontFamily Glyph = new("Segoe MDL2 Assets");

        public const double Transcript = 14.5;
        public const double SheetTitle = 15;
        public const double SettingRow = 13.5;
        public const double SessionTime = 13;
        public const double SessionTime2 = 13;   // unselected
        public const double Button = 13;
        public const double Wordmark = 13;
        public const double Control = 12.5;
        public const double Field = 12.5;
        public const double SessionLine = 12;
        public const double Caption = 11.5;
        public const double Speaker = 10.5;
        public const double SectionHead = 10.5;
        public const double MenuItem = 12.5;
        public const double MenuItemStrong = 12.5;
        public const double Chevron = 8;
        public const double ButtonGlyph = 10.5;

        // Icon sizes, matching the mock's SVG boxes. These are glyphs, not text.
        public const double Icon = 13;
        public const double IconSmall = 12;
        public const double Gear = 15;
        public const double Placeholder = 38;

        // Monospaced ramp.
        public const double MonoBody = 13;
        public const double MonoField = 12.5;
        public const double MonoRepo = 11.5;
        public const double MonoSmall = 11;
        public const double MonoTiny = 10.5;

        public static readonly FontWeight SheetTitleWeight = FontWeights.SemiBold;
        public static readonly FontWeight SessionTimeWeight = FontWeights.SemiBold;
        /// <summary>Regular, so the selected row stays the heavier of the pair.</summary>
        public static readonly FontWeight SessionTime2Weight = FontWeights.Regular;
        public static readonly FontWeight ButtonWeight = FontWeights.SemiBold;
        public static readonly FontWeight WordmarkWeight = FontWeights.SemiBold;
        /// <summary>SemiBold: emphasis on its own, with no heavier sibling nearby.</summary>
        public static readonly FontWeight ControlWeight = FontWeights.SemiBold;
        public static readonly FontWeight SpeakerWeight = FontWeights.SemiBold;
        public static readonly FontWeight SectionHeadWeight = FontWeights.SemiBold;
        public static readonly FontWeight MenuItemStrongWeight = FontWeights.SemiBold;
        public static readonly FontWeight ChevronWeight = FontWeights.SemiBold;
        public static readonly FontWeight ButtonGlyphWeight = FontWeights.SemiBold;
        public static readonly FontWeight PlaceholderWeight = FontWeights.Light;

        /// <summary>Tracking for uppercase micro-labels: 0.09em at 10.5pt.</summary>
        public const double LabelTracking = 0.95;

        /// <summary>
        /// The transcript line box, 14.5 × 1.62 from the mock. WPF sets the whole
        /// box through LineHeight with BlockLineHeight, so the SwiftUI workaround
        /// of adding 6.41 of spacing per line is not needed here. Do not also pad
        /// vertically: that double-counts the leading.
        /// </summary>
        public const double TranscriptLineHeight = 23.49;
    }

    /// <summary>Metrics.</summary>
    public static class M
    {
        public const double SidebarWidth = 252;
        public const double TitleBarHeight = 52;
        public const double WindowMinWidth = 860;
        public const double WindowMinHeight = 660;
        public const double WindowDefaultWidth = 940;
        public const double WindowDefaultHeight = 720;
        public const double SettingsWidth = 560;
        public const double SheetBodyMaxHeight = 620;

        // The settings switch: a 38x22 pill with an 18pt knob inset 2pt.
        public const double SwitchTrackWidth = 38;
        public const double SwitchTrackHeight = 22;
        public const double SwitchKnobSize = 18;
        public const double SwitchKnobInset = 2;

        /// <summary>
        /// Leading inset of the titlebar strip. The macOS value is 88 because it
        /// has to clear the traffic lights, which sit at the left. Windows puts
        /// its caption buttons at the right, so the mark starts near the edge and
        /// the trailing side carries the clearance instead.
        /// </summary>
        public const double TitleBarLeading = 16;
        public const double TitleBarTrailing = 14;

        /// <summary>Width of one caption button, and they are TitleBarHeight tall.</summary>
        public const double CaptionButtonWidth = 46;

        public const double TranscriptPad = 22;
        public const double TurnGap = 22;

        /// <summary>
        /// LOAD-BEARING. Trailing alignment can only offset an element narrower
        /// than its line: at 1.0 both speakers span the column and the me-right,
        /// them-left structure silently disappears.
        /// </summary>
        public const double TurnMaxWidthFraction = 0.78;

        public const double BubbleRadius = 11;
        public const double BubblePadV = 13;
        public const double BubblePadH = 16;

        public const double RowRadius = 7;
        public const double RowPad = 9;
        public const double RowInset = 8;
        public const double RowGap = 3;

        public const double ControlRadius = 7;
        public const double SplitButtonHeight = 32;
        public const double SplitChevronWidth = 28;
        public const double FieldRadius = 6;
        public const double CardRadius = 8;
        public const double MenuRadius = 9;
        public const double MenuRowRadius = 5;

        public const double SidebarPad = 12;
        public const double SheetPadH = 20;
        public const double SheetSectionGap = 22;

        // The ranked destination menu, drawn in the app rather than as a system
        // menu because a system menu owns its own material and metrics.
        public const double MenuWidth = 300;
        public const double MenuPadding = 6;
        public const double MenuTextInset = 10;
        public const double MenuSectionTopPad = 8;
        public const double MenuSectionBottomPad = 6;
        public const double MenuRowPadV = 7;
        public const double MenuFrequentRowPadV = 8;
        public const double MenuRowGap = 9;
        public const double MenuIconSize = 14;
        public const double MenuRuleInset = 8;
        public const double MenuRulePadV = 5;
        public const double MenuTop = 61;
        public const double MenuTrailing = 20;
        public const double MenuShadowRadius = 20;
        public const double MenuShadowY = 18;

        // Icon sizes.
        public const double MarkSize = 17;
        public const double IconSmall = 12;
        public const double IconTiny = 10.5;

        /// <summary>The tray icon's grid, and the recording dot on it.</summary>
        public const double StatusItemSize = 18;
        public const double RecordDotSize = 7;
        public const double NotesGutterWidth = 46;
        public const double NotesStripMaxHeight = 132;

        /// <summary>Hairline. One device-independent pixel.</summary>
        public const double Hairline = 1;

        /// <summary>
        /// Smallest tappable control, from the accessibility floor in rule 13.
        /// </summary>
        public const double MinTarget = 28;
    }

    /// <summary>
    /// Composite insets.
    ///
    /// These exist because XAML cannot build a Thickness out of markup
    /// extensions: "{x:Static M.Foo},6" is a parse error, so a padding has to
    /// arrive as one object. Naming them here also fixes something the macOS
    /// views do less tidily, where a handful of padding pairs sit inline in the
    /// view files. Each value below is the pair its macOS counterpart uses, cited
    /// at the token.
    /// </summary>
    public static class T
    {
        /// <summary>Drag chip, and any chip like it.</summary>
        public static readonly Thickness Chip = new(11, 8, 11, 8);

        /// <summary>The primary button's label inset.</summary>
        public static readonly Thickness PrimaryButton = new(14, 0, 14, 0);

        /// <summary>Sidebar search field.</summary>
        public static readonly Thickness SearchField = new(9, 6, 9, 6);

        /// <summary>A settings text well.</summary>
        public static readonly Thickness SettingsField = new(13, 9, 13, 9);

        /// <summary>The settings sheet header and footer strips.</summary>
        public static readonly Thickness SheetStrip = new(M.SheetPadH, 18, M.SheetPadH, 18);

        /// <summary>The Save button in the settings footer.</summary>
        public static readonly Thickness SheetPrimaryButton = new(17, 9, 17, 9);

        /// <summary>The detail pane header strip.</summary>
        public static readonly Thickness DetailHeader = new(14, 12, 14, 12);

        /// <summary>One note in the notes list.</summary>
        public static readonly Thickness NoteRow = new(9, 7, 9, 7);

        /// <summary>The note entry field.</summary>
        public static readonly Thickness NoteField = new(14, 10, 14, 10);

        /// <summary>A transcript turn's bubble.</summary>
        public static readonly Thickness Bubble = new(M.BubblePadH, M.BubblePadV, M.BubblePadH, M.BubblePadV);

        /// <summary>One row of a drawn menu.</summary>
        public static readonly Thickness MenuRow = new(M.MenuTextInset, M.MenuRowPadV, M.MenuTextInset, M.MenuRowPadV);

        /// <summary>A most-used row, which sits a little taller.</summary>
        public static readonly Thickness MenuFrequentRow =
            new(M.MenuTextInset, M.MenuFrequentRowPadV, M.MenuTextInset, M.MenuFrequentRowPadV);

        /// <summary>The divider inside a drawn menu.</summary>
        public static readonly Thickness MenuRule = new(M.MenuRuleInset, M.MenuRulePadV, M.MenuRuleInset, M.MenuRulePadV);

        /// <summary>A sidebar row, inset from the column edges.</summary>
        public static readonly Thickness SidebarRow = new(M.RowInset, 0, M.RowInset, M.RowGap);

        /// <summary>Inside a sidebar row.</summary>
        public static readonly Thickness SidebarRowContent = new(M.RowPad, M.RowPad, M.RowPad, M.RowPad);

        /// <summary>A tooltip's inner inset.</summary>
        public static readonly Thickness ToolTip = new(8, 5, 8, 5);

        /// <summary>A plain text input.</summary>
        public static readonly Thickness TextField = new(8, 6, 8, 6);

        /// <summary>The transcript column.</summary>
        public static readonly Thickness TranscriptColumn =
            new(M.TranscriptPad, M.TranscriptPad, M.TranscriptPad, M.TranscriptPad);

        /// <summary>A hairline at the top of an element.</summary>
        public static readonly Thickness TopHairline = new(0, M.Hairline, 0, 0);

        /// <summary>A hairline at the bottom of an element.</summary>
        public static readonly Thickness BottomHairline = new(0, 0, 0, M.Hairline);

        /// <summary>A hairline on the trailing edge, for the sidebar divider.</summary>
        public static readonly Thickness RightHairline = new(0, 0, M.Hairline, 0);

        /// <summary>A one pixel border all round.</summary>
        public static readonly Thickness Hairline = new(M.Hairline);

        /// <summary>The titlebar's leading inset, for the mark.</summary>
        public static readonly Thickness TitleBarLeft = new(M.TitleBarLeading, 0, 0, 0);

        /// <summary>The gap before the caption buttons.</summary>
        public static readonly Thickness TitleBarRight = new(M.TitleBarTrailing, 0, 0, 0);

        /// <summary>The space below one transcript turn.</summary>
        public static readonly Thickness TurnGap = new(0, 0, 0, M.TurnGap);

        /// <summary>Sidebar content, inset from the column edges.</summary>
        public static readonly Thickness SidebarHorizontal = new(M.SidebarPad, 0, M.SidebarPad, 0);

        public static readonly Thickness None = new(0);
    }
}
