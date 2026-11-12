using VisionCanvas.Commands;

namespace TeachingToolWpf.Commands;

public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : IRefreshableCommand
{
    private readonly VisionCanvas.Commands.RelayCommand _inner = new(execute, canExecute);

    public event EventHandler? CanExecuteChanged
    {
        add => _inner.CanExecuteChanged += value;
        remove => _inner.CanExecuteChanged -= value;
    }

    public bool CanExecute(object? parameter) => _inner.CanExecute(parameter);

    public void Execute(object? parameter) => _inner.Execute(parameter);

    public void Refresh() => _inner.Refresh();
}
