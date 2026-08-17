using Patchthrough.Core;
using Patchthrough.Windows;
using Patchthrough.Windows.Transcription;
using System.Text.Json;

// The first Windows milestone is a console recorder. It writes the session
// format, and the npm CLI does the handoff. The tray application comes later.
//
//   Patchthrough rec [--out <dir>] [--name <title>]
//   Patchthrough transcribe [--out <dir>]
//   Patchthrough doctor [--out <dir>]

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    var verb = args.FirstOrDefault() ?? "help";
    var options = ParseOptions(args.Skip(1));
    var config = Config.Load();
    var root = config.ResolveRecordingsRoot(options.GetValueOrDefault("out"));

    switch (verb)
    {
        case "rec":
            var directory = Record(root, options.GetValueOrDefault("name"));
            if (!config.TranscriptionEnabled)
            {
                Console.Error.WriteLine("transcription is disabled in the config");
            }
            else
            {
                await TranscribeAsync([directory], config, options.GetValueOrDefault("project"));
            }
            Console.WriteLine(directory);
            return 0;

        case "transcribe":
            // Anything recorded but never transcribed, oldest first. A crash
            // mid-transcription therefore costs nothing but a rerun.
            var pending = TranscriptionPipeline.Pending(root);
            if (pending.Count == 0)
            {
                Console.Error.WriteLine($"nothing pending in {root}");
                return 0;
            }
            Console.Error.WriteLine($"{pending.Count} session(s) to transcribe");
            return await TranscribeAsync(pending, config, options.GetValueOrDefault("project"));

        case "doctor":
            return Doctor(root, config);

        case "benchmark":
            return await BenchmarkAsync(options);

        default:
            Console.WriteLine("""
            Patchthrough for Windows

              Patchthrough rec [--out <dir>] [--name <title>] [--project <dir>]   record a meeting
              Patchthrough transcribe [--out <dir>] [--project <dir>]             transcribe what is pending
              Patchthrough doctor [--out <dir>]                 check this machine
              Patchthrough benchmark --audio <file> [--engine parakeet|whisper]

            Recording writes a session that the npm CLI hands to an agent:

              npm i -g patchthrough
              patchthrough hand claude
            """);
            return 0;
    }
}

static async Task<int> BenchmarkAsync(IReadOnlyDictionary<string, string> options)
{
    if (!options.TryGetValue("audio", out var audio) || string.IsNullOrWhiteSpace(audio))
        throw new ArgumentException("benchmark requires --audio <file>");
    if (!File.Exists(audio)) throw new FileNotFoundException("benchmark audio does not exist", audio);
    var name = options.GetValueOrDefault("engine", EngineCatalog.Parakeet).ToLowerInvariant();
    if (!EngineCatalog.Known.Contains(name))
    {
        throw new ArgumentException("engine must be parakeet or whisper");
    }
    var engine = EngineCatalog.Create(name);
    var quality = options.GetValueOrDefault("quality", "standard") switch
    {
        "standard" => QualityMode.Standard,
        "max_accuracy" => QualityMode.MaxAccuracy,
        _ => throw new ArgumentException("quality must be standard or max_accuracy"),
    };
    try
    {
        await engine.PrepareAsync();
        var context = new TranscriptionContext(
            quality,
            ProjectVocabulary.Collect(options.GetValueOrDefault("project")));
        var transcript = await engine.TranscribeAsync(Path.GetFullPath(audio), context);
        var json = JsonSerializer.Serialize(transcript, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = true,
        }) + Environment.NewLine;
        if (options.TryGetValue("output", out var output) && !string.IsNullOrWhiteSpace(output))
            await File.WriteAllTextAsync(output, json);
        else
            Console.Write(json);
        return 0;
    }
    finally
    {
        await engine.DisposeAsync();
    }
}

/// <summary>
/// Transcribe each session. A session that fails keeps its audio and its
/// meta.json, so `transcribe` picks it up again later.
/// </summary>
static async Task<int> TranscribeAsync(
    IReadOnlyList<string> sessions,
    Config config,
    string? projectOverride)
{
    // One transcriber for the whole run, so a model loads once rather than per
    // session.
    await using var transcriber = SessionTranscriber.Create(config, projectOverride);
    var failed = 0;
    foreach (var session in sessions)
    {
        try { await transcriber.RunAsync(session); }
        catch (Exception error)
        {
            failed++;
            Console.Error.WriteLine($"{Path.GetFileName(session)}: {error.Message}");
        }
    }
    return failed == 0 ? 0 : 1;
}

static string Record(string root, string? name)
{
    using var recording = new RecordingService();

    // A device that dies mid-meeting is reported as it happens, not at stop. An
    // hour of silence the user could have fixed in seconds is the failure worth
    // interrupting for.
    recording.TrackFailed += (track, error) =>
        Console.Error.WriteLine($"warning: the {track} track stopped early: {error.Message}");

    // Ctrl+C has to stop the recording rather than kill the process, or the
    // audio stays on disk with a provisional meta.json and no transcript.
    var stopping = new ManualResetEventSlim(false);
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        stopping.Set();
    };

    var session = recording.Start(root);
    Console.Error.WriteLine($"recording to {session.Directory}");
    Console.Error.WriteLine("press Ctrl+C or Enter to stop");

    var reader = Task.Run(() => Console.ReadLine());
    WaitHandle.WaitAny([stopping.WaitHandle, ((IAsyncResult)reader).AsyncWaitHandle]);
    var directory = recording.Stop(name);
    Console.Error.WriteLine("stopped");
    return directory;
}

static int Doctor(string root, Config config)
{
    var checks = DoctorReport.Collect(root, config);
    foreach (var check in checks)
    {
        Console.WriteLine($"{Mark(check.Severity)} {check.Label,-12} {check.Detail}");
        // A remedy prints for anything that is not already fine, which covers
        // both a fault to fix and a pending session to transcribe. A check that
        // passes has nothing to add.
        if (check.Severity != DoctorSeverity.Ok && check.Remedy is not null)
        {
            Console.WriteLine($"  {check.Remedy}");
        }
    }
    return DoctorReport.CanRecord(checks) ? 0 : 1;
}

static string Mark(DoctorSeverity severity) => severity == DoctorSeverity.Ok ? "\u2713" : "\u25cb";

static Dictionary<string, string> ParseOptions(IEnumerable<string> args)
{
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    string? pending = null;
    foreach (var arg in args)
    {
        if (arg.StartsWith("--", StringComparison.Ordinal))
        {
            pending = arg[2..];
            options[pending] = "";
        }
        else if (pending is not null)
        {
            options[pending] = arg;
            pending = null;
        }
    }
    return options;
}
