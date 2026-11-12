using System.Diagnostics.CodeAnalysis;
using System.Windows.Input;

namespace VisionCanvas.SampleShell.Commands;

/// <summary>
/// 샘플 뷰모델이 공유하는 비동기·동기 실행 명령이다. 실행 중 중복 클릭을 차단하고 예외를 콜백으로 전달한다.
/// </summary>
public sealed class EditorCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool> _canExecute;
    private readonly Action<Exception> _failed;
    private bool _isRunning;

    public EditorCommand(Func<Task> execute, Func<bool> canExecute, Action<Exception> failed)
    {
        ArgumentNullException.ThrowIfNull(execute);
        ArgumentNullException.ThrowIfNull(canExecute);
        ArgumentNullException.ThrowIfNull(failed);

        _execute = execute;
        _canExecute = canExecute;
        _failed = failed;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_isRunning && _canExecute();

    // [Domain Why]: WPF ICommand.Execute는 void 계약이므로 경계에서만 async void를 두고 실제 실행은 ExecuteAsync로 위임한다.
    [SuppressMessage("Reliability", "DAS1003", Justification = "ICommand.Execute는 void 계약이다. Task를 반환하는 ExecuteAsync에서 명령 예외를 관찰하고 보고한다.")]
    public async void Execute(object? parameter) => await ExecuteAsync();

    public async Task ExecuteAsync()
    {
        if (!CanExecute(null))
        {
            return;
        }

        _isRunning = true;
        Refresh();
        try
        {
            await _execute();
        }
        catch (Exception error)
        {
            _failed(error);
        }
        finally
        {
            _isRunning = false;
            Refresh();
        }
    }

    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
