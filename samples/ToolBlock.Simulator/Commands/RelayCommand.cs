// File: samples/ToolBlock.Simulator/Commands/RelayCommand.cs
using System.Windows.Input;

namespace ToolBlock.Simulator.Commands;

/// <summary>
/// Lightweight synchronous <see cref="ICommand"/> implementation for MVVM bindings in the ToolBlock Simulator.
/// </summary>
public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    private readonly Action _execute = execute ?? throw new ArgumentNullException(nameof(execute));
    private readonly Func<bool>? _canExecute = canExecute;

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter)
        => _canExecute is null || _canExecute();

    public void Execute(object? parameter)
        => _execute();
}
