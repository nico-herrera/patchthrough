using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Patchthrough.App.Mvvm;

/// <summary>
/// Change notification, and nothing else.
///
/// This is hand-written rather than taken from a toolkit. The whole need is one
/// event and a setter helper across a handful of viewmodels, and the dependency
/// graph here is deliberately locked and small.
///
/// **Viewmodels live on the UI thread.** Services raise their events on worker
/// threads, so every handler that reaches a viewmodel goes through
/// <see cref="UiThread"/> first.
/// </summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>
    /// Assign and notify, returning true when the value actually changed. Also
    /// notifies any dependent property named in <paramref name="also"/>, which is
    /// what keeps a computed label in step with the field behind it.
    /// </summary>
    protected bool Set<T>(
        ref T field,
        T value,
        string[]? also = null,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(propertyName);
        if (also is null) return true;
        foreach (var dependent in also) Raise(dependent);
        return true;
    }
}
