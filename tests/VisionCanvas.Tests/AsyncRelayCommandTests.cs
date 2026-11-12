using VisionCanvas.Commands;
using Xunit;

namespace TeachingToolWpf.Tests;

public sealed class AsyncRelayCommandTests
{
    [Fact]
    public async Task AsyncRelayCommand_PreventsReentrancyAndRoutesExceptionsToErrorHandler()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errorReported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        int executionCount = 0;
        int canExecuteChangedCount = 0;

        var command = new AsyncRelayCommand(
            async () =>
            {
                Interlocked.Increment(ref executionCount);
                await gate.Task;
                throw new InvalidOperationException("Simulated preset failure");
            },
            ex => errorReported.TrySetResult(ex));

        command.CanExecuteChanged += (_, _) => Interlocked.Increment(ref canExecuteChangedCount);

        Assert.True(command.CanExecute(null));

        command.Execute(null);
        Assert.False(command.CanExecute(null));
        Assert.True(command.IsExecuting);

        // Re-entrant click while running must be ignored.
        await command.ExecuteAsync();
        Assert.Equal(1, executionCount);

        gate.SetResult();
        Exception captured = await errorReported.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(command.IsExecuting);
        Assert.True(command.CanExecute(null));
        Assert.IsType<InvalidOperationException>(captured);
        Assert.Contains("Simulated preset failure", captured.Message, StringComparison.Ordinal);
        Assert.True(canExecuteChangedCount >= 2);
    }
}
