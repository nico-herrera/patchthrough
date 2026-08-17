using Patchthrough.Core;

namespace Patchthrough.Windows.Transcription;

/// <summary>
/// The one place an engine name becomes an engine.
///
/// The mapping used to sit inline in two places, and a third caller would have
/// been a third copy. A name that reaches here and is not known is a
/// configuration error, so it throws rather than falling back: silently
/// recording with an engine the user did not ask for is worse than a message.
/// </summary>
public static class EngineCatalog
{
    public const string Parakeet = "parakeet";
    public const string Whisper = "whisper";

    /// <summary>The engines this build can run, for a settings chooser.</summary>
    public static IReadOnlyList<string> Known => [Parakeet, Whisper];

    public static ITranscriptionEngine Create(string name) => name.ToLowerInvariant() switch
    {
        Parakeet => new ParakeetEngine(),
        Whisper => new WhisperEngine(),
        _ => throw new InvalidOperationException($"unknown transcription engine: {name}"),
    };

    /// <summary>
    /// Which engines this machine's config and quality profile select. Windows
    /// keeps both quality modes on the recoverable Parakeet path unless the
    /// profile carries release-qualified evidence for something else, which is
    /// <see cref="QualityProfile.Engines"/>'s decision and not this class's.
    /// </summary>
    public static IReadOnlyList<string> Select(Config config, QualityProfile? profile = null) =>
        (profile ?? QualityProfile.Load()).Engines(config.TranscriptionEngine, config.TranscriptionQualityMode);
}
