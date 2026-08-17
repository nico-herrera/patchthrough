using Patchthrough.Core;

namespace Patchthrough.Windows.Transcription;

/// <summary>
/// Which part of installing a model is running. The phases are separate because
/// they take minutes each and a single percentage across all three would stall
/// twice with no explanation.
/// </summary>
public enum ModelInstallPhase
{
    Downloading,

    /// <summary>Hashing against the pinned SHA-256. Minutes on a 480 MB archive.</summary>
    Verifying,

    /// <summary>Unpacking the archive, which reports no byte counts.</summary>
    Extracting,
}

/// <summary>
/// How far a model install has got. <paramref name="TotalBytes"/> is zero when
/// the phase has nothing to count, which <see cref="ModelInstallPhase.Extracting"/>
/// does not.
/// </summary>
public sealed record ModelInstallProgress(ModelInstallPhase Phase, long BytesReceived, long TotalBytes)
{
    /// <summary>0 to 1, or null when this phase cannot be measured.</summary>
    public double? Fraction => TotalBytes > 0
        ? Math.Clamp((double)BytesReceived / TotalBytes, 0, 1)
        : null;
}

/// <summary>
/// Makes sure the models an engine needs are on disk before it is asked to
/// prepare.
///
/// The engines download their own models when they prepare, which is right for
/// the console verbs: the line "transcribing mic.m4a" is followed by a wait. A
/// window cannot do that. It has to say a 600 MB download is happening, and it
/// needs the download to be a step it can watch rather than a side effect
/// hidden inside PrepareAsync.
/// </summary>
public static class ModelProvisioning
{
    /// <summary>
    /// Download and verify whatever the named engines need. Already-installed
    /// models are checked and returned quickly.
    /// </summary>
    public static async Task EnsureAsync(
        IEnumerable<string> engineNames,
        IProgress<ModelInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        foreach (var name in engineNames)
        {
            switch (name.ToLowerInvariant())
            {
                case EngineCatalog.Parakeet:
                    await ModelStore.Default.EnsureAsync(progress, cancellationToken);
                    break;
                case EngineCatalog.Whisper:
                    await WhisperModelStore.Default.EnsureAsync(progress, cancellationToken);
                    break;
                default:
                    throw new InvalidOperationException($"unknown transcription engine: {name}");
            }
        }
    }

    /// <summary>
    /// Whether anything would download, so a caller can skip a progress surface
    /// entirely on every launch after the first.
    /// </summary>
    public static bool NeedsDownload(Config config)
    {
        var names = EngineCatalog.Select(config);
        if (names.Contains(EngineCatalog.Parakeet) && ModelStore.Default.Missing().Count > 0) return true;
        return names.Contains(EngineCatalog.Whisper) && !File.Exists(WhisperModelStore.Default.Path);
    }
}
