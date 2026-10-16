using System.Collections.Concurrent;
using TeachingToolWpf.Commands;

namespace TeachingToolWpf.Imaging.Tests;

public sealed class CommandContextTests
{
    [Fact]
    public void CompletionUsesOwnerContextAndRestoresCallerContext()
    {
        var previous = SynchronizationContext.Current;
        using var owner = new QueuedContext();
        using var input = new QueuedContext();
        try
        {
            SynchronizationContext.SetSynchronizationContext(owner);
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var command = new AsyncCommand(async () => await pending.Task, error => throw error);
            SynchronizationContext.SetSynchronizationContext(input);
            var running = command.ExecuteAsync();
            Assert.Same(input, SynchronizationContext.Current);
            Assert.False(command.CanExecute(null));
            pending.SetResult();
            Assert.True(owner.WaitForPost());
            Assert.Equal(0, input.Count);
            Assert.True(owner.Count > 0);
            SynchronizationContext.SetSynchronizationContext(owner);
            owner.Drain();
            Assert.True(running.IsCompletedSuccessfully);
            Assert.True(command.CanExecute(null));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private sealed class QueuedContext : SynchronizationContext, IDisposable
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _pending = new();
        private readonly ManualResetEventSlim _posted = new();
        public int Count => _pending.Count;
        public override void Post(SendOrPostCallback callback, object? state)
        {
            _pending.Enqueue((callback, state));
            _posted.Set();
        }
        public bool WaitForPost() => _posted.Wait(TimeSpan.FromSeconds(2));
        public void Dispose() => _posted.Dispose();

        public void Drain()
        {
            while (_pending.TryDequeue(out var item))
            {
                item.Callback(item.State);
            }
        }
    }
}
