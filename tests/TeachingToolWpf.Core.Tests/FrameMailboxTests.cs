using TeachingToolWpf.Core.Imaging;

namespace TeachingToolWpf.Core.Tests;

public class FrameMailboxTests
{
    [Fact] public void PausedFrameSurvivesRendererRestartUntilLastLeaseReturns()
    {
        using var mailbox = new LatestFrameMailbox();
        var pixels = new PixelBuffer(8, 8, PixelKind.Gray8);
        mailbox.Publish(new(1, 0, pixels));
        mailbox.Take()!.Dispose();
        using var restored = mailbox.RetainLatest();
        Assert.NotNull(restored); Assert.Same(pixels, restored.Pixels);
        mailbox.Clear(); Assert.Equal(64, restored.Pixels.Length);
        restored.Dispose(); Assert.Throws<ObjectDisposedException>(() => pixels.Bytes);
    }
    [Fact] public void ReplacedFrameIsReturnedAndNewestFrameTransfersOwnership()
    {
        using var mailbox = new LatestFrameMailbox();
        var old = new VideoFrame(1, 0, new(8, 8, PixelKind.Gray8));
        var next = new VideoFrame(2, 0, new(8, 8, PixelKind.Gray8));
        mailbox.Publish(old); mailbox.Publish(next);
        Assert.Equal(1, mailbox.Dropped);
        Assert.Throws<ObjectDisposedException>(() => old.Pixels.Bytes);
        using var taken = mailbox.Take(); Assert.Same(next, taken);
        Assert.Null(mailbox.Take());
    }
    [Fact] public void PublishAfterShutdownReturnsFrameExactlyOnce()
    {
        var mailbox = new LatestFrameMailbox(); mailbox.Dispose();
        var frame = new VideoFrame(1, 0, new(8, 8, PixelKind.Gray16));
        mailbox.Publish(frame); mailbox.Dispose();
        Assert.Throws<ObjectDisposedException>(() => frame.Pixels.Bytes);
    }
}
