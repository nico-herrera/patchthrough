using Patchthrough.App.Mvvm;
using Patchthrough.Core;

namespace Patchthrough.App.ViewModels;

/// <summary>
/// One row of the sidebar.
///
/// It wraps a <see cref="SessionListing"/> and adds only what a row needs that
/// the listing cannot know: whether it is selected, and whether its name is being
/// edited in place.
/// </summary>
public sealed class SessionItemViewModel(SessionListing listing) : ViewModelBase
{
    private bool _isRenaming;
    private bool _isSelected;
    private string _editingName = listing.Name ?? "";

    public SessionListing Listing { get; private set; } = listing;

    public string Id => Listing.Id;

    public string Directory => Listing.Directory;

    public SessionStatus Status => Listing.Status;

    /// <summary>The name the user gave the meeting, or the folder timestamp.</summary>
    public string Title => Listing.DisplayTitle;

    /// <summary>True when the meeting has a name, so the row can lead with it.</summary>
    public bool HasName => Listing.Name is not null;

    /// <summary>
    /// The clock time, for a row whose group header already carries the day.
    /// </summary>
    public string TimeOfDay => Listing.StartedAt?.ToLocalTime().ToString("h:mm tt") ?? Listing.Id;

    /// <summary>
    /// The second line of a transcribed row: what the meeting opened with.
    /// </summary>
    public string? FirstLine => Listing.FirstLine;

    public string Duration => Listing.DurationSeconds > 0
        ? HandoffDocument.Duration(Listing.DurationSeconds)
        : "";

    /// <summary>
    /// What a row says when it has no transcript to show. Each state gets its own
    /// sentence, because "pending" and "no speech" look identical on disk and a
    /// user who cannot tell them apart waits for something that already finished.
    /// </summary>
    public string Subtitle => Listing.Status switch
    {
        SessionStatus.Recording => "Recording",
        SessionStatus.Pending => "Transcribing",
        SessionStatus.Empty => "No speech",
        SessionStatus.Broken => "Interrupted",
        _ => Duration,
    };

    /// <summary>
    /// A ready row shows its opening line; every other state shows its status.
    /// </summary>
    public bool ShowsTranscriptLine => Listing.Status == SessionStatus.Ready;

    public bool IsRecording => Listing.Status == SessionStatus.Recording;

    /// <summary>A live recording cannot be renamed or deleted while it is running.</summary>
    public bool CanEdit => Listing.Status != SessionStatus.Recording;

    /// <summary>
    /// The selected row. It draws as a filled row with a ring, never as a coloured
    /// leading edge bar: that is design rule 4, and the fill plus ring is the one
    /// selection shape the whole app uses.
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    /// <summary>The name field is open in the row.</summary>
    public bool IsRenaming
    {
        get => _isRenaming;
        set
        {
            // Opening the editor always starts from the stored name, so a
            // cancelled edit cannot leak into the next one.
            if (value) EditingName = Listing.Name ?? "";
            Set(ref _isRenaming, value);
        }
    }

    /// <summary>The text being typed while renaming.</summary>
    public string EditingName
    {
        get => _editingName;
        set => Set(ref _editingName, value);
    }

    /// <summary>
    /// Take a fresh listing for the same session, after a rename or a
    /// transcription finishing. Updating in place rather than replacing the row
    /// keeps the selection and any open editor.
    /// </summary>
    public void Update(SessionListing listing)
    {
        Listing = listing;
        Raise(nameof(Listing));
        Raise(nameof(Status));
        Raise(nameof(Title));
        Raise(nameof(HasName));
        Raise(nameof(TimeOfDay));
        Raise(nameof(FirstLine));
        Raise(nameof(Duration));
        Raise(nameof(Subtitle));
        Raise(nameof(ShowsTranscriptLine));
        Raise(nameof(IsRecording));
        Raise(nameof(CanEdit));
    }
}
