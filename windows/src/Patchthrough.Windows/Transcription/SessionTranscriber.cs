using Patchthrough.Core;

namespace Patchthrough.Windows.Transcription;

/// <summary>
/// Everything needed to transcribe sessions on this machine: the quality
/// profile, the engines, the project vocabulary, and the pipeline over them.
///
/// One instance holds its engines open, so a run of several sessions loads a
/// model once. That matters: Parakeet is hundreds of megabytes and loading it
/// per session would cost more than the transcription. Dispose releases them.
/// </summary>
public sealed class SessionTranscriber : IAsyncDisposable
{
    private readonly IReadOnlyList<ITranscriptionEngine> _engines;
    private readonly TranscriptionPipeline _pipeline;

    private SessionTranscriber(IReadOnlyList<ITranscriptionEngine> engines, TranscriptionPipeline pipeline)
    {
        _engines = engines;
        _pipeline = pipeline;
    }

    /// <summary>
    /// Build the pipeline this config asks for.
    /// </summary>
    /// <param name="projectOverride">
    /// A project directory from the command line, which wins over the config.
    /// Its manifests and glossaries bias the engine toward the project's own
    /// vocabulary.
    /// </param>
    /// <param name="log">
    /// Where per-track progress lines go. Null means stderr, which is what the
    /// console verbs want. Every line is also appended to the session's
    /// transcribe.log either way.
    /// </param>
    public static SessionTranscriber Create(
        Config config,
        string? projectOverride = null,
        TextWriter? log = null)
    {
        var profile = QualityProfile.Load();
        var mode = config.TranscriptionQualityMode;
        var engines = EngineCatalog.Select(config, profile).Select(EngineCatalog.Create).ToList();

        var project = !string.IsNullOrWhiteSpace(projectOverride)
            ? Config.ExpandHome(projectOverride)
            : config.TranscriptionProjectDirectory;
        var context = new TranscriptionContext(mode, ProjectVocabulary.Collect(project));

        return new SessionTranscriber(
            engines,
            new TranscriptionPipeline(engines, mode, profile, context, config.DedupMicEcho, log));
    }

    /// <summary>
    /// Transcribe one session. Anything this throws leaves the audio and
    /// meta.json in place, so the session stays pending and can be retried.
    /// </summary>
    public Task RunAsync(string sessionDirectory, CancellationToken cancellationToken = default) =>
        _pipeline.RunAsync(sessionDirectory, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        foreach (var engine in _engines) await engine.DisposeAsync();
    }
}
