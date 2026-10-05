using TeachingToolWpf.Imaging.Matching;
using VisionAlgorithm;
using VisionAlgorithm.OpenSrc.PatternMatching;

namespace TeachingToolWpf.Imaging.Tests;

public sealed class BoundedInspectionCropSchedulerTests
{
    [Theory]
    [InlineData(PreparedSearchImage.MaximumPixels, PreparedSearchImage.MaximumPixels + 1)]
    [InlineData(ShapeTrainer.MaximumPixels, ShapeTrainer.MaximumPixels + 1)]
    public void CropAboveHardLimitIsRejectedBeforeOriginalRead(int maximum, int pixels)
    {
        var read = false;
        Assert.Throws<ArgumentException>(() => BoundedInspectionCropScheduler.Run(new[] { pixels }, count => count,
            (_, _) =>
            {
                read = true;
                return new GrayImage(new byte[1], 1, 1, 1);
            }, (_, _, _) => 0, (_, _) => { }, new(4, PreparedSearchImage.MaximumPixels, maximum), default));
        Assert.False(read);
    }

    [Fact]
    public async Task ReadFailureDrainsActiveWorkerBeforeReturning()
    {
        using var release = new ManualResetEventSlim();
        var workerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerExited = false;
        var run = Task.Run(() => BoundedInspectionCropScheduler.Run(new[] { 0, 1 }, _ => 1,
            (index, _) =>
            {
                if (index == 1)
                {
                    workerEntered.Task.GetAwaiter().GetResult();
                    readFailed.SetResult();
                    throw new IOException("Injected original read failure.");
                }
                return new GrayImage(new byte[1], 1, 1, 1);
            }, (_, _, token) =>
            {
                try
                {
                    workerEntered.SetResult();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                    token.ThrowIfCancellationRequested();
                    return 0;
                }
                finally
                {
                    Volatile.Write(ref workerExited, true);
                }
            }, (_, _) => { }, new(2, 2, 2), default));
        try
        {
            await readFailed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(run.IsCompleted);
            Assert.False(Volatile.Read(ref workerExited));
        }
        finally
        {
            release.Set();
        }
        await Assert.ThrowsAsync<IOException>(() => run);
        Assert.True(Volatile.Read(ref workerExited));
    }
}
