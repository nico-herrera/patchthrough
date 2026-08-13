using System.Runtime.InteropServices;

namespace Patchthrough.Windows.Shell;

/// <summary>
/// Deleting to the Recycle Bin rather than off the disk.
///
/// A meeting cannot be recorded again, and Windows already has the place users
/// look for things they deleted by mistake. Emptying it stays their decision,
/// made somewhere they expect to make it. This is the same reasoning the macOS
/// app applies with the Trash.
/// </summary>
public static class RecycleBin
{
    private const uint FO_DELETE = 0x0003;

    // ALLOWUNDO is the whole point: without it the shell deletes permanently.
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    /// <summary>
    /// Move a file or directory to the Recycle Bin.
    /// </summary>
    /// <exception cref="IOException">
    /// The shell refused. A session directory with an open capture handle is the
    /// likely cause, which is why callers must not offer this for a live
    /// recording.
    /// </exception>
    public static void Send(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full) && !Directory.Exists(full))
        {
            throw new FileNotFoundException("nothing to delete", full);
        }

        // SHFileOperation takes a double-null-terminated list, so the extra \0
        // is not a typo. Confirmation is the caller's job: it asks first, naming
        // what is lost, then this deletes without a second dialog.
        var operation = new SHFILEOPSTRUCTW
        {
            wFunc = FO_DELETE,
            pFrom = full + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };

        var result = SHFileOperationW(ref operation);
        if (result != 0)
        {
            throw new IOException($"could not move {full} to the Recycle Bin (shell error 0x{result:X})");
        }
        // The shell reports success with this flag set when the user cancelled a
        // dialog we suppressed, so treat a surviving path as a failure.
        if (operation.fAnyOperationsAborted || File.Exists(full) || Directory.Exists(full))
        {
            throw new IOException($"{full} was not moved to the Recycle Bin");
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCTW fileOp);
}
