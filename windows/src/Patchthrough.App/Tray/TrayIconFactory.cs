using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Patchthrough.App.Theme;
using Drawing = System.Drawing;

namespace Patchthrough.App.Tray;

/// <summary>What the tray icon is saying.</summary>
public enum TrayState
{
    Idle,

    /// <summary>A meeting is being captured. The mark carries a red dot.</summary>
    Recording,

    /// <summary>Transcribing. The mark's stroke goes heavy, as it does on macOS.</summary>
    Transcribing,
}

/// <summary>
/// Builds the tray icon from the same geometry the window draws.
///
/// The icons are rendered rather than checked in as files. That keeps one
/// definition of the mark, and it is what makes the two things a Windows tray
/// icon needs cheap: a set of pixel sizes for each display scale, and a light and
/// a dark variant, because the icon sits on the taskbar's ground rather than on
/// the app's. macOS solves the second with a template image that the system tints;
/// Windows has no equivalent, so the colour is chosen here.
///
/// There is deliberately no pulsing variant. The recording dot on macOS pulses,
/// but animating a tray icon means handing the shell a new icon several times a
/// second, and on Windows a blinking tray icon is the convention for "something
/// needs attention now". The state is carried by the dot, the tooltip, and the
/// menu instead.
/// </summary>
public static class TrayIconFactory
{
    /// <summary>
    /// The sizes Windows asks for across display scales. One icon file holds all
    /// of them, and the shell picks; rendering only 16 leaves a blurry icon at
    /// 150%, which is a common laptop default.
    /// </summary>
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48];

    /// <summary>
    /// An icon for one state. The caller owns it and must dispose the one it
    /// replaces: an icon holds an unmanaged handle.
    /// </summary>
    public static Drawing.Icon Build(TrayState state, bool lightTaskbar)
    {
        var frames = Sizes.Select(size => Render(state, lightTaskbar, size)).ToList();
        using var stream = new MemoryStream();
        WriteIcoWithPngFrames(stream, frames);
        stream.Position = 0;
        return new Drawing.Icon(stream);
    }

    /// <summary>One frame, as PNG bytes.</summary>
    private static byte[] Render(TrayState state, bool lightTaskbar, int pixels)
    {
        // The stroke has to contrast with the taskbar, not with the app. A light
        // taskbar takes the dark ink; a dark one takes the light.
        var ink = new SolidColorBrush(lightTaskbar ? PT.C.Window : PT.C.Text);
        ink.Freeze();
        var weight = state == TrayState.Transcribing ? Mark.HeavyWeight : Mark.RegularWeight;

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var (geometry, thickness) = Mark.Build(pixels, weight);
            context.DrawGeometry(null, Mark.PenFor(ink, thickness), geometry);

            if (state == TrayState.Recording)
            {
                // Solid Signal red, the same fill the window's record control uses.
                var dot = Mark.RecordDot(pixels);
                context.DrawEllipse(
                    PT.C.SignalBrush, null,
                    new Point(dot.X + (dot.Width / 2), dot.Y + (dot.Height / 2)),
                    dot.Width / 2, dot.Height / 2);
            }
        }

        // Rendered at 96 DPI with the geometry already sized in pixels, so one
        // device-independent pixel is one real pixel and the stroke lands on the
        // grid instead of straddling it.
        var bitmap = new RenderTargetBitmap(pixels, pixels, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// Write a multi-image .ico whose frames are PNG.
    ///
    /// A PNG payload inside an icon is understood from Windows Vista onwards, and
    /// the floor here is Windows 10. Building the container by hand avoids
    /// Bitmap.GetHicon, which hands back an unmanaged icon handle that has to be
    /// destroyed separately and leaks quietly when it is not.
    /// </summary>
    private static void WriteIcoWithPngFrames(Stream output, IReadOnlyList<byte[]> frames)
    {
        using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);

        // ICONDIR
        writer.Write((ushort)0);              // reserved
        writer.Write((ushort)1);              // type: icon
        writer.Write((ushort)frames.Count);

        const int directorySize = 6;
        const int entrySize = 16;
        var offset = directorySize + (entrySize * frames.Count);

        for (var index = 0; index < frames.Count; index++)
        {
            var size = Sizes[index];
            // 0 means 256 in this field. Nothing here reaches 256, but the rule is
            // why the field is a byte.
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);            // palette colours
            writer.Write((byte)0);            // reserved
            writer.Write((ushort)1);          // colour planes
            writer.Write((ushort)32);         // bits per pixel
            writer.Write(frames[index].Length);
            writer.Write(offset);
            offset += frames[index].Length;
        }

        foreach (var frame in frames) writer.Write(frame);
        writer.Flush();
    }
}
