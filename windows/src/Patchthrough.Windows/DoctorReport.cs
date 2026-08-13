using NAudio.CoreAudioApi;
using Patchthrough.Core;
using Patchthrough.Windows.Transcription;

namespace Patchthrough.Windows;

/// <summary>
/// How serious a check's result is.
/// </summary>
public enum DoctorSeverity
{
    /// <summary>Working, or simply information.</summary>
    Ok,

    /// <summary>
    /// Not ready yet, and it will fix itself. A model that has not been
    /// downloaded is the case this exists for: reporting it as broken sends the
    /// user looking for a fault on a machine that is fine.
    /// </summary>
    Pending,

    /// <summary>Recording will not work until the user does something.</summary>
    Failed,
}

/// <summary>
/// One line of the machine check. <paramref name="Remedy"/> is the sentence that
/// tells the user what to do, and it is null when there is nothing to do.
/// </summary>
public sealed record DoctorCheck(DoctorSeverity Severity, string Label, string Detail, string? Remedy = null);

/// <summary>
/// What this machine can do, as data.
///
/// The console verb renders these as text and the settings surface renders the
/// same list, so a machine problem reads the same in both places. Collecting
/// never throws: a check that cannot run is a failed check, not a failed report.
/// </summary>
public static class DoctorReport
{
    public static IReadOnlyList<DoctorCheck> Collect(string recordingsRoot, Config config)
    {
        var checks = new List<DoctorCheck>
        {
            new(Directory.Exists(recordingsRoot) ? DoctorSeverity.Ok : DoctorSeverity.Pending,
                "recordings", recordingsRoot,
                Directory.Exists(recordingsRoot) ? null : "The folder is created when you record."),
            new(DoctorSeverity.Ok, "config", Config.DefaultPath),
        };

        // A capture device is the microphone. Windows gates microphone access for
        // desktop applications behind one privacy setting, and a denied device
        // reports as absent here.
        var devices = new MMDeviceEnumerator();
        var capture = CountEndpoints(devices, DataFlow.Capture);
        checks.Add(new(
            capture > 0 ? DoctorSeverity.Ok : DoctorSeverity.Failed,
            "microphone", $"{capture} capture device(s)",
            capture > 0 ? null : "No capture device. Check Settings, Privacy & security, Microphone."));

        // Loopback needs no permission on Windows. It needs something to play into.
        var render = CountEndpoints(devices, DataFlow.Render);
        checks.Add(new(
            render > 0 ? DoctorSeverity.Ok : DoctorSeverity.Failed,
            "system audio", $"{render} playback device(s)",
            render > 0 ? null : "No playback device, so there is nothing to capture."));

        checks.AddRange(TranscriptionChecks(config));

        // A pending session is one the CLI cannot hand off yet.
        var pending = TranscriptionPipeline.Pending(recordingsRoot).Count;
        if (pending > 0)
        {
            checks.Add(new(DoctorSeverity.Pending, "pending", $"{pending} session(s) not transcribed",
                "Run: Patchthrough transcribe"));
        }

        return checks;
    }

    /// <summary>True when nothing blocks a recording.</summary>
    public static bool CanRecord(IEnumerable<DoctorCheck> checks) =>
        checks.All(check => check.Severity != DoctorSeverity.Failed);

    private static IEnumerable<DoctorCheck> TranscriptionChecks(Config config)
    {
        if (!config.TranscriptionEnabled)
        {
            yield return new(DoctorSeverity.Ok, "transcription", "disabled in the config");
            yield break;
        }

        var names = EngineCatalog.Select(config);
        yield return new(DoctorSeverity.Ok, "transcription",
            $"{string.Join(" + ", names)} ({config.TranscriptionQualityMode})");

        // A model that has not downloaded yet is not a broken machine, so these
        // report as pending and say where the download lands.
        if (names.Contains(EngineCatalog.Parakeet) && ModelStore.Default.Missing().Count > 0)
        {
            yield return new(DoctorSeverity.Pending, "model",
                $"Parakeet will download and verify on first use ({ModelStore.Default.Directory})");
        }
        if (names.Contains(EngineCatalog.Whisper) && !File.Exists(WhisperModelStore.Default.Path))
        {
            yield return new(DoctorSeverity.Pending, "model",
                $"Whisper will download and verify on first use ({WhisperModelStore.Default.Directory})");
        }
    }

    private static int CountEndpoints(MMDeviceEnumerator devices, DataFlow flow)
    {
        try
        {
            return devices.EnumerateAudioEndPoints(flow, DeviceState.Active).Count;
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
