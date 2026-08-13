namespace Patchthrough.Windows;

/// <summary>The session being recorded now.</summary>
public sealed record RecordingSessionInfo(string Directory, DateTimeOffset StartedAt)
{
    /// <summary>The folder name, which is the session's identity everywhere.</summary>
    public string Id => new DirectoryInfo(Directory).Name;
}

/// <summary>
/// Start and stop recording, for a caller that lives longer than one meeting.
///
/// <see cref="Recorder"/> is one meeting and holds two open capture devices.
/// This owns the sequence of them, so a tray icon or a console verb has a
/// single object to hold and one place to ask what is happening.
///
/// No elapsed timer lives here. A display ticks itself from
/// <see cref="RecordingSessionInfo.StartedAt"/>, which keeps the service free of
/// anything that has to be marshalled onto a UI thread.
/// </summary>
public sealed class RecordingService : IDisposable
{
    private readonly object _gate = new();
    private Recorder? _recorder;
    private RecordingLock? _lock;

    /// <summary>A meeting is being captured now.</summary>
    public bool IsRecording
    {
        get { lock (_gate) return _recorder?.IsRecording ?? false; }
    }

    /// <summary>The session being recorded, or null when idle.</summary>
    public RecordingSessionInfo? Current
    {
        get
        {
            lock (_gate)
            {
                return _recorder is null || !_recorder.IsRecording
                    ? null
                    : new RecordingSessionInfo(_recorder.Directory, _recorder.StartedAt);
            }
        }
    }

    /// <summary>
    /// A track stopped on its own, named "mic" or "system". The meeting keeps
    /// recording: one track surviving is worth more than neither.
    ///
    /// **Raised on the capture thread.**
    /// </summary>
    public event Action<string, Exception>? TrackFailed;

    /// <summary>
    /// Begin a meeting and return where it is being written.
    ///
    /// Throws when a capture device cannot be opened, with nothing left behind:
    /// half a session recorded silently is worse than a clear failure. A caller
    /// that already has a meeting running gets an
    /// <see cref="InvalidOperationException"/> rather than a second recorder
    /// competing for the same microphone.
    /// </summary>
    public RecordingSessionInfo Start(string recordingsRoot)
    {
        lock (_gate)
        {
            if (_recorder is not null && _recorder.IsRecording)
            {
                throw new InvalidOperationException("a recording is already running");
            }

            // One recording per machine. Without this the tray app and a
            // `Patchthrough rec` in a terminal both capture the same meeting
            // into two session folders, and neither one fails.
            var held = RecordingLock.TryAcquire()
                ?? throw new InvalidOperationException(
                    "another Patchthrough is already recording on this machine");

            Directory.CreateDirectory(recordingsRoot);
            var recorder = new Recorder(recordingsRoot);
            recorder.TrackFailed += OnTrackFailed;
            try
            {
                recorder.Start();
            }
            catch
            {
                recorder.TrackFailed -= OnTrackFailed;
                recorder.Dispose();
                held.Dispose();
                throw;
            }

            // The previous recorder is only released once the new one is
            // running, so a failed start leaves the service exactly as it was.
            ReleaseLocked();
            _recorder = recorder;
            _lock = held;
            return new RecordingSessionInfo(recorder.Directory, recorder.StartedAt);
        }
    }

    /// <summary>
    /// End the meeting and return the session directory.
    ///
    /// This encodes both tracks before it returns, which takes seconds on a long
    /// meeting. **Call it off a UI thread**, or the window freezes while a
    /// recording is being finalized.
    /// </summary>
    public string Stop(string? name = null)
    {
        Recorder recorder;
        lock (_gate)
        {
            recorder = _recorder ?? throw new InvalidOperationException("nothing is recording");
        }

        try
        {
            // Deliberately outside the lock: encoding is slow, and IsRecording
            // has to stay readable by a status line while it runs.
            recorder.Stop(name);
        }
        finally
        {
            // Released even on a failed finalize, or the machine stays unable to
            // record until the process exits.
            lock (_gate)
            {
                _lock?.Dispose();
                _lock = null;
            }
        }
        return recorder.Directory;
    }

    private void OnTrackFailed(string track, Exception error) => TrackFailed?.Invoke(track, error);

    /// <summary>
    /// Stop a recording in progress so a quit does not abandon one. The session
    /// is finalized, not discarded: the audio is already on disk either way, and
    /// a final meta.json is what makes it transcribable.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_recorder is not null && _recorder.IsRecording)
            {
                try { _recorder.Stop(); }
                catch (Exception) { /* a failed finalize cannot fail a shutdown */ }
            }
            ReleaseLocked();
        }
    }

    private void ReleaseLocked()
    {
        _lock?.Dispose();
        _lock = null;
        if (_recorder is null) return;
        _recorder.TrackFailed -= OnTrackFailed;
        _recorder.Dispose();
        _recorder = null;
    }
}
