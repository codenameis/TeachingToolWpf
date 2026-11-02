using System.Windows.Threading;
namespace VisionCanvas.Wgpu.Tests;

internal static class Sta
{
    public static Task RunAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            _ = RunAsync();
            Dispatcher.Run();

            async Task RunAsync()
            {
                try
                {
                    await action();
                    completion.SetResult();
                }
                catch (Exception error)
                {
                    completion.SetException(error);
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }
}

