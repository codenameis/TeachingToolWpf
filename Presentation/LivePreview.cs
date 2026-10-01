using TeachingToolWpf.Core.Imaging;
using TeachingToolWpf.Core.Editing;

namespace TeachingToolWpf.Presentation;

public interface IFramePresenter
{
    void PublishFrame(VideoFrame frame);
    void ClearFrames();
}

public sealed class LivePreview(IFramePresenter presenter, Func<PixelKind, IFrameSource> factory) : IDisposable
{
    private CancellationTokenSource? cancellation;
    private Task? pump;
    public ImageInfo? Image { get; private set; }
    public bool IsRunning => cancellation is not null;
    public event Action? Changed;
    public event Action<Exception>? Failed;
    public ImageReference Reference(PixelKind kind)
    {
        var info = factory(kind).Info;
        return new($"live:{kind}", info.Name, info.Width, info.Height);
    }
    public void Start(PixelKind kind)
    {
        if (IsRunning) return;
        var source = factory(kind);
        Image = source.Info; cancellation = new();
        var token = cancellation.Token;
        Changed?.Invoke();
        pump = Task.Run(async () =>
        {
            try { await foreach (var frame in source.Frames(token)) presenter.PublishFrame(frame); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { Failed?.Invoke(ex); }
        }, token);
    }
    public Task StopAsync() => StopCoreAsync(false);
    public Task CloseAsync() => StopCoreAsync(true);
    private async Task StopCoreAsync(bool close)
    {
        if (cancellation is not { } current) { if (close) { Image = null; presenter.ClearFrames(); Changed?.Invoke(); } return; }
        current.Cancel();
        try { if (pump is not null) await pump; }
        catch (OperationCanceledException) { }
        finally { current.Dispose(); cancellation = null; pump = null; if (close) { Image = null; presenter.ClearFrames(); } Changed?.Invoke(); }
    }
    public void Dispose()
    {
        var current = cancellation;
        if (current is null) return;
        current.Cancel();
        // Closing the window must not block its dispatcher while the source unwinds.
        if (pump is { } running)
            _ = running.ContinueWith(_ => current.Dispose(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        else current.Dispose();
    }
}
