using Patchthrough.Core;
using Patchthrough.Windows.Transcription;

namespace Patchthrough.Windows;

/// <summary>
/// The transcription queue with engines attached.
///
/// <see cref="TranscriptionQueue"/> knows about ordering and status and nothing
/// about models. This supplies the work: it makes sure the models are on disk,
/// loads the engines once for a run of sessions, and releases them when the queue
/// goes quiet. Holding a loaded Parakeet costs hundreds of megabytes, so it is
/// not kept between drains.
///
/// **Events are raised on the drain's worker thread.** A window marshals them.
/// </summary>
public sealed class TranscriptionHost : IAsyncDisposable
{
    private readonly Func<Config> _config;
    private readonly TranscriptionQueue _queue;
    private readonly object _gate = new();
    private SessionTranscriber? _transcriber;

    /// <param name="config">
    /// Read at the start of each drain rather than captured once, so a settings
    /// change takes effect on the next session instead of the next launch.
    /// </param>
    public TranscriptionHost(Func<Config> config)
    {
        _config = config;
        _queue = new TranscriptionQueue(TranscribeAsync);
        _queue.StatusChanged += OnStatusChanged;
        _queue.SessionCompleted += (directory, error) => SessionCompleted?.Invoke(directory, error);
    }

    public TranscriptionQueueSnapshot Status => _queue.Snapshot;

    public event Action<TranscriptionQueueSnapshot>? StatusChanged;

    public event Action<string, Exception?>? SessionCompleted;

    /// <summary>
    /// A model is being downloaded, verified, or unpacked. On a first run this is
    /// around 600 MB and several minutes, so it needs somewhere to be shown.
    /// </summary>
    public event Action<ModelInstallProgress>? ModelProgress;

    /// <summary>
    /// Queue a finished session. With transcription switched off in the config
    /// this does nothing, and the session stays on disk for `transcribe` later.
    /// </summary>
    public void Enqueue(string sessionDirectory)
    {
        if (!_config().TranscriptionEnabled) return;
        _queue.Enqueue(sessionDirectory);
    }

    /// <summary>
    /// Queue everything recorded but never transcribed. This is the launch-time
    /// rescan: a crash or a quit mid-transcription leaves the work on disk.
    /// </summary>
    public int EnqueuePending(string recordingsRoot) =>
        _config().TranscriptionEnabled ? _queue.EnqueuePending(recordingsRoot) : 0;

    private async Task TranscribeAsync(string sessionDirectory, CancellationToken cancellationToken)
    {
        var config = _config();

        // Ahead of the engines, so a first run reports a download rather than
        // sitting silent inside PrepareAsync for several minutes.
        if (ModelProvisioning.NeedsDownload(config))
        {
            var progress = new Progress<ModelInstallProgress>(report => ModelProgress?.Invoke(report));
            await ModelProvisioning.EnsureAsync(EngineCatalog.Select(config), progress, cancellationToken);
        }

        SessionTranscriber transcriber;
        lock (_gate)
        {
            // One transcriber per drain. The queue is serial, so there is never
            // more than one caller here at a time.
            _transcriber ??= SessionTranscriber.Create(config);
            transcriber = _transcriber;
        }
        await transcriber.RunAsync(sessionDirectory, cancellationToken);
    }

    private void OnStatusChanged(TranscriptionQueueSnapshot snapshot)
    {
        // The drain has finished, so the models can go. Anything queued after
        // this loads them again, which is cheaper than holding them all day.
        if (snapshot.State != TranscriptionQueueState.Transcribing) ReleaseEngines();
        StatusChanged?.Invoke(snapshot);
    }

    private void ReleaseEngines()
    {
        SessionTranscriber? transcriber;
        lock (_gate)
        {
            transcriber = _transcriber;
            _transcriber = null;
        }
        if (transcriber is null) return;
        // Fire and forget: releasing a model must not hold up a status update the
        // user is waiting to see.
        _ = Task.Run(async () =>
        {
            try { await transcriber.DisposeAsync(); }
            catch (Exception) { /* a failed release cannot fail the queue */ }
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _queue.DisposeAsync();
        SessionTranscriber? transcriber;
        lock (_gate)
        {
            transcriber = _transcriber;
            _transcriber = null;
        }
        if (transcriber is not null) await transcriber.DisposeAsync();
    }
}
