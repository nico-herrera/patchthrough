using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using Patchthrough.App.Interop;
using Patchthrough.App.Mvvm;
using Patchthrough.Core;
using Patchthrough.Windows;
using Patchthrough.Windows.Handoff;
using Patchthrough.Windows.Shell;
using Patchthrough.Windows.Transcription;

namespace Patchthrough.App.ViewModels;

/// <summary>
/// Everything the window and the tray icon read, and every action they take.
///
/// It owns no work of its own. Recording, transcription, and the session list are
/// services in Patchthrough.Windows and Patchthrough.Core; this holds the state a
/// view can bind to and marshals the services' events onto the interface thread.
/// </summary>
public sealed class ShellViewModel : ViewModelBase, IDisposable
{
    private readonly RecordingService _recording;
    private readonly TranscriptionHost _transcription;
    private readonly DispatcherTimer _elapsedTicker;
    private readonly Func<Config> _config;

    private readonly AppState _state;

    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _watcherDebounce;
    private string _recordingsRoot;
    private DestinationViewModel? _target;

    private bool _isRecording;
    private string _elapsed = "0:00";
    private string? _liveSessionId;
    private string? _transcriptionStatus;
    private string? _lastAction;
    private string _search = "";
    private SessionItemViewModel? _selected;
    private ModelInstallProgress? _modelProgress;
    private string _noteDraft = "";

    public ShellViewModel(
        RecordingService recording,
        TranscriptionHost transcription,
        Func<Config> config,
        AppState? state = null)
    {
        _recording = recording;
        _transcription = transcription;
        _config = config;
        _state = state ?? AppState.Load();
        _recordingsRoot = config().ResolveRecordingsRoot();

        ToggleRecordingCommand = new RelayCommand(ToggleRecording);
        SelectCommand = new RelayCommand<SessionItemViewModel>(item => Selected = item);
        OpenRecordingsFolderCommand = new RelayCommand(() => Explorer.OpenFolder(RecordingsRoot));
        RevealSessionCommand = new RelayCommand<SessionItemViewModel>(item => Explorer.Reveal(item.Directory));
        BeginRenameCommand = new RelayCommand<SessionItemViewModel>(item => item.IsRenaming = true);
        CommitRenameCommand = new RelayCommand<SessionItemViewModel>(CommitRename);
        RemoveNameCommand = new RelayCommand<SessionItemViewModel>(item => Rename(item, null));
        DeleteSessionCommand = new RelayCommand<SessionItemViewModel>(DeleteSession);
        SendCommand = new RelayCommand(SendToTarget, () => CanSend);
        AddNoteCommand = new RelayCommand(AddNote, () => CanTakeNotes);
        ChooseDestinationCommand = new RelayCommand<DestinationViewModel>(Choose);

        // A 1 second tick, started only while recording. The service exposes the
        // instant the session began, so the display counts up on its own rather
        // than being sent a value every second from a worker thread.
        _elapsedTicker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTicker.Tick += (_, _) => UpdateElapsed();

        _recording.TrackFailed += (track, error) =>
            UiThread.Post(() => LastAction = $"The {track} track stopped early. {error.Message}");
        _transcription.StatusChanged += snapshot => UiThread.Post(() => ApplyTranscriptionStatus(snapshot));
        _transcription.SessionCompleted += (directory, error) =>
            UiThread.Post(() => OnSessionCompleted(directory, error));
        _transcription.ModelProgress += progress => UiThread.Post(() => ModelProgress = progress);
    }

    // ---------------------------------------------------------------- sessions

    public ObservableCollection<SessionGroupViewModel> Groups { get; } = [];

    public string RecordingsRoot => _recordingsRoot;

    /// <summary>
    /// Filter text. It matches a meeting's name, its folder, and its opening line,
    /// which is what makes a half-remembered meeting findable.
    /// </summary>
    public string Search
    {
        get => _search;
        set
        {
            if (Set(ref _search, value)) Refresh();
        }
    }

    public SessionItemViewModel? Selected
    {
        get => _selected;
        set
        {
            var previous = _selected;
            if (!Set(ref _selected, value, [nameof(Detail)])) return;
            // The row carries its own selected flag, because the list is a set of
            // grouped item controls rather than one selector, and a group header
            // must not be selectable.
            if (previous is not null) previous.IsSelected = false;
            if (value is not null) value.IsSelected = true;
            Detail?.Dispose();
            Detail = value is null ? null : new SessionDetailViewModel(value);
            Raise(nameof(Detail));
            // Only a transcribed session can be handed off, so the button follows
            // the selection rather than staying enabled over a pending one.
            Raise(nameof(CanSend));
            SendCommand.RaiseCanExecuteChanged();
            Raise(nameof(IsTakingNotes));
            Raise(nameof(CanTakeNotes));
            AddNoteCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>The right-hand pane for the selected session, or null when nothing is selected.</summary>
    public SessionDetailViewModel? Detail { get; private set; }

    public bool HasSessions => Groups.Count > 0;

    /// <summary>True when a filter is hiding everything, which reads differently from an empty folder.</summary>
    public bool IsFiltered => !string.IsNullOrWhiteSpace(_search);

    // --------------------------------------------------------------- recording

    public bool IsRecording
    {
        get => _isRecording;
        private set => Set(ref _isRecording, value, [nameof(RecordButtonTooltip)]);
    }

    /// <summary>Elapsed time as m:ss, for the titlebar and the tray.</summary>
    public string Elapsed
    {
        get => _elapsed;
        private set => Set(ref _elapsed, value);
    }

    public string RecordButtonTooltip => IsRecording ? "Stop and transcribe" : "Start recording";

    /// <summary>
    /// The transcription line, or null when nothing is happening. Worded as the
    /// macOS menu bar words it.
    /// </summary>
    public string? TranscriptionStatus
    {
        get => _transcriptionStatus;
        private set => Set(ref _transcriptionStatus, value);
    }

    /// <summary>True while transcribing, which thickens the mark the way macOS does.</summary>
    public bool IsTranscribing { get; private set; }

    /// <summary>The last thing that happened, shown at the foot of the detail pane.</summary>
    public string? LastAction
    {
        get => _lastAction;
        private set => Set(ref _lastAction, value);
    }

    /// <summary>Model download progress, or null when no model is being installed.</summary>
    public ModelInstallProgress? ModelProgress
    {
        get => _modelProgress;
        private set => Set(ref _modelProgress, value, [nameof(ModelProgressLabel), nameof(HasModelProgress)]);
    }

    public bool HasModelProgress => _modelProgress is not null;

    /// <summary>
    /// What the download says. The phases are named because each one takes
    /// minutes, and a single bar that stalled twice with no explanation would read
    /// as a hang.
    /// </summary>
    public string? ModelProgressLabel => _modelProgress is null ? null : _modelProgress.Phase switch
    {
        ModelInstallPhase.Downloading =>
            $"Downloading the transcription model. {Megabytes(_modelProgress.BytesReceived)} of {Megabytes(_modelProgress.TotalBytes)}.",
        ModelInstallPhase.Verifying => "Checking the transcription model.",
        _ => "Unpacking the transcription model.",
    };

    // ---------------------------------------------------------------- commands

    public RelayCommand ToggleRecordingCommand { get; }
    public RelayCommand<SessionItemViewModel> SelectCommand { get; }
    public RelayCommand OpenRecordingsFolderCommand { get; }
    public RelayCommand<SessionItemViewModel> RevealSessionCommand { get; }
    public RelayCommand<SessionItemViewModel> BeginRenameCommand { get; }
    public RelayCommand<SessionItemViewModel> CommitRenameCommand { get; }
    public RelayCommand<SessionItemViewModel> RemoveNameCommand { get; }
    public RelayCommand<SessionItemViewModel> DeleteSessionCommand { get; }

    /// <summary>
    /// Commit the note in <see cref="NoteDraft"/> to the selected session.
    /// </summary>
    public RelayCommand AddNoteCommand { get; }

    /// <summary>
    /// Send the selected session to <see cref="Target"/>. This is the red half of
    /// the split button, and it is the only thing that ever launches a handoff.
    /// </summary>
    public RelayCommand SendCommand { get; }

    /// <summary>
    /// Point the button at a destination. It never sends: browsing the picker has to
    /// be safe, so choosing and sending are two separate acts.
    /// </summary>
    public RelayCommand<DestinationViewModel> ChooseDestinationCommand { get; }

    /// <summary>
    /// Asks the user before a session is deleted, and reports what is lost. Set by
    /// the window, because a viewmodel does not own a dialog.
    /// </summary>
    public Func<SessionItemViewModel, bool>? ConfirmDelete { get; set; }

    /// <summary>Raised when a transcript is ready or has failed, for a notification.</summary>
    public event Action<string, string>? Notify;

    /// <summary>
    /// Asks for the window. Set by the application, and used when a recording starts:
    /// the note field is in the window, and notes are typed during a meeting rather
    /// than after it. Controlled by `notes.open_window_on_record`, which defaults on.
    /// </summary>
    public Action? RequestWindow { get; set; }

    // ------------------------------------------------------------------ lifetime

    /// <summary>
    /// Read the sessions, pick up anything left untranscribed, and start watching
    /// the folder. Called once the window and the tray exist.
    /// </summary>
    public void Start()
    {
        Refresh();
        _transcription.EnqueuePending(RecordingsRoot);
        WatchRecordingsRoot();
    }

    private void ToggleRecording()
    {
        if (IsRecording) StopRecording();
        else StartRecording();
    }

    private void StartRecording()
    {
        try
        {
            var session = _recording.Start(RecordingsRoot);
            _liveSessionId = session.Id;
            IsRecording = true;
            UpdateElapsed();
            _elapsedTicker.Start();
            Refresh();
            // Select the live session, so the window is already showing the
            // meeting that is being recorded.
            Selected = FindItem(session.Id) ?? Selected;
            LastAction = null;
            if (_config().NotesOpenWindowOnRecord) RequestWindow?.Invoke();
        }
        catch (Exception error)
        {
            LastAction = error.Message;
            Notify?.Invoke("Patchthrough: Recording failed", error.Message);
        }
    }

    private void StopRecording()
    {
        _elapsedTicker.Stop();
        IsRecording = false;
        var live = _liveSessionId;
        _liveSessionId = null;

        // Stop encodes both tracks, which takes seconds on a long meeting. Off
        // the interface thread, or the window freezes while it runs.
        Task.Run(() =>
        {
            try
            {
                var directory = _recording.Stop();
                UiThread.Post(() =>
                {
                    Refresh();
                    Selected = FindItem(live) ?? Selected;
                });
                _transcription.Enqueue(directory);
            }
            catch (Exception error)
            {
                UiThread.Post(() =>
                {
                    LastAction = error.Message;
                    Notify?.Invoke("Patchthrough: Recording failed", error.Message);
                    Refresh();
                });
            }
        });
    }

    private void UpdateElapsed()
    {
        var started = _recording.Current?.StartedAt;
        if (started is null)
        {
            Elapsed = "0:00";
            return;
        }
        var span = DateTimeOffset.UtcNow - started.Value;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        Elapsed = span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{(int)span.TotalMinutes}:{span.Seconds:00}";
    }

    private void ApplyTranscriptionStatus(TranscriptionQueueSnapshot snapshot)
    {
        IsTranscribing = snapshot.State == TranscriptionQueueState.Transcribing;
        Raise(nameof(IsTranscribing));
        TranscriptionStatus = snapshot.State switch
        {
            TranscriptionQueueState.Transcribing when snapshot.QueuedCount > 0 =>
                $"Transcribing {snapshot.SessionName}, {snapshot.QueuedCount} queued",
            TranscriptionQueueState.Transcribing => $"Transcribing {snapshot.SessionName}",
            TranscriptionQueueState.Failed => $"Transcription failed: {snapshot.SessionName}",
            _ => null,
        };
        // A finished drain has no model work left to report.
        if (snapshot.State != TranscriptionQueueState.Transcribing) ModelProgress = null;
        Refresh();
    }

    private void OnSessionCompleted(string directory, Exception? error)
    {
        var name = new DirectoryInfo(directory).Name;
        if (error is null)
        {
            LastAction = $"Transcript ready: {name}";
            Notify?.Invoke("Patchthrough: Transcript ready", name);
        }
        else
        {
            LastAction = $"Transcription failed: {name}";
            Notify?.Invoke("Patchthrough: Transcription failed", $"{name}. See transcribe.log");
        }
        Refresh();
    }

    // ------------------------------------------------------------------ editing

    private void CommitRename(SessionItemViewModel item)
    {
        var name = item.EditingName;
        item.IsRenaming = false;
        Rename(item, name);
    }

    private void Rename(SessionItemViewModel item, string? name)
    {
        try
        {
            SessionMeta.UpdateName(item.Directory, name);
            LastAction = string.IsNullOrWhiteSpace(name)
                ? $"Removed the name from {item.Id}"
                : $"Renamed to {name.Trim()}";
        }
        catch (Exception error)
        {
            LastAction = $"Could not rename {item.Id}. {error.Message}";
        }
        Refresh();
    }

    private void DeleteSession(SessionItemViewModel item)
    {
        // A live recording holds open file handles for both tracks. The menu hides
        // the item, and this refuses anything that reaches it another way.
        if (!item.CanEdit) return;
        if (ConfirmDelete is not null && !ConfirmDelete(item)) return;

        try
        {
            RecycleBin.Send(item.Directory);
            LastAction = $"Moved {item.Title} to the Recycle Bin";
            if (ReferenceEquals(Selected, item)) Selected = null;
        }
        catch (Exception error)
        {
            LastAction = $"Could not delete {item.Title}. {error.Message}";
        }
        Refresh();
    }

    // ------------------------------------------------------------------- notes

    /// <summary>
    /// What the user is typing. Held here rather than in the field, so committing a
    /// note and clearing the box are one action.
    /// </summary>
    public string NoteDraft
    {
        get => _noteDraft;
        set
        {
            if (Set(ref _noteDraft, value)) AddNoteCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// Notes belong to a meeting, and the one worth taking notes on is the one being
    /// recorded now. A finished session's notes are readable but not editable: they
    /// are timestamped against the recording, and a note typed a day later would
    /// claim a position in a conversation it was not part of.
    /// </summary>
    public bool CanTakeNotes =>
        Selected?.Status == SessionStatus.Recording && _noteDraft.Trim().Length > 0;

    /// <summary>True while a recording is selected, so the note field is shown.</summary>
    public bool IsTakingNotes => Selected?.Status == SessionStatus.Recording;

    private void AddNote()
    {
        if (Selected is not { Status: SessionStatus.Recording } session) return;
        var text = _noteDraft;
        NoteDraft = "";

        try
        {
            // The instant is stamped here, when the user committed it, and never
            // recomputed. The transcript's zero is not final until the recording
            // stops, so an offset taken now would be measured against a zero that
            // can still move. See docs/notes-and-the-recording-clock.md.
            SessionNotes.Append(session.Directory, text);
            Detail?.ReloadNotes();
        }
        catch (Exception error)
        {
            LastAction = $"Could not save the note. {error.Message}";
            // The text goes back in the box rather than being lost.
            NoteDraft = text;
        }
    }

    // ---------------------------------------------------------- patch through

    /// <summary>The doors this machine has, grouped for the picker.</summary>
    public ObservableCollection<DestinationGroupViewModel> DestinationGroups { get; } = [];

    /// <summary>
    /// Where the primary button sends. It follows the last destination used rather
    /// than the most used one: the thing done last is the best guess at the thing
    /// about to be done again.
    /// </summary>
    public DestinationViewModel? Target
    {
        get => _target;
        private set => Set(ref _target, value, [nameof(SendLabel), nameof(CanSend), nameof(HasDestinations)]);
    }

    public bool HasDestinations => _target is not null;

    /// <summary>
    /// A handoff needs a transcript. A pending or empty session has nothing to send,
    /// so the button is disabled rather than failing when pressed.
    /// </summary>
    public bool CanSend => _target is not null && Selected?.Status == SessionStatus.Ready;

    public string SendLabel => _target is null
        ? "Patch through to"
        : $"Patch through to {_target.ShortLabel}";

    /// <summary>
    /// Chooses a repository for an agent handoff. Set by the window, because a
    /// viewmodel does not own a folder dialog. Returning null cancels.
    /// </summary>
    public Func<string?, string?>? PickRepository { get; set; }

    /// <summary>
    /// Asks before sending to a site that keeps a copy off the machine. Set by the
    /// window. Returning false cancels the handoff.
    /// </summary>
    public Func<DestinationViewModel, bool>? ConfirmCloudUpload { get; set; }

    private void Choose(DestinationViewModel destination)
    {
        Target = destination;
        _state.LastDestination = destination.Id;
    }

    private void SendToTarget()
    {
        if (_target is null || Selected is not { Status: SessionStatus.Ready } session) return;

        try
        {
            var document = DocumentFor(session);
            var result = _target.Kind == DestinationKind.Terminal
                ? SendToAgent(_target, session, document)
                : SendToSite(_target, session, document);

            if (result is null) return;   // the user cancelled
            LastAction = result.Message;
            // The ranking only counts a handoff that actually went out.
            _state.RecordUse(_target.Id);
            ResolveDestinations();
        }
        catch (Exception error)
        {
            LastAction = $"Could not patch through to {_target.ShortLabel}. {error.Message}";
        }
    }

    private HandoffResult? SendToAgent(
        DestinationViewModel destination,
        SessionItemViewModel session,
        string document)
    {
        var repository = _state.HandoffRepository;
        if (repository is null || !Directory.Exists(repository))
        {
            // No repository yet, or the saved one has moved. Ask, starting from the
            // configured projects directory.
            repository = PickRepository?.Invoke(_state.HandoffRepository ?? _config().ProjectsDirectory);
            if (repository is null) return null;
            _state.HandoffRepository = repository;
        }

        return HandoffLauncher.ToAgent(
            destination.Agent!, repository, session.Id, document, _config().Terminal);
    }

    private HandoffResult? SendToSite(
        DestinationViewModel destination,
        SessionItemViewModel session,
        string document)
    {
        // A site that keeps a copy is the one thing here that leaves the machine, so
        // it is confirmed every time unless the user has said not to ask again.
        if (destination.UploadsToCloud
            && !_state.IsSuppressed($"cloud-upload.{destination.Id}")
            && ConfirmCloudUpload?.Invoke(destination) == false)
        {
            return null;
        }

        return HandoffLauncher.ToChatSite(
            destination.Site!,
            Path.Combine(session.Directory, "handoff.md"),
            session.Listing.DurationSeconds,
            document);
    }

    /// <summary>
    /// The handoff document for a session, written if it is not there yet. Older
    /// sessions predate handoff.md, and the npm CLI expects to find one.
    /// </summary>
    private static string DocumentFor(SessionItemViewModel session)
    {
        var path = Path.Combine(session.Directory, "handoff.md");
        if (!File.Exists(path))
        {
            HandoffDocument.Write(
                session.Directory,
                session.Listing.DurationSeconds,
                session.Listing.CleanStop,
                session.Listing.Name);
        }
        return File.ReadAllText(path);
    }

    /// <summary>
    /// Find the agents and sites this machine has, and group them for the picker.
    ///
    /// Probing the PATH for every agent and reading the config touches the
    /// filesystem, so this runs on a refresh rather than on every menu open. A newly
    /// installed agent therefore appears on the next refresh, not instantly.
    /// </summary>
    private void ResolveDestinations()
    {
        var config = _config();
        var agents = AgentCatalog.Installed(
                Environment.GetEnvironmentVariable("PATH"),
                Environment.GetEnvironmentVariable("PATHEXT"))
            .Select(DestinationViewModel.ForAgent)
            .ToList();
        var sites = DestinationCatalog.Resolve(config, TextWriter.Null)
            .Select(DestinationViewModel.ForSite)
            .ToList();

        var all = agents.Concat(sites).ToList();
        var ranked = _state.RankedDestinations();
        var mostUsed = ranked
            .Select(id => all.FirstOrDefault(destination => destination.Id == id))
            .Where(destination => destination is not null)
            .Take(3)
            .Select(destination => destination!)
            .ToList();

        DestinationGroups.Clear();
        if (mostUsed.Count > 0) DestinationGroups.Add(new DestinationGroupViewModel("Most used", mostUsed));
        AddGroup("Terminal", DestinationKind.Terminal);
        AddGroup("Web", DestinationKind.Web);
        AddGroup("Custom", DestinationKind.Custom);

        // The saved target, then the most used, then anything. A destination the
        // user uninstalled falls through rather than leaving a dead button.
        Target = all.FirstOrDefault(destination => destination.Id == _state.LastDestination)
            ?? mostUsed.FirstOrDefault()
            ?? all.FirstOrDefault();
        SendCommand.RaiseCanExecuteChanged();

        void AddGroup(string title, DestinationKind kind)
        {
            var members = all.Where(destination => destination.Kind == kind).ToList();
            if (members.Count > 0) DestinationGroups.Add(new DestinationGroupViewModel(title, members));
        }
    }

    // ------------------------------------------------------------------ refresh

    /// <summary>
    /// Re-read the recordings folder and rebuild the list, keeping the selection
    /// and any open rename editor.
    /// </summary>
    public void Refresh()
    {
        _recordingsRoot = _config().ResolveRecordingsRoot();
        Raise(nameof(RecordingsRoot));

        var listings = SessionIndex.Scan(RecordingsRoot, _liveSessionId);
        var filtered = Filter(listings);
        var grouped = SessionGrouping.Group(filtered, DateTimeOffset.Now);

        // Existing rows are reused by id, so the selection survives a refresh and
        // a row being edited keeps its editor open.
        var existing = Groups
            .SelectMany(group => group.Sessions)
            .ToDictionary(item => item.Id, StringComparer.Ordinal);

        Groups.Clear();
        foreach (var group in grouped)
        {
            var rows = new List<SessionItemViewModel>(group.Sessions.Count);
            foreach (var listing in group.Sessions)
            {
                if (existing.TryGetValue(listing.Id, out var row)) row.Update(listing);
                else row = new SessionItemViewModel(listing);
                rows.Add(row);
            }
            Groups.Add(new SessionGroupViewModel(group.Title, rows));
        }
        Raise(nameof(HasSessions));
        Raise(nameof(IsFiltered));
        if (DestinationGroups.Count == 0) ResolveDestinations();

        // Keep the selected session selected, and fall back to the newest one that
        // can actually be read.
        var selectedId = Selected?.Id;
        var all = Groups.SelectMany(group => group.Sessions).ToList();
        var next = all.FirstOrDefault(item => string.Equals(item.Id, selectedId, StringComparison.Ordinal));
        if (next is not null)
        {
            if (!ReferenceEquals(next, Selected)) Selected = next;
            else Detail?.Reload();
        }
        else
        {
            Selected = all.FirstOrDefault(item => item.Status == SessionStatus.Ready) ?? all.FirstOrDefault();
        }
    }

    private IEnumerable<SessionListing> Filter(IEnumerable<SessionListing> listings)
    {
        if (string.IsNullOrWhiteSpace(_search)) return listings;
        var needle = _search.Trim();
        return listings.Where(listing =>
            Contains(listing.Name, needle)
            || Contains(listing.Id, needle)
            || Contains(listing.FirstLine, needle));
    }

    private static bool Contains(string? haystack, string needle) =>
        haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private SessionItemViewModel? FindItem(string? id) =>
        id is null
            ? null
            : Groups.SelectMany(group => group.Sessions)
                .FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));

    /// <summary>
    /// Watch the recordings folder, so a session written by the console tool shows
    /// up here without a refresh button. This is what the macOS app does with a
    /// dispatch source on the same directory.
    /// </summary>
    private void WatchRecordingsRoot()
    {
        _watcher?.Dispose();
        try
        {
            Directory.CreateDirectory(RecordingsRoot);
            _watcher = new FileSystemWatcher(RecordingsRoot)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                IncludeSubdirectories = true,
                EnableRaisingEvents = true,
            };
            _watcher.Created += OnRootChanged;
            _watcher.Deleted += OnRootChanged;
            _watcher.Renamed += OnRootChanged;
            _watcher.Changed += OnRootChanged;
        }
        catch (Exception)
        {
            // A folder that cannot be watched still lists on demand. Losing live
            // updates is worth less than the window.
            _watcher = null;
        }
    }

    private void OnRootChanged(object sender, FileSystemEventArgs e) => UiThread.Post(() =>
    {
        // Debounced. Transcribing one session writes several files, and a refresh
        // per file would re-read the whole folder a dozen times a second.
        _watcherDebounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _watcherDebounce.Tick -= OnDebounceElapsed;
        _watcherDebounce.Tick += OnDebounceElapsed;
        _watcherDebounce.Stop();
        _watcherDebounce.Start();
    });

    private void OnDebounceElapsed(object? sender, EventArgs e)
    {
        _watcherDebounce?.Stop();
        Refresh();
    }

    private static string Megabytes(long bytes) =>
        $"{bytes / 1024d / 1024d:0} MB";

    public void Dispose()
    {
        _elapsedTicker.Stop();
        _watcherDebounce?.Stop();
        _watcher?.Dispose();
        Detail?.Dispose();
    }
}

/// <summary>One date bucket in the sidebar.</summary>
public sealed class SessionGroupViewModel(string title, IReadOnlyList<SessionItemViewModel> sessions)
{
    public string Title { get; } = title;

    public IReadOnlyList<SessionItemViewModel> Sessions { get; } = sessions;
}
