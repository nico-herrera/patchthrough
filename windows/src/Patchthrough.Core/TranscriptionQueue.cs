namespace Patchthrough.Core;

/// <summary>What the queue is doing, for a status line.</summary>
public enum TranscriptionQueueState
{
    Idle,
    Transcribing,

    /// <summary>
    /// The last drain finished and at least one session failed. This survives
    /// until the next session starts, so a failure the user did not see is not
    /// erased by the queue going quiet.
    /// </summary>
    Failed,
}

/// <summary>
/// The queue as a status line needs it. <paramref name="SessionName"/> is the
/// folder name being transcribed, or the one that failed, and null when idle.
/// </summary>
public sealed record TranscriptionQueueSnapshot(
    TranscriptionQueueState State,
    string? SessionName,
    int QueuedCount)
{
    public static readonly TranscriptionQueueSnapshot Idle =
        new(TranscriptionQueueState.Idle, null, 0);
}

/// <summary>
/// A serial queue of session directories to transcribe, mirroring
/// TranscriptionCoordinator in Sources/patchthrough/Transcription/.
///
/// It holds no engines and knows no file formats. The work is one delegate,
/// supplied by the platform layer, so this class stays testable anywhere and
/// the ordering rules have one home.
///
/// **Events are raised on the drain's worker thread.** A UI subscriber has to
/// marshal them onto its own thread.
/// </summary>
public sealed class TranscriptionQueue : IAsyncDisposable
{
    private readonly Func<string, CancellationToken, Task> _transcribe;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _gate = new();
    private readonly List<string> _queue = [];

    private bool _draining;
    private string? _current;
    private string? _lastFailure;
    private Task _drain = Task.CompletedTask;
    private TranscriptionQueueSnapshot _snapshot = TranscriptionQueueSnapshot.Idle;

    /// <param name="transcribe">
    /// Transcribes one session directory. Anything it throws is a failure for
    /// that session only and never stops the queue.
    /// </param>
    public TranscriptionQueue(Func<string, CancellationToken, Task> transcribe) =>
        _transcribe = transcribe;

    /// <summary>The current status. Safe to read from any thread.</summary>
    public TranscriptionQueueSnapshot Snapshot
    {
        get { lock (_gate) return _snapshot; }
    }

    /// <summary>Raised whenever <see cref="Snapshot"/> changes.</summary>
    public event Action<TranscriptionQueueSnapshot>? StatusChanged;

    /// <summary>
    /// One session finished. The exception is null on success. A session that
    /// was deleted while queued raises nothing at all, because a delete is a
    /// cancellation rather than a result.
    /// </summary>
    public event Action<string, Exception?>? SessionCompleted;

    /// <summary>
    /// Queue a session and start draining if nothing is draining already.
    ///
    /// A directory that is already queued, or is being transcribed right now,
    /// is ignored. Transcribing one session twice concurrently wastes minutes
    /// of CPU and interleaves two engines' lines in one transcribe.log.
    /// </summary>
    public void Enqueue(string sessionDirectory)
    {
        var full = Path.GetFullPath(sessionDirectory);
        lock (_gate)
        {
            if (_cancellation.IsCancellationRequested) return;
            if (string.Equals(_current, full, StringComparison.Ordinal)) return;
            if (_queue.Contains(full, StringComparer.Ordinal)) return;
            _queue.Add(full);
        }
        StartDraining();
    }

    /// <summary>
    /// Queue every session that was recorded but never transcribed, oldest
    /// first, and return how many were added. This is the launch-time rescan: a
    /// crash or a quit mid-transcription leaves the audio and meta.json in
    /// place, and the filesystem is the queue.
    /// </summary>
    public int EnqueuePending(string root)
    {
        var pending = TranscriptionPipeline.Pending(root);
        var added = 0;
        lock (_gate)
        {
            if (_cancellation.IsCancellationRequested) return 0;
            foreach (var dir in pending)
            {
                var full = Path.GetFullPath(dir);
                if (string.Equals(_current, full, StringComparison.Ordinal)) continue;
                if (_queue.Contains(full, StringComparer.Ordinal)) continue;
                _queue.Add(full);
                added++;
            }
        }
        if (added > 0) StartDraining();
        return added;
    }

    private void StartDraining()
    {
        lock (_gate)
        {
            if (_draining || _queue.Count == 0) return;
            _draining = true;
            // A new drain clears the last failure, so the status line reports
            // the run in front of the user rather than one they already saw.
            _lastFailure = null;
            _drain = Task.Run(DrainAsync);
        }
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            string directory;
            int remaining;
            lock (_gate)
            {
                // The exit decision happens under the lock that Enqueue takes,
                // so a session queued at this exact moment cannot be left
                // sitting until the next one arrives.
                if (_queue.Count == 0 || _cancellation.IsCancellationRequested)
                {
                    _draining = false;
                    _current = null;
                    break;
                }
                directory = _queue[0];
                _queue.RemoveAt(0);
                _current = directory;
                remaining = _queue.Count;
            }

            // A session the user deleted while it sat in the queue is a
            // cancellation, not a failure. Reporting one would tell them to
            // read a transcribe.log inside the folder they just deleted.
            if (!Directory.Exists(directory)) continue;

            var name = new DirectoryInfo(directory).Name;
            Publish(new TranscriptionQueueSnapshot(TranscriptionQueueState.Transcribing, name, remaining));

            try
            {
                await _transcribe(directory, _cancellation.Token);
                SessionCompleted?.Invoke(directory, null);
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
                // Shutting down. The session stays pending on disk and the next
                // launch picks it up through EnqueuePending.
                lock (_gate)
                {
                    _draining = false;
                    _current = null;
                }
                break;
            }
            catch (Exception error)
            {
                // Same reasoning as above, for a delete that landed while the
                // engine was running. Narrower window, identical conclusion.
                if (!Directory.Exists(directory)) continue;
                lock (_gate) _lastFailure = name;
                SessionCompleted?.Invoke(directory, error);
            }
        }

        string? failure;
        lock (_gate) failure = _lastFailure;
        Publish(failure is null
            ? TranscriptionQueueSnapshot.Idle
            : new TranscriptionQueueSnapshot(TranscriptionQueueState.Failed, failure, 0));
    }

    private void Publish(TranscriptionQueueSnapshot snapshot)
    {
        lock (_gate) _snapshot = snapshot;
        StatusChanged?.Invoke(snapshot);
    }

    /// <summary>
    /// How long <see cref="DisposeAsync"/> waits for the session in flight.
    /// </summary>
    public static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Stop the queue and give the session in flight a chance to unwind. The
    /// delegate receives the cancellation, so an engine can stop rather than be
    /// abandoned mid-write.
    ///
    /// The wait is bounded, because a token is a request and not every callee
    /// can honour one: an engine sitting inside a native inference call cannot
    /// observe cancellation until that call returns, which on a long recording
    /// is minutes. Blocking until then would hang Quit. Giving up instead costs
    /// nothing that is not recoverable: every session file is written
    /// atomically, so an abandoned session is still pending on disk and
    /// <see cref="EnqueuePending"/> picks it up on the next launch.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Task drain;
        lock (_gate)
        {
            _queue.Clear();
            drain = _drain;
        }
        await _cancellation.CancelAsync();
        try
        {
            // WhenAny, so a drain that cannot stop does not become a hung app.
            await Task.WhenAny(drain, Task.Delay(ShutdownGrace));
        }
        catch (Exception) { /* a drain that died on the way out cannot fail a shutdown */ }
        _cancellation.Dispose();
    }
}
