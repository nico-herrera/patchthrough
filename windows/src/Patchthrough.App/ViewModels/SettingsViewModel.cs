using Patchthrough.App.Mvvm;
using Patchthrough.Core;
using Patchthrough.Windows;
using Patchthrough.Windows.Transcription;

namespace Patchthrough.App.ViewModels;

/// <summary>
/// The settings sheet.
///
/// Edits are staged and only written on Save, so Cancel really cancels. What gets
/// written is only what differs from a default: the config file is shared with the
/// macOS app and the npm CLI, and a file full of restated defaults would freeze
/// this build's defaults into every other reader.
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly Config _loaded;
    private readonly string _executablePath;

    private string _recordingsDirectory;
    private bool _launchAtLogin;
    private bool _transcriptionEnabled;
    private bool _maxAccuracy;
    private string _engine;
    private string? _projectsDirectory;
    private bool _openWindowOnRecord;

    public SettingsViewModel(Config config, string executablePath)
    {
        _loaded = config;
        _executablePath = executablePath;

        _recordingsDirectory = config.ResolveRecordingsRoot();
        _transcriptionEnabled = config.TranscriptionEnabled;
        _maxAccuracy = config.TranscriptionQualityMode == QualityMode.MaxAccuracy;
        _engine = config.TranscriptionEngine;
        _projectsDirectory = config.TranscriptionProjectDirectory;
        _openWindowOnRecord = config.NotesOpenWindowOnRecord;
        _launchAtLogin = LoginLaunch.IsEnabled();

        Profile = QualityProfile.Load();
    }

    /// <summary>Where the config being edited lives, shown in the header.</summary>
    public string ConfigPath => Config.DefaultPath;

    public QualityProfile Profile { get; }

    // ------------------------------------------------------------- recordings

    public string RecordingsDirectory
    {
        get => _recordingsDirectory;
        set => Set(ref _recordingsDirectory, value);
    }

    /// <summary>
    /// A tradeoff, not a restatement: the next recording moves, the existing ones
    /// do not.
    /// </summary>
    public string RecordingsCaption =>
        "Applies to the next recording. Existing sessions stay where they are.";

    public bool LaunchAtLogin
    {
        get => _launchAtLogin;
        set => Set(ref _launchAtLogin, value);
    }

    public string LaunchAtLoginCaption =>
        "Keeps the recorder ready in the tray, so a meeting can start without a launch first.";

    // ---------------------------------------------------------- transcription

    public bool TranscriptionEnabled
    {
        get => _transcriptionEnabled;
        set => Set(ref _transcriptionEnabled, value);
    }

    public string TranscriptionCaption => "On this PC, about 20 seconds per hour of audio.";

    public bool MaxAccuracy
    {
        get => _maxAccuracy;
        set => Set(ref _maxAccuracy, value);
    }

    /// <summary>
    /// Max Accuracy is only offered when the checked-in quality profile carries
    /// release-qualified evidence for it. Without that the choice would claim an
    /// accuracy gain nothing has measured, so the control is disabled rather than
    /// silently doing the same thing as Standard.
    /// </summary>
    public bool MaxAccuracyAvailable => Profile.CanRunConsensus;

    public string QualityCaption => MaxAccuracyAvailable
        ? "Two complementary engines, and up to 5 processing minutes per recorded hour."
        : "Standard is the only qualified mode on this machine. Max Accuracy needs measured evidence that it is better.";

    /// <summary>The engines a user may pick, plus letting the profile decide.</summary>
    public IReadOnlyList<string> Engines { get; } = ["auto", .. EngineCatalog.Known];

    public string Engine
    {
        get => _engine;
        set => Set(ref _engine, value);
    }

    public string EngineCaption =>
        "Auto uses the best engine this machine qualifies for. A named engine overrides that.";

    // ------------------------------------------------------------------ notes

    public bool OpenWindowOnRecord
    {
        get => _openWindowOnRecord;
        set => Set(ref _openWindowOnRecord, value);
    }

    /// <summary>
    /// The tradeoff, not a restatement: the field is in the window, so without this
    /// there is nowhere to type during a meeting.
    /// </summary>
    public string OpenWindowCaption =>
        "The note field is in the window, not the tray, so notes need it open.";

    // --------------------------------------------------------------- projects

    /// <summary>Where a repository picker starts when patching through to an agent.</summary>
    public string? ProjectsDirectory
    {
        get => _projectsDirectory;
        set => Set(ref _projectsDirectory, value);
    }

    public string ProjectsCaption =>
        "Where picking a project starts when you patch a transcript through to a coding agent.";

    // ------------------------------------------------------------------- save

    /// <summary>
    /// Write the changes.
    ///
    /// Only deliberate overrides are stored: a value that matches the default is
    /// removed rather than written. `on_stop` is deliberately never included, so a
    /// hook the user set by hand in the file survives a save from here. The macOS
    /// settings sheet leaves the same key alone for the same reason.
    /// </summary>
    public void Save()
    {
        var changes = new Dictionary<string, object?>
        {
            ["recordings_dir"] = Same(RecordingsDirectory, Config.DefaultRecordingsRoot)
                ? null
                : RecordingsDirectory,
            ["transcription.enabled"] = TranscriptionEnabled ? null : false,
            ["transcription.quality_mode"] = MaxAccuracy && MaxAccuracyAvailable ? "max_accuracy" : null,
            ["transcription.engine"] = string.Equals(Engine, "auto", StringComparison.Ordinal) ? null : Engine,
            ["projects_dir"] = string.IsNullOrWhiteSpace(ProjectsDirectory) ? null : ProjectsDirectory,
            ["notes.open_window_on_record"] = OpenWindowOnRecord ? null : false,
        };

        Config.Update(changes);

        // Launch at login is an operating system facility rather than a config
        // value, exactly as it is on macOS. It is also per machine, and the config
        // file is shared between machines.
        LaunchAtLogin = LoginLaunch.Set(LaunchAtLogin, _executablePath);
    }

    private static bool Same(string? left, string? right) =>
        string.Equals(
            left?.TrimEnd(Path.DirectorySeparatorChar),
            right?.TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
