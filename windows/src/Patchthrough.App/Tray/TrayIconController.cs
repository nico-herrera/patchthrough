using Microsoft.Win32;
using Patchthrough.App.Mvvm;
using Patchthrough.App.Theme;
using Patchthrough.App.ViewModels;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Patchthrough.App.Tray;

/// <summary>
/// The tray icon and its menu.
///
/// Recording starts here. That is the same division the macOS app makes: the menu
/// bar is where a meeting begins, and the window is where what happened during it
/// is read. The window carries a second record control as a fallback, because a
/// tray icon can end up hidden in the overflow area and would otherwise leave the
/// primary action unreachable while the app is running.
/// </summary>
public sealed class TrayIconController : IDisposable
{
    private readonly ShellViewModel _shell;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _stateItem;
    private readonly Forms.ToolStripMenuItem _transcriptionItem;
    private readonly Forms.ToolStripMenuItem _toggleItem;
    private readonly Forms.ToolStripMenuItem _patchItem;

    private Drawing.Icon? _current;
    private TrayState _state = TrayState.Idle;
    private bool _lightTaskbar;

    public TrayIconController(ShellViewModel shell, Action openWindow, Action quit)
    {
        _shell = shell;
        _lightTaskbar = TaskbarIsLight();

        // Disabled items are state lines rather than unavailable actions. A system
        // menu has no other way to show read-only text.
        _stateItem = new Forms.ToolStripMenuItem("Idle") { Enabled = false };
        _transcriptionItem = new Forms.ToolStripMenuItem("") { Enabled = false, Visible = false };
        _toggleItem = new Forms.ToolStripMenuItem("Start recording", null,
            (_, _) => UiThread.Post(() => _shell.ToggleRecordingCommand.Execute(null)));

        // The promoted one-click handoff. It names the meeting and the destination,
        // so the most common action after a meeting takes one click from the tray
        // without opening the window at all.
        _patchItem = new Forms.ToolStripMenuItem("", null,
            (_, _) => UiThread.Post(() => _shell.SendCommand.Execute(null)))
        {
            Visible = false,
        };

        var menu = new Forms.ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            BackColor = DarkMenuRenderer.Gdi(PT.C.Raised),
            ForeColor = DarkMenuRenderer.Gdi(PT.C.Text2),
            ShowImageMargin = false,
        };
        menu.Items.AddRange(
        [
            _stateItem,
            _transcriptionItem,
            new Forms.ToolStripSeparator(),
            _toggleItem,
            new Forms.ToolStripSeparator(),
            _patchItem,
            new Forms.ToolStripMenuItem("Open Patchthrough", null, (_, _) => UiThread.Post(openWindow)),
            new Forms.ToolStripMenuItem("Recordings folder", null,
                (_, _) => UiThread.Post(() => _shell.OpenRecordingsFolderCommand.Execute(null))),
            new Forms.ToolStripSeparator(),
            new Forms.ToolStripMenuItem("Quit Patchthrough", null, (_, _) => UiThread.Post(quit)),
        ]);

        _icon = new Forms.NotifyIcon
        {
            ContextMenuStrip = menu,
            Visible = true,
            Text = "Patchthrough",
        };
        // A left click opens the window, which is what a Windows user expects from
        // a tray icon. The menu belongs to the right button.
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) UiThread.Post(openWindow);
        };
        _icon.BalloonTipClicked += (_, _) => UiThread.Post(openWindow);

        ApplyIcon();
        Update();

        _shell.PropertyChanged += (_, _) => UiThread.Post(Update);
        _shell.Notify += Notify;

        // The taskbar can switch between light and dark while the app is running,
        // and the icon is drawn onto it. Without this the mark becomes invisible.
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    /// <summary>
    /// Show a message. These render as ordinary Windows notifications on Windows 10
    /// 1809 and later, so they land in Action Center and respect Focus Assist,
    /// without the app needing a packaged identity.
    /// </summary>
    public void Notify(string title, string body) =>
        _icon.ShowBalloonTip(5000, title, body, Forms.ToolTipIcon.None);

    private void Update()
    {
        var sessions = _shell.Groups.Sum(group => group.Sessions.Count);

        // Recording wins the icon: it is the state a user most needs to see from
        // across the room, and it is the one with a cost to missing.
        var next = _shell.IsRecording ? TrayState.Recording
            : _shell.IsTranscribing ? TrayState.Transcribing
            : TrayState.Idle;
        if (next != _state)
        {
            _state = next;
            ApplyIcon();
        }

        _stateItem.Text = _shell.IsRecording
            ? $"Recording  {_shell.Elapsed}"
            : $"Idle · {sessions} session{(sessions == 1 ? "" : "s")}";

        _transcriptionItem.Text = _shell.TranscriptionStatus ?? "";
        _transcriptionItem.Visible = _shell.TranscriptionStatus is not null;

        _toggleItem.Text = _shell.IsRecording ? "Stop and transcribe" : "Start recording";

        // Only offered when there is something to send and somewhere to send it,
        // and never during a recording: the session being captured has no
        // transcript yet.
        var target = _shell.Target;
        var session = _shell.Selected;
        var sendable = !_shell.IsRecording && _shell.CanSend && target is not null && session is not null;
        _patchItem.Visible = sendable;
        if (sendable)
        {
            _patchItem.Text = $"Patch {session!.TimeOfDay} to {target!.ShortLabel}";
        }

        // The tooltip carries the state in words. A red dot on its own would put
        // the meaning in colour alone, which rule 13 rules out.
        _icon.Text = _shell.IsRecording
            ? $"Patchthrough: Recording {_shell.Elapsed}"
            : _shell.TranscriptionStatus is not null
                ? $"Patchthrough: {_shell.TranscriptionStatus}"
                : "Patchthrough";
    }

    private void ApplyIcon()
    {
        var previous = _current;
        _current = TrayIconFactory.Build(_state, _lightTaskbar);
        _icon.Icon = _current;
        // Disposed after the swap: an icon holds an unmanaged handle, and freeing
        // the one still assigned would blank the tray.
        previous?.Dispose();
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle)) return;
        UiThread.Post(() =>
        {
            var light = TaskbarIsLight();
            if (light == _lightTaskbar) return;
            _lightTaskbar = light;
            ApplyIcon();
        });
    }

    /// <summary>
    /// Whether the taskbar is light. This is a separate setting from the app theme
    /// in Windows, and it is the one that matters: the icon is drawn onto the
    /// taskbar. Patchthrough's own surfaces stay dark either way (rule 5).
    /// </summary>
    private static bool TaskbarIsLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
        catch (Exception)
        {
            // Unreadable means the default, which since Windows 10 has been a dark
            // taskbar. A light mark on a dark ground is the safer guess.
            return false;
        }
    }

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _shell.Notify -= Notify;
        _icon.Visible = false;
        _icon.Dispose();
        _current?.Dispose();
    }
}
