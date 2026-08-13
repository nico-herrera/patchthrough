using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Patchthrough.App.Interop;
using Patchthrough.App.Theme;
using Patchthrough.App.ViewModels;

namespace Patchthrough.App.Shell;

/// <summary>
/// The window.
///
/// The code here is window-chrome and focus work only. Anything about sessions,
/// recording, or transcription belongs to <see cref="ShellViewModel"/>: a click
/// handler that did more than forward to a command would be state the viewmodel
/// cannot see.
/// </summary>
public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell;

    public MainWindow(ShellViewModel shell)
    {
        _shell = shell;
        InitializeComponent();
        DataContext = shell;

        shell.PropertyChanged += (_, e) =>
        {
            // The mark thickens while transcribing, matching the tray icon and the
            // macOS menu bar item.
            if (e.PropertyName == nameof(ShellViewModel.IsTranscribing))
            {
                MarkGlyph.Weight = shell.IsTranscribing ? Theme.Mark.HeavyWeight : Theme.Mark.RegularWeight;
            }
            // Choosing a destination retargets the button and closes the picker. It
            // does not send: that stays the red half, so browsing is always safe.
            if (e.PropertyName == nameof(ShellViewModel.Target)) DestinationPopup.IsOpen = false;
        };

        shell.ConfirmDelete = ConfirmDelete;
        shell.PickRepository = PickRepository;
        shell.ConfirmCloudUpload = ConfirmCloudUpload;

        // The frame around the drawn titlebar belongs to the compositor, and it is
        // light until asked otherwise. It can only be set once there is a handle.
        SourceInitialized += (_, _) => Dwm.UseDarkFrame(this);
        StateChanged += (_, _) => UpdateMaximizeGlyph();

        // Ctrl+F reaches the search field, which is not a system control and so
        // has no shortcut of its own.
        InputBindings.Add(new KeyBinding(
            new Mvvm.RelayCommand(() => SearchBox.Focus()), Key.F, ModifierKeys.Control));
    }

    /// <summary>
    /// Closing hides the window rather than destroying it. Patchthrough keeps
    /// recording from the tray after its window is gone, and a user who closes a
    /// window does not expect to stop a recording.
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnToggleMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void UpdateMaximizeGlyph()
    {
        // Segoe MDL2: E922 maximize, E923 restore. A button that kept the same
        // glyph in both states would claim the window is not already maximized.
        var maximized = WindowState == WindowState.Maximized;
        MaximizeButton.Content = maximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = maximized ? "Restore" : "Maximize";
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e) => OpenSettings?.Invoke();

    /// <summary>
    /// Start a drag carrying the handoff document.
    ///
    /// The drop payload is a file reference rather than text, so an application that
    /// accepts attachments gets a file with its name and extension intact.
    /// </summary>
    private void OnDragChipMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (_shell.Selected is not { Status: Core.SessionStatus.Ready } session) return;

        var handoff = Path.Combine(session.Directory, "handoff.md");
        if (!File.Exists(handoff)) return;

        var data = new DataObject(DataFormats.FileDrop, new[] { handoff });
        DragDrop.DoDragDrop(DragChip, data, DragDropEffects.Copy);
    }

    private void OnOpenDestinations(object sender, RoutedEventArgs e) =>
        DestinationPopup.IsOpen = !DestinationPopup.IsOpen;

    /// <summary>
    /// Ask which repository an agent should work in.
    ///
    /// A CLI agent reads the transcript from inside the repository the meeting was
    /// about, so this is the one thing a terminal handoff cannot guess.
    /// </summary>
    private string? PickRepository(string? startAt)
    {
        var dialog = new OpenFolderDialog
        {
            Multiselect = false,
            Title = "Choose the project this meeting was about",
        };
        if (!string.IsNullOrWhiteSpace(startAt) && Directory.Exists(startAt))
        {
            dialog.InitialDirectory = startAt;
        }
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    /// <summary>
    /// Ask before a transcript leaves the machine.
    ///
    /// Patchthrough transcribes on-device and uploads nothing on its own, so a
    /// destination that keeps a copy is a change in what the product promises. It is
    /// confirmed per site, and agreeing about one site says nothing about another.
    /// </summary>
    private bool ConfirmCloudUpload(DestinationViewModel destination)
    {
        var message =
            $"{destination.Label} copies the transcript off this PC.\n\n"
            + "Patchthrough records and transcribes on this machine and uploads nothing on its own. "
            + "Sending it here hands the meeting to that service.";

        return MessageBox.Show(
            this, message, "Patchthrough",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning,
            MessageBoxResult.Cancel) == MessageBoxResult.OK;
    }

    /// <summary>Set by the application, which owns the settings window.</summary>
    public Action? OpenSettings { get; set; }

    // ------------------------------------------------------------ inline rename

    private void OnRenameBoxLoaded(object sender, RoutedEventArgs e)
    {
        // The editor appears where the row's title was, so it has to take focus
        // and select what is there: a rename usually replaces the name rather
        // than appending to it.
        if (sender is not TextBox box) return;
        box.Focus();
        box.SelectAll();
    }

    private void OnRenameKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { Tag: SessionItemViewModel item }) return;
        switch (e.Key)
        {
            case Key.Enter:
                _shell.CommitRenameCommand.Execute(item);
                e.Handled = true;
                break;
            case Key.Escape:
                // Escape abandons the edit. The name on disk is untouched.
                item.IsRenaming = false;
                e.Handled = true;
                break;
        }
    }

    private void OnRenameLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Clicking away commits, which is what a user who typed a name and moved
        // on expects. Escape is the way to discard.
        if (sender is TextBox { Tag: SessionItemViewModel { IsRenaming: true } item })
        {
            _shell.CommitRenameCommand.Execute(item);
        }
    }

    // ----------------------------------------------------------------- dialogs

    /// <summary>
    /// Ask before a session goes to the Recycle Bin, naming exactly what is lost.
    ///
    /// A meeting cannot be recorded again, so this is never suppressible and the
    /// default button is Cancel: a stray Return must not delete a meeting.
    /// </summary>
    private bool ConfirmDelete(SessionItemViewModel item)
    {
        var lost = new List<string> { "both audio tracks" };
        if (item.Status == Core.SessionStatus.Ready) lost.Add("the transcript");

        var message =
            $"Move \"{item.Title}\" to the Recycle Bin?\n\nThis removes {string.Join(" and ", lost)}. "
            + "A meeting cannot be recorded again.";

        return MessageBox.Show(
            this, message, "Patchthrough",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning,
            MessageBoxResult.Cancel) == MessageBoxResult.OK;
    }
}
