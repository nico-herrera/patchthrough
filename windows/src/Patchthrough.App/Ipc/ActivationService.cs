namespace Patchthrough.App.Ipc;

/// <summary>
/// Keeps one copy of the app running, and gives a second launch a way to reach
/// the first.
///
/// A tray app has to do this. Its window is usually closed, so launching it again
/// looks like nothing happened, and a user who cannot see a window will click the
/// Start menu entry repeatedly. Two copies would then compete for the microphone
/// and both write session folders.
///
/// The signal carries no payload, so it is an event and not a pipe. When a deep
/// link needs to carry a URL, the same two methods can front a named pipe without
/// any caller changing.
/// </summary>
public sealed class ActivationService : IDisposable
{
    // Local, so the scope is the signed-in session. Two users on one machine each
    // get their own copy, which is right: the recordings are per user.
    private const string InstanceName = @"Local\com.nicoherrera.patchthrough.app";
    private const string ActivateName = @"Local\com.nicoherrera.patchthrough.activate";

    private readonly Mutex _instance;
    private readonly EventWaitHandle _activate;
    private readonly CancellationTokenSource _listening = new();
    private Thread? _listener;

    private ActivationService(Mutex instance, EventWaitHandle activate)
    {
        _instance = instance;
        _activate = activate;
    }

    /// <summary>
    /// Become the single instance, or return null when one is already running.
    /// A null return has already told the running copy to show itself, so the
    /// caller's only job is to exit quietly.
    /// </summary>
    public static ActivationService? Claim()
    {
        // An abandoned mutex means the previous copy crashed. That is a grant,
        // not a conflict: WaitOne throws AbandonedMutexException and the lock is
        // ours. Without this a crash would block every later launch.
        var instance = new Mutex(initiallyOwned: false, InstanceName);
        bool held;
        try
        {
            held = instance.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            held = true;
        }

        var activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateName);
        if (held) return new ActivationService(instance, activate);

        // Someone else owns it. Ask them to come forward, then go away.
        activate.Set();
        activate.Dispose();
        instance.Dispose();
        return null;
    }

    /// <summary>
    /// Call <paramref name="onActivated"/> whenever another launch asks for the
    /// window. **Raised on a background thread**, so a UI subscriber marshals.
    /// </summary>
    public void OnActivationRequested(Action onActivated)
    {
        if (_listener is not null) throw new InvalidOperationException("already listening");

        // A dedicated thread rather than a pool thread: this waits for the whole
        // life of the process, and a pool thread parked forever is a pool thread
        // taken from real work.
        _listener = new Thread(() =>
        {
            var handles = new[] { _activate, _listening.Token.WaitHandle };
            while (WaitHandle.WaitAny(handles) == 0)
            {
                onActivated();
            }
        })
        {
            IsBackground = true,
            Name = "patchthrough-activation",
        };
        _listener.Start();
    }

    /// <summary>
    /// Release the instance lock. Best called on the thread that claimed it, a
    /// mutex being owned by a thread rather than a process. Calling it elsewhere
    /// is handled rather than fatal: Windows releases the mutex when the process
    /// exits either way, so this is tidiness and not correctness.
    /// </summary>
    public void Dispose()
    {
        _listening.Cancel();
        // The listener is a background thread, so a shutdown never waits on it.
        _listening.Dispose();
        _activate.Dispose();
        try { _instance.ReleaseMutex(); }
        catch (ApplicationException) { /* never held, or claimed on another thread */ }
        _instance.Dispose();
    }
}
