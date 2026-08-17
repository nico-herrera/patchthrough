using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Patchthrough.App.Interop;

/// <summary>
/// Showing a file or folder to the user in File Explorer. This is the Windows
/// counterpart to "Show in Finder".
/// </summary>
public static class Explorer
{
    /// <summary>
    /// Open the containing folder with the item selected.
    ///
    /// The shell call is preferred over launching explorer.exe because it reuses
    /// a window that already shows the folder instead of opening another one, and
    /// because it takes the path as data rather than as a command line. Passing a
    /// user-named session folder through a command line is how a name with a
    /// quote in it becomes a second argument.
    /// </summary>
    public static void Reveal(string path)
    {
        var full = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(full);
        if (parent is null)
        {
            OpenFolder(full);
            return;
        }

        if (SHParseDisplayName(parent, IntPtr.Zero, out var folderId, 0, out _) != 0)
        {
            OpenFolder(parent);
            return;
        }
        try
        {
            if (SHParseDisplayName(full, IntPtr.Zero, out var itemId, 0, out _) != 0)
            {
                // The folder exists but the item does not, which happens when a
                // session was deleted between the click and the call.
                OpenFolder(parent);
                return;
            }
            try
            {
                var items = new[] { itemId };
                if (SHOpenFolderAndSelectItems(folderId, (uint)items.Length, items, 0) != 0)
                {
                    OpenFolder(parent);
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(itemId);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(folderId);
        }
    }

    /// <summary>
    /// Open a folder with nothing selected. Used for the recordings root, which
    /// is a destination rather than an item.
    /// </summary>
    public static void OpenFolder(string path)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(full);
        // UseShellExecute, so the path is handed to the shell as a path. It never
        // reaches a command interpreter.
        Process.Start(new ProcessStartInfo(full) { UseShellExecute = true })?.Dispose();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHParseDisplayName(
        string name, IntPtr bindContext, out IntPtr idList, uint sfgaoIn, out uint sfgaoOut);

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHOpenFolderAndSelectItems(
        IntPtr folderIdList, uint count, IntPtr[] itemIdLists, uint flags);
}
