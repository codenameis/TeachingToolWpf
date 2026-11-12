using VisionCanvas.Commands;

namespace TeachingToolWpf.Commands;

public sealed class AsyncCommand(Func<Task> execute, Action<Exception> failed, Func<bool>? canExecute = null) : IRefreshableCommand
{
    private readonly AsyncRelayCommand _inner = new(execute, failed, canExecute);

    public event EventHandler? CanExecuteChanged
    {
        add => _inner.CanExecuteChanged += value;
        remove => _inner.CanExecuteChanged -= value;
    }

    public bool CanExecute(object? parameter) => _inner.CanExecute(parameter);

    public void Execute(object? parameter) => _inner.Execute(parameter);

    public Task ExecuteAsync(object? parameter = null) => _inner.ExecuteAsync(parameter);

    public void Refresh() => _inner.Refresh();
}
