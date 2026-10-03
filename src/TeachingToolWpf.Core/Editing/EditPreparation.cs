namespace TeachingToolWpf.Core.Editing;

/// <summary>CPU preparation yields scheduling priority to input and presentation on constrained machines.</summary>
public static class EditPreparation
{
    public static Task<T> Run<T>(Func<T> prepare, CancellationToken token = default) => Task.Run(() =>
    {
        var thread = Thread.CurrentThread;
        var priority = thread.Priority;
        try
        {
            if (priority > ThreadPriority.BelowNormal)
            {
                thread.Priority = ThreadPriority.BelowNormal;
            }

            token.ThrowIfCancellationRequested();
            return prepare();
        }
        finally
        {
            thread.Priority = priority;
        } // Never leave a shared pool thread with our scheduling policy.
    }, token);
}
