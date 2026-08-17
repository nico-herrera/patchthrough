using System.Windows;
using System.Windows.Threading;

namespace Patchthrough.App.Mvvm;

/// <summary>
/// The one crossing point from a service thread onto the interface thread.
///
/// Every service in Patchthrough.Windows documents that it raises events on
/// whatever thread the work happened on: NAudio's capture thread for a failed
/// track, the transcription drain's worker for a status change, an HTTP read for
/// download progress. Touching a viewmodel from any of those corrupts WPF's
/// bindings in ways that surface much later and somewhere else.
///
/// Rule: **a service event handler's whole body goes through Post.** Not part
/// of it, and not "only where it touches the UI", because a viewmodel property is
/// bound and therefore is the UI.
/// </summary>
public static class UiThread
{
    private static Dispatcher? _dispatcher;

    /// <summary>
    /// Capture the interface thread. Called once, from application startup, so
    /// that later calls cannot accidentally capture a worker thread's dispatcher.
    /// </summary>
    public static void Capture() => _dispatcher = Application.Current?.Dispatcher;

    private static Dispatcher Dispatcher =>
        _dispatcher ?? Application.Current?.Dispatcher
        ?? throw new InvalidOperationException("no interface thread has been captured");

    /// <summary>
    /// Run this on the interface thread, without waiting. Already on it, run it
    /// now: posting would defer work that could have completed, and would let a
    /// caller observe stale state on the very next line.
    /// </summary>
    public static void Post(Action action)
    {
        var dispatcher = Dispatcher;
        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}
