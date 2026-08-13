using Patchthrough.App.Theme;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Patchthrough.App.Tray;

/// <summary>
/// Paints the tray menu in the app's palette.
///
/// The tray menu is a Windows Forms menu on purpose: on macOS the equivalent is a
/// plain system menu, so a native-feeling menu is the faithful parallel rather
/// than a custom-drawn popup. What is not faithful is its default colour scheme,
/// which is light grey with a blue highlight, so the colours are replaced here and
/// only the colours.
///
/// One thing does not port. A system menu cannot mix fonts and colours inside a
/// single item, so the menu bar's red state line and monospaced digits become
/// plain text. That is a deliberate trade for a menu that behaves the way a
/// Windows user expects.
/// </summary>
internal sealed class DarkMenuRenderer() : Forms.ToolStripProfessionalRenderer(new DarkColorTable())
{
    protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
    {
        // A disabled item is a state line, not an unavailable action, so it keeps
        // a readable colour instead of the system's washed-out grey.
        e.TextColor = e.Item?.Enabled == true ? Gdi(PT.C.Text2) : Gdi(PT.C.Text4);
        base.OnRenderItemText(e);
    }

    internal static Drawing.Color Gdi(System.Windows.Media.Color color) =>
        Drawing.Color.FromArgb(color.A, color.R, color.G, color.B);

    private sealed class DarkColorTable : Forms.ProfessionalColorTable
    {
        public override Drawing.Color ToolStripDropDownBackground => Gdi(PT.C.Raised);
        public override Drawing.Color ImageMarginGradientBegin => Gdi(PT.C.Raised);
        public override Drawing.Color ImageMarginGradientMiddle => Gdi(PT.C.Raised);
        public override Drawing.Color ImageMarginGradientEnd => Gdi(PT.C.Raised);
        public override Drawing.Color MenuBorder => Gdi(PT.C.MenuEdge);

        // Highlight is Signal at 16%, the same value the drawn menus use. It is
        // pre-flattened against the menu ground because a system menu has no
        // alpha compositing to hand.
        public override Drawing.Color MenuItemSelected => Gdi(Flatten(PT.C.MenuSelectFill, PT.C.Raised));
        public override Drawing.Color MenuItemSelectedGradientBegin => MenuItemSelected;
        public override Drawing.Color MenuItemSelectedGradientEnd => MenuItemSelected;
        public override Drawing.Color MenuItemBorder => Gdi(PT.C.MenuEdge);
        public override Drawing.Color MenuItemPressedGradientBegin => Gdi(PT.C.Raised);
        public override Drawing.Color MenuItemPressedGradientEnd => Gdi(PT.C.Raised);
        public override Drawing.Color SeparatorDark => Gdi(PT.C.MenuRule);
        public override Drawing.Color SeparatorLight => Gdi(PT.C.MenuRule);

        /// <summary>
        /// Composite a translucent token over its ground, so a colour defined as an
        /// alpha over the menu reads the same here as it does in the window.
        /// </summary>
        private static System.Windows.Media.Color Flatten(
            System.Windows.Media.Color over,
            System.Windows.Media.Color under)
        {
            var alpha = over.A / 255d;
            return System.Windows.Media.Color.FromRgb(
                (byte)Math.Round((over.R * alpha) + (under.R * (1 - alpha))),
                (byte)Math.Round((over.G * alpha) + (under.G * (1 - alpha))),
                (byte)Math.Round((over.B * alpha) + (under.B * (1 - alpha))));
        }
    }
}
