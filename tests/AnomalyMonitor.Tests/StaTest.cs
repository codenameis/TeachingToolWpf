using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace AnomalyMonitor.Tests;

internal static class StaTest
{
    public static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    public static Task Run(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            try
            {
                var task = action();
                _ = task.ContinueWith(finished =>
                {
                    if (finished.IsFaulted)
                    {
                        completion.TrySetException(finished.Exception!.InnerExceptions);
                    }
                    else if (finished.IsCanceled)
                    {
                        completion.TrySetCanceled();
                    }
                    else
                    {
                        completion.TrySetResult();
                    }

                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }, TaskScheduler.Default);
                Dispatcher.Run();
            }
            catch (Exception error) { completion.TrySetException(error); }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(150));
    }

    public static async Task Until(Func<bool> condition, string description, int seconds = 40)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed.TotalSeconds > seconds)
            {
                throw new TimeoutException(description);
            }

            await Task.Delay(25);
        }
    }

    public static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    public static void Click(Window window, string content)
    {
        var button = Descendants(window).OfType<Button>().Single(button => Equals(button.Content, content));
        Assert.True(button.IsEnabled);
        var peer = new System.Windows.Automation.Peers.ButtonAutomationPeer(button);
        ((System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();
    }
}
