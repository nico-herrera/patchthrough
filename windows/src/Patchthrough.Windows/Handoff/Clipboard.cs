using System.Runtime.InteropServices;

namespace Patchthrough.Windows.Handoff;

/// <summary>
/// The clipboard, for handing a transcript to an application that cannot be given a
/// file path.
///
/// This is Win32 rather than the framework's clipboard classes because the console
/// tool and the app both need it, and neither WPF's nor Windows Forms' clipboard is
/// available to a library that must also compile without them. The calls are
/// documented and small.
///
/// Every method returns a bool rather than throwing. A clipboard that refuses is a
/// normal outcome: another process can hold it open, and a session with no
/// interactive desktop has none at all. The caller says something different in that
/// case rather than failing the handoff.
/// </summary>
public static class Clipboard
{
    private const uint CF_UNICODETEXT = 13;
    private const uint CF_HDROP = 15;
    private const uint GMEM_MOVEABLE = 0x0002;

    /// <summary>Put text on the clipboard, replacing what was there.</summary>
    public static bool SetText(string text)
    {
        // Unicode, so an accented name or a non-Latin transcript survives. The older
        // text format reads the console code page and would mangle both.
        var bytes = System.Text.Encoding.Unicode.GetBytes(text + '\0');
        return WithClipboard(() => Place(CF_UNICODETEXT, bytes));
    }

    /// <summary>
    /// Put a file reference on the clipboard, so a paste into a chat composer
    /// attaches the file rather than pasting its text.
    /// </summary>
    public static bool SetFile(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) return false;

        // DROPFILES: a 20-byte header, then a double-null-terminated list of wide
        // paths. fWide is what marks the list as Unicode.
        var pathBytes = System.Text.Encoding.Unicode.GetBytes(full + "\0\0");
        var header = new byte[20];
        BitConverter.TryWriteBytes(header.AsSpan(0, 4), 20);      // pFiles: offset to the list
        BitConverter.TryWriteBytes(header.AsSpan(16, 4), 1);      // fWide: paths are Unicode

        var payload = new byte[header.Length + pathBytes.Length];
        header.CopyTo(payload, 0);
        pathBytes.CopyTo(payload, header.Length);

        return WithClipboard(() => Place(CF_HDROP, payload));
    }

    private static bool WithClipboard(Func<bool> body)
    {
        // A retry, because another application can hold the clipboard open for a
        // moment. Failing on the first attempt would make the handoff flaky.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    if (!EmptyClipboard()) return false;
                    return body();
                }
                finally
                {
                    CloseClipboard();
                }
            }
            Thread.Sleep(20);
        }
        return false;
    }

    /// <summary>
    /// Copy the payload into global memory and hand it over. Called with the
    /// clipboard open.
    /// </summary>
    private static bool Place(uint format, byte[] payload)
    {
        var handle = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)payload.Length);
        if (handle == IntPtr.Zero) return false;

        var locked = GlobalLock(handle);
        if (locked == IntPtr.Zero)
        {
            GlobalFree(handle);
            return false;
        }
        try
        {
            Marshal.Copy(payload, 0, locked, payload.Length);
        }
        finally
        {
            GlobalUnlock(handle);
        }

        if (SetClipboardData(format, handle) != IntPtr.Zero) return true;

        // Ownership only transfers on success, so a failed call leaves this memory
        // to free here.
        GlobalFree(handle);
        return false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr data);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr handle);
}
