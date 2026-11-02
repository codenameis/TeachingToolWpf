using System.Windows.Input;

namespace VisionCanvas.SkiaSample.Commands;

public sealed class EditorCommand(Func<Task> execute, Func<bool> canExecute, Action<Exception> failed) : ICommand
{
    private bool _running;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_running && canExecute();

    // WPF ICommand 경계에서만 void를 사용하고 완료·오류 처리는 Task 메서드에 둔다.
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
        catch (Exception error)
        {
            failed(error);
        }
        finally
        {
            _running = false;
            Refresh();
        }
    }

    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
