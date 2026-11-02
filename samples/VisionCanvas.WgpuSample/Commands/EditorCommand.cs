using System.Diagnostics.CodeAnalysis;
using System.Windows.Input;

namespace VisionCanvas.WgpuSample.Commands;

public sealed class EditorCommand(Func<Task> execute, Func<bool> canExecute, Action<Exception> failed) : ICommand
{
    private bool _running;
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_running && canExecute();

    // WPF ICommand 경계에서만 void를 사용하고 완료·오류 처리는 Task 메서드에 둔다.
    [SuppressMessage("Reliability", "DAS1003", Justification = "ICommand.Execute는 void 계약이다. Task를 반환하는 ExecuteAsync에서 명령 예외를 관찰하고 보고한다.")]
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

