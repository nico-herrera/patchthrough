using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Patchthrough.App.Interop;

/// <summary>
/// The parts of the window frame that WPF does not draw.
///
/// The app draws its own 52 unit titlebar strip, but the frame around it belongs
/// to the desktop compositor: the resize border, the drop shadow, and the
/// thumbnail Windows shows in Alt-Tab and on the taskbar. Left alone those are
/// light, which puts a white hairline around a very dark window.
/// </summary>
public static class Dwm
{
    /// <summary>
    /// Windows 10 20H1 and later. On 1809 through 1903 the same attribute lived
    /// at 19, undocumented, so both are attempted: the older build silently
    /// ignores the newer number.
    /// </summary>
    private const int UseImmersiveDarkMode = 20;
    private const int UseImmersiveDarkModeBefore20H1 = 19;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// Ask for a dark frame. Failure is ignored on purpose: an older build that
    /// does not know the attribute keeps a light frame, which is cosmetic, and
    /// refusing to show the window over it would not be.
    /// </summary>
    public static void UseDarkFrame(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        var enabled = 1;
        if (DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(handle, UseImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));
        }
    }
}
