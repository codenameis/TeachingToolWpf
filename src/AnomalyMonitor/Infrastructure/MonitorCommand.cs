using System.Windows.Input;

namespace AnomalyMonitor.Infrastructure;

public sealed class MonitorCommand(Func<Task> execute, Func<bool>? canExecute = null, Action<Exception>? reportError = null) : ICommand
{
    private bool _running;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke() ?? true);
    public async void Execute(object? parameter) => await ExecuteAsync();
    public async Task ExecuteAsync()
    {
        if (!CanExecute(null))
        {
            return;
        }

        _running = true;
        Refresh();
        try
        {
            await execute();
        }
        catch (Exception error) when (reportError is not null) { reportError(error); }
        finally { _running = false; Refresh(); }
    }
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
