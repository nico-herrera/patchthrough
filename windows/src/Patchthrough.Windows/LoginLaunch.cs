using Microsoft.Win32;

namespace Patchthrough.Windows;

/// <summary>
/// Start the app when the user signs in.
///
/// This is the per-user Run key, not a service and not a scheduled task: no
/// administrator, and it shows up in Task Manager's Startup tab where a user
/// looks to turn it off. It is the Windows counterpart to SMAppService on macOS,
/// and like that one it lives outside config.json, because launch-at-login is a
/// property of one machine and config.json is shared between them.
///
/// The installer writes the same value name, so an install that offers to start
/// at login and this toggle stay one setting rather than two.
/// </summary>
public static class LoginLaunch
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// The registry value name. The installer's [Registry] entry has to match
    /// this exactly, or uninstalling leaves a dead entry behind.
    /// </summary>
    public const string ValueName = "Patchthrough";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string command && !string.IsNullOrWhiteSpace(command);
    }

    /// <summary>
    /// Register the given executable. The path is quoted, because the default
    /// install location sits under a directory with a space in it and an
    /// unquoted path there launches nothing.
    /// </summary>
    public static void Enable(string executablePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException($@"cannot open HKCU\{RunKey}");
        key.SetValue(ValueName, $"\"{Path.GetFullPath(executablePath)}\"", RegistryValueKind.String);
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        // A missing key means it was never enabled, which is the wanted state.
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// Apply a setting, and report what it ended up as. A registry write can be
    /// refused by policy, so the caller shows the real state rather than the
    /// state it asked for.
    /// </summary>
    public static bool Set(bool enabled, string executablePath)
    {
        if (enabled) Enable(executablePath);
        else Disable();
        return IsEnabled();
    }
}
