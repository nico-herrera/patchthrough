using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Patchthrough.App.Interop;
using Patchthrough.App.ViewModels;
using Patchthrough.Core;

namespace Patchthrough.App.Shell;

/// <summary>
/// The settings sheet. Modal over the window, and nothing is written until Save.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _settings;

    public SettingsWindow(SettingsViewModel settings)
    {
        _settings = settings;
        InitializeComponent();
        DataContext = settings;
        SourceInitialized += (_, _) => Dwm.UseDarkFrame(this);
    }

    /// <summary>True when the user saved, so the caller knows to re-read the config.</summary>
    public bool Saved { get; private set; }

    /// <summary>
    /// The sheet has no system titlebar, so the header strip moves the window.
    /// </summary>
    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void OnChooseRecordings(object sender, RoutedEventArgs e)
    {
        var chosen = ChooseFolder(_settings.RecordingsDirectory);
        if (chosen is not null) _settings.RecordingsDirectory = chosen;
    }

    private void OnChooseProjects(object sender, RoutedEventArgs e)
    {
        var chosen = ChooseFolder(_settings.ProjectsDirectory);
        if (chosen is not null) _settings.ProjectsDirectory = chosen;
    }

    /// <summary>
    /// The in-box folder picker, which .NET 8 added. No package, and it is the
    /// dialog Windows users already know.
    /// </summary>
    private string? ChooseFolder(string? startAt)
    {
        var dialog = new OpenFolderDialog { Multiselect = false };
        if (!string.IsNullOrWhiteSpace(startAt) && Directory.Exists(startAt))
        {
            dialog.InitialDirectory = startAt;
        }
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private void OnRevealConfig(object sender, RoutedEventArgs e)
    {
        // The config file only exists once something has been saved, so reveal the
        // folder when the file is not there yet. Selecting a path that does not
        // exist silently does nothing.
        var path = Config.DefaultPath;
        if (File.Exists(path))
        {
            Explorer.Reveal(path);
            return;
        }
        var parent = Path.GetDirectoryName(path);
        if (parent is not null) Explorer.OpenFolder(parent);
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        // Nothing was written, so there is nothing to undo.
        Saved = false;
        Close();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            _settings.Save();
            Saved = true;
            Close();
        }
        catch (Exception error)
        {
            // A config that could not be written must say so. Closing the sheet as
            // though it saved would leave the user believing a setting took effect.
            MessageBox.Show(
                this,
                $"Settings could not be saved. {error.Message}",
                "Patchthrough",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}
