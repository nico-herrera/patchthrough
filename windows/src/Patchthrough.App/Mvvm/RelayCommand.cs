using System.Windows.Input;

namespace Patchthrough.App.Mvvm;

/// <summary>A command backed by a delegate.</summary>
public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => execute();

    /// <summary>
    /// Re-ask whether this command can run. Called explicitly rather than through
    /// CommandManager.RequerySuggested, which fires on every keystroke and focus
    /// change and would re-evaluate state during a recording for no reason.
    /// </summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>A command backed by a delegate that takes the bound item.</summary>
public sealed class RelayCommand<T>(Action<T> execute, Func<T, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) =>
        parameter is T value ? canExecute?.Invoke(value) ?? true : parameter is null && canExecute is null;

    public void Execute(object? parameter)
    {
        if (parameter is T value) execute(value);
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
