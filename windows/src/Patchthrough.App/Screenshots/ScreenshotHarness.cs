using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Patchthrough.App.Shell;
using Patchthrough.App.ViewModels;
using Patchthrough.Core;
using Patchthrough.Windows;

namespace Patchthrough.App.Screenshots;

/// <summary>
/// Renders the interface to PNG files, so a change to it can be reviewed.
///
/// This exists because the interface cannot be checked where it is written.
/// Development happens on a Mac, where this project compiles but WPF cannot run,
/// and a compile says nothing about whether a pane is laid out correctly, whether
/// the palette survived the port, or whether a fractional type size was rounded.
/// Windows CI can run it, so CI is where the screenshots come from.
///
/// It is reached through an environment variable rather than a command line verb,
/// matching the PATCHTHROUGH_DEBUG hooks the macOS app uses, and deliberately not
/// through a no-argument launch: the release verifier starts the executables with
/// no arguments and a mode that waited for a user would hang it.
///
/// The sessions are fixtures written by the real writers in Patchthrough.Core, so
/// the screenshots show the formats the app actually reads.
///
/// **The live recording pane is not captured.** That state exists only while a
/// recording is running, which needs real capture devices, and faking it would mean
/// exposing a way to set the live session that nothing else needs. It is verified by
/// hand on the acceptance checklist instead.
/// </summary>
internal static class ScreenshotHarness
{
    private const string DirectoryVariable = "PATCHTHROUGH_UI_SCREENSHOT";

    /// <summary>Where to write, or null when the app should start normally.</summary>
    public static string? RequestedDirectory =>
        Environment.GetEnvironmentVariable(DirectoryVariable) is { Length: > 0 } path ? path : null;

    /// <summary>
    /// Write one PNG per surface and return how many were written. Every session
    /// the fixtures create is dated relative to today, so the sidebar's date
    /// headers read "Today" and "Yesterday" on any day the harness runs.
    /// </summary>
    public static int Run(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var recordings = Path.Combine(Path.GetTempPath(), "pt-ui-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(recordings);

        try
        {
            var sessions = WriteFixtures(recordings);

            using var recording = new RecordingService();
            TranscriptionHost? transcription = null;
            try
            {
                // A config that points at the fixtures, written through the real
                // reader so nothing touches the recordings folder of whoever runs
                // this and nothing bypasses the parsing the app actually uses.
                var configPath = Path.Combine(recordings, "config.json");
                File.WriteAllText(configPath,
                    $"{{ \"recordings_dir\": {System.Text.Json.JsonSerializer.Serialize(recordings)} }}");
                var config = Config.Load(configPath);
                transcription = new TranscriptionHost(() => config);
                var shell = new ShellViewModel(recording, transcription, () => config);
                shell.Refresh();

                var written = 0;
                var window = new MainWindow(shell);
                window.Left = -4000;   // off to the side, so a real desktop does not flash
                window.Top = -4000;
                window.Width = Theme.PT.M.WindowDefaultWidth;
                window.Height = Theme.PT.M.WindowDefaultHeight;
                window.Show();

                foreach (var (name, id) in sessions)
                {
                    shell.Selected = shell.Groups
                        .SelectMany(group => group.Sessions)
                        .FirstOrDefault(item => item.Id == id);
                    Capture(window, Path.Combine(outputDirectory, $"window-{name}.png"));
                    written++;
                }

                // The empty state, which is what a first run shows.
                shell.Search = "nothing matches this";
                Capture(window, Path.Combine(outputDirectory, "window-no-matches.png"));
                written++;
                shell.Search = "";

                window.Hide();

                var settings = new SettingsWindow(new SettingsViewModel(config, "PatchthroughApp.exe"))
                {
                    Left = -4000,
                    Top = -4000,
                };
                settings.Show();
                Capture(settings, Path.Combine(outputDirectory, "settings.png"));
                written++;
                settings.Hide();

                shell.Dispose();
                return written;
            }
            finally
            {
                transcription?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        finally
        {
            try { Directory.Delete(recordings, recursive: true); }
            catch (IOException) { /* a leftover temp directory is not a failure */ }
        }
    }

    /// <summary>
    /// One session per state the detail pane can show, so a reviewer sees all of
    /// them rather than only the one that happened to be selected.
    /// </summary>
    private static List<(string Name, string Id)> WriteFixtures(string root)
    {
        var today = DateTimeOffset.Now;
        var yesterday = today.AddDays(-1);

        var ready = Session(root, today.AddHours(-2), name: "Windows port kickoff", transcript: true);
        var readyUnnamed = Session(root, today.AddHours(-4), name: null, transcript: true);
        var pending = Session(root, yesterday, name: null, transcript: false);
        var empty = Session(root, yesterday.AddHours(-1), name: null, transcript: false, completionMarker: true);
        var broken = Path.Combine(root, yesterday.AddHours(-2).ToLocalTime().ToString("yyyy.MM.dd-HHmm"));
        Directory.CreateDirectory(broken);

        return
        [
            ("transcript", new DirectoryInfo(ready).Name),
            ("transcript-unnamed", new DirectoryInfo(readyUnnamed).Name),
            ("pending", new DirectoryInfo(pending).Name),
            ("empty", new DirectoryInfo(empty).Name),
            ("broken", new DirectoryInfo(broken).Name),
        ];
    }

    private static string Session(
        string root,
        DateTimeOffset startedAt,
        string? name,
        bool transcript,
        bool completionMarker = false)
    {
        var writer = SessionWriter.Create(root, startedAt);
        writer.AddTrack("mic", "mic.m4a");
        writer.AddTrack("system", "system.m4a", offsetMs: 12);
        writer.WriteFinalMeta(startedAt.AddSeconds(1832), name);

        if (transcript)
        {
            new Transcript
            {
                Engine = "parakeet",
                Model = "parakeet-tdt-0.6b-v2",
                CreatedAt = startedAt,
                Segments =
                [
                    new Segment("me", 1_000, 6_400,
                        "Let's get the tray app standing before we touch the handoff. I want start and stop working from the tray, and the sessions list reading real transcripts."),
                    new Segment("them", 7_200, 13_800,
                        "Agreed. One thing to watch is the recording state: if the tray icon is the only place it shows, someone with a full notification area will lose it."),
                    new Segment("me", 14_500, 18_900,
                        "The window carries a second record control for exactly that reason. Both call the same toggle."),
                    new Segment("them", 19_400, 27_100,
                        "Then the sidebar needs to tell a pending session apart from one that found no speech. Those look identical on disk and they mean different things to whoever is waiting."),
                    new Segment("me", 27_900, 31_200,
                        "That is what the completion marker is for. I will make each one say its own sentence."),
                ],
            }.Write(writer.Directory);
            HandoffDocument.Write(writer.Directory, 1832, cleanStop: true, name: name);
        }
        else if (completionMarker)
        {
            // Transcription ran and found nothing. The marker is what separates
            // this from a session still waiting for its turn.
            File.WriteAllText(Path.Combine(writer.Directory, "transcript.json"),
                """{ "segments": [] }""");
        }

        return writer.Directory;
    }

    /// <summary>
    /// Render a window to a PNG. The bitmap is sRGB, so the colours in the file are
    /// the token values and can be compared against design/SPEC.md directly. On
    /// macOS the same check needs a colour-space conversion first; here it does not.
    /// </summary>
    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        // Let bindings and the layout pass settle before the frame is taken.
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
            () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);

        var width = (int)Math.Ceiling(window.ActualWidth);
        var height = (int)Math.Ceiling(window.ActualHeight);
        if (width <= 0 || height <= 0) return;

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
