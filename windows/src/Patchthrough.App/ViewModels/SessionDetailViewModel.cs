using System.Collections.ObjectModel;
using Patchthrough.App.Mvvm;
using Patchthrough.Core;

namespace Patchthrough.App.ViewModels;

/// <summary>
/// Which pane the detail area shows. Every one of these is a real state a session
/// directory can be in, and each says something different to the user, so none of
/// them shares a template with another.
/// </summary>
public enum DetailState
{
    /// <summary>Being recorded now.</summary>
    Recording,

    /// <summary>Transcribed, with a transcript to read.</summary>
    Transcript,

    /// <summary>Recorded, waiting for transcription.</summary>
    Pending,

    /// <summary>Transcription finished and found no speech.</summary>
    Empty,

    /// <summary>Interrupted before it wrote a marker.</summary>
    Broken,
}

/// <summary>
/// The right-hand pane for one session.
///
/// The transcript is read here rather than in the list, because reading every
/// transcript to draw a sidebar would mean parsing megabytes to show a few lines.
/// </summary>
public sealed class SessionDetailViewModel : ViewModelBase, IDisposable
{
    private readonly SessionItemViewModel _item;

    public SessionDetailViewModel(SessionItemViewModel item)
    {
        _item = item;
        _item.PropertyChanged += OnItemChanged;
        Reload();
    }

    public SessionItemViewModel Item => _item;

    public ObservableCollection<Turn> Turns { get; } = [];

    /// <summary>
    /// The notes typed during this meeting, on the transcript's clock. Shown above
    /// the transcript, which is the order a reader needs: what a human flagged, then
    /// the record it points at.
    /// </summary>
    public ObservableCollection<ResolvedNote> Notes { get; } = [];

    public bool HasNotes => Notes.Count > 0;

    public DetailState State => _item.Status switch
    {
        SessionStatus.Recording => DetailState.Recording,
        SessionStatus.Ready => DetailState.Transcript,
        SessionStatus.Pending => DetailState.Pending,
        SessionStatus.Empty => DetailState.Empty,
        _ => DetailState.Broken,
    };

    /// <summary>The header line: the session's identity and its size.</summary>
    public string Title => _item.Title;

    /// <summary>
    /// Under the title. A named meeting shows the folder it lives in, because the
    /// folder is the identity everywhere else, including in a handoff.
    /// </summary>
    public string Subtitle
    {
        get
        {
            var parts = new List<string>();
            if (_item.HasName) parts.Add(_item.Id);
            if (_item.Duration.Length > 0) parts.Add(_item.Duration);
            if (_item.Listing.Words > 0) parts.Add($"{_item.Listing.Words} words");
            if (!_item.Listing.CleanStop) parts.Add("ended uncleanly");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Headline for a pane with no transcript to show.</summary>
    public string PlaceholderTitle => State switch
    {
        DetailState.Recording => $"Recording {_item.Title}",
        DetailState.Pending => $"Transcribing {_item.Id}",
        DetailState.Empty => $"No speech in {_item.Id}",
        DetailState.Broken => $"{_item.Id} was interrupted",
        _ => "",
    };

    /// <summary>
    /// Re-read only the notes, after one has been added. Reloading everything would
    /// re-parse the transcript on every keystroke-committed note.
    /// </summary>
    public void ReloadNotes()
    {
        Notes.Clear();
        foreach (var note in SessionNotes.Resolved(_item.Directory)) Notes.Add(note);
        Raise(nameof(HasNotes));
    }

    /// <summary>The sentence under it, which is where the reassurance lives.</summary>
    public string PlaceholderDetail => State switch
    {
        DetailState.Recording => "Your microphone and the meeting are being recorded as two tracks.",
        DetailState.Pending => "About 20 seconds per hour of audio.",
        DetailState.Empty => "The recording finished and no speech was found. The audio is still in the folder.",
        DetailState.Broken => "There is no meta.json, so the recording stopped before it could be finished.",
        _ => "",
    };

    /// <summary>
    /// Re-read the transcript. Called when the session's status changes, which is
    /// how a pending pane becomes a transcript without the user doing anything.
    /// </summary>
    public void Reload()
    {
        Turns.Clear();
        if (_item.Status == SessionStatus.Ready)
        {
            foreach (var turn in TranscriptTurns.Read(_item.Directory)) Turns.Add(turn);
        }

        Notes.Clear();
        foreach (var note in SessionNotes.Resolved(_item.Directory)) Notes.Add(note);
        Raise(nameof(HasNotes));
        Raise(nameof(State));
        Raise(nameof(Title));
        Raise(nameof(Subtitle));
        Raise(nameof(PlaceholderTitle));
        Raise(nameof(PlaceholderDetail));
    }

    private void OnItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SessionItemViewModel.Status) or nameof(SessionItemViewModel.Title))
        {
            Reload();
        }
    }

    public void Dispose() => _item.PropertyChanged -= OnItemChanged;
}
