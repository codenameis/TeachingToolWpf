namespace VisionCanvas.Commands;

/// <summary>
/// Asynchronous MVVM command preventing re-entrant execution, preserving the owner <see cref="SynchronizationContext"/>,
/// and routing unobserved UI-triggered faults to an explicit error handler.
/// </summary>
public sealed class AsyncRelayCommand : IRefreshableCommand
{
    private readonly Func<Task> _execute;
    private readonly Action<Exception> _onError;
    private readonly Func<bool>? _canExecute;
    private readonly SynchronizationContext? _ownerContext;
    private readonly int _ownerThread;
    private bool _isExecuting;

    public AsyncRelayCommand(
        Func<Task> execute,
        Action<Exception> onError,
        Func<bool>? canExecute = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        ArgumentNullException.ThrowIfNull(onError);
        _execute = execute;
        _onError = onError;
        _canExecute = canExecute;
        _ownerContext = SynchronizationContext.Current;
        _ownerThread = Environment.CurrentManagedThreadId;
    }

    public event EventHandler? CanExecuteChanged;

    public bool IsExecuting => _isExecuting;

    public bool CanExecute(object? parameter) => !_isExecuting && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        try
        {
            await ExecuteAsync(parameter);
        }
        catch (Exception exception)
        {
            _onError(exception);
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

        _isExecuting = true;
        try
        {
            Refresh();
            await _execute();
        }
        finally
        {
            _isExecuting = false;
            Refresh();
        }
    }

    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
