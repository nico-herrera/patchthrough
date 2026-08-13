namespace Patchthrough.Windows;

/// <summary>
/// One recording per machine, held for as long as a meeting is being captured.
///
/// Two recorders are easy to start by accident: the tray app is recording and the
/// user runs `Patchthrough rec` in a terminal. Windows allows both to open the
/// microphone in shared mode, so nothing fails. The result is two sessions of the
/// same meeting, two transcriptions, and a user who has to work out which folder
/// to keep.
///
/// This is an exclusively opened file rather than a named mutex, for two
/// reasons. Windows releases a file handle when a process dies, so a crashed
/// recorder does not leave the machine unable to record until the user signs
/// out. And a file handle has no thread affinity, where a mutex has to be
/// released by the thread that took it: this lock is taken on whichever thread
/// starts a recording and released on whichever thread stops it.
/// </summary>
internal sealed class RecordingLock : IDisposable
{
    private readonly FileStream _stream;

    private RecordingLock(FileStream stream) => _stream = stream;

    private static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "patchthrough", "recording.lock");

    /// <summary>
    /// Take the lock, or return null when another process is recording.
    /// </summary>
    public static RecordingLock? TryAcquire()
    {
        var path = Path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        try
        {
            // DeleteOnClose keeps the directory tidy; the exclusive share mode
            // is what actually holds the lock.
            return new RecordingLock(new FileStream(
                path, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 1, FileOptions.DeleteOnClose));
        }
        catch (IOException)
        {
            // Held by another process. An unwritable directory would also land
            // here, and refusing to record is the safe reading of both.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose() => _stream.Dispose();
}
