using System.Windows.Input;

namespace TeachingToolWpf.Commands;

public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
        {
            execute();
        }
    }

    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class AsyncCommand(Func<Task> execute, Action<Exception> failed, Func<bool>? canExecute = null) : ICommand
{
    private readonly SynchronizationContext? _ownerContext = SynchronizationContext.Current;
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    private bool _running;
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        try
        {
            await ExecuteAsync(parameter);
        }
        catch (Exception ex)
        {
            failed(ex);
        }
    }

    public Task ExecuteAsync(object? parameter = null)
    {
        var caller = SynchronizationContext.Current;
        if (_ownerContext is null || Environment.CurrentManagedThreadId != _ownerThread || ReferenceEquals(caller, _ownerContext))
        {
            return ExecuteCoreAsync(parameter);
        }
        // 입력/Idle 콜백의 낮은 우선순위를 검사 완료까지 물려받지 않는다.
        // 생성된 UI 문맥에서 비동기 처리를 시작하고 호출자의 문맥은 즉시 복원한다.
        try
        {
            SynchronizationContext.SetSynchronizationContext(_ownerContext);
            return ExecuteCoreAsync(parameter);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(caller);
        }
    }

    private async Task ExecuteCoreAsync(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _running = true;
        try
        {
            Refresh();
            await execute();
        }
        finally
        {
            _running = false;
            Refresh();
        }
    }

    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
