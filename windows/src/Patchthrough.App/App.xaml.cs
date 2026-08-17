using System.Windows;
using Patchthrough.App.Ipc;
using Patchthrough.App.Mvvm;
using Patchthrough.App.Screenshots;
using Patchthrough.App.Shell;
using Patchthrough.App.Tray;
using Patchthrough.App.ViewModels;
using Patchthrough.Core;
using Patchthrough.Windows;

namespace Patchthrough.App;

/// <summary>
/// The application, and the one place everything is wired together.
///
/// There is no window at startup. Patchthrough lives in the tray, records from
/// there, and opens a window when the user asks for one. That shape is why
/// <c>ShutdownMode</c> is explicit in App.xaml: the default quits the app when the
/// last window closes, which would turn closing the window into quitting.
///
/// The console verbs are not here. They belong to Patchthrough.exe, which stays a
/// console application so that `Patchthrough rec` in a terminal keeps behaving
/// like a command: a graphical executable does not hold a shell, and Ctrl+C would
/// no longer reach the recorder.
/// </summary>
public partial class App : Application
{
    private ActivationService? _activation;
    private RecordingService? _recording;
    private TranscriptionHost? _transcription;
    private ShellViewModel? _shell;
    private TrayIconController? _tray;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        UiThread.Capture();

        // The screenshot mode renders the interface and exits. It runs before the
        // single-instance claim on purpose: taking screenshots must work while a
        // real copy is already in the tray, and it never records or transcribes.
        if (ScreenshotHarness.RequestedDirectory is { } screenshots)
        {
            var written = ScreenshotHarness.Run(screenshots);
            Console.Out.WriteLine($"wrote {written} screenshot(s) to {screenshots}");
            Shutdown();
            return;
        }

        // One copy per signed-in user. A second launch tells the first to show
        // itself and then exits, because a tray app with its window closed looks
        // like it failed to start and gets launched again.
        _activation = ActivationService.Claim();
        if (_activation is null)
        {
            Shutdown();
            return;
        }
        _activation.OnActivationRequested(() => UiThread.Post(ShowWindow));

        DispatcherUnhandledException += (_, args) =>
        {
            // A crash while a meeting is being recorded would lose the meeting, so
            // the recording is finalized before anything else. The audio is already
            // on disk; a final meta.json is what makes it transcribable.
            args.Handled = true;
            Recover(args.Exception);
        };

        _recording = new RecordingService();
        _transcription = new TranscriptionHost(() => Config.Load());
        _shell = new ShellViewModel(_recording, _transcription, () => Config.Load());

        _shell.RequestWindow = ShowWindow;
        _tray = new TrayIconController(_shell, ShowWindow, QuitRequested);
        _shell.Start();
    }

    /// <summary>
    /// Bring the window up, creating it the first time. Called from the tray and
    /// from a second launch.
    /// </summary>
    private void ShowWindow()
    {
        if (_shell is null) return;
        if (_window is null)
        {
            _window = new MainWindow(_shell) { OpenSettings = ShowSettings };
        }
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void ShowSettings()
    {
        if (_shell is null) return;

        var executable = Environment.ProcessPath
            ?? Path.Combine(AppContext.BaseDirectory, "PatchthroughApp.exe");
        var settings = new SettingsWindow(new SettingsViewModel(Config.Load(), executable))
        {
            Owner = _window,
        };
        settings.ShowDialog();
        // A saved recordings folder changes where the list reads from, so the
        // window has to re-read rather than wait for a file to change.
        if (settings.Saved) _shell.Refresh();
    }

    private void QuitRequested()
    {
        // Quitting while recording finalizes the session rather than abandoning it.
        // Disposal does that, and it runs before the process goes away.
        Shutdown();
    }

    private void Recover(Exception error)
    {
        try
        {
            _tray?.Notify("Patchthrough: Something went wrong", error.Message);
        }
        catch (Exception)
        {
            // A failed notification cannot be allowed to mask the original fault.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Order matters. The recording is finalized first, so a meeting in progress
        // survives a quit, and the queue is stopped before the engines are released.
        _tray?.Dispose();
        _shell?.Dispose();
        _recording?.Dispose();
        if (_transcription is not null) _transcription.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _activation?.Dispose();
        base.OnExit(e);
    }
}
