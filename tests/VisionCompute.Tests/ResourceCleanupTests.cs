using VisionCompute.ILGPU;

namespace VisionCompute.Tests;

public sealed class ResourceCleanupTests
{
    [Fact]
    public void FirstReleaseFailureStillReleasesEveryResourceAndReturnsUpload()
    {
        var calls = new List<string>();
        var failure = new InvalidOperationException("buffer release failed");
        var actual = Assert.Throws<InvalidOperationException>(() => ResourceCleanup.Complete(null,
            () =>
            {
                calls.Add("gradient-y");
                throw failure;
            },
            () => calls.Add("gradient-x"),
            () => calls.Add("smooth"),
            () => calls.Add("horizontal"),
            () => calls.Add("input"),
            () => calls.Add("return-upload")));

        Assert.Same(failure, actual);
        Assert.Equal(new[] { "gradient-y", "gradient-x", "smooth", "horizontal", "input", "return-upload" }, calls);
    }

    [Fact]
    public void SynchronizationAndReleaseFailuresAreAllPreservedWhileLaterCleanupRuns()
    {
        var synchronizeFailure = new InvalidOperationException("synchronize failed");
        var streamFailure = new InvalidOperationException("stream release failed");
        var calls = new List<string>();
        var error = Assert.Throws<AggregateException>(() => ResourceCleanup.Complete(null,
            () => throw synchronizeFailure,
            () => calls.Add("buffers"),
            () => throw streamFailure,
            () => calls.Add("accelerator"),
            () => calls.Add("context")));

        Assert.Equal(new[] { synchronizeFailure, streamFailure }, error.InnerExceptions);
        Assert.Equal(new[] { "buffers", "accelerator", "context" }, calls);
    }

    [Fact]
    public void ConstructorFailureRemainsFirstWhenCleanupAlsoFails()
    {
        var constructorFailure = new NotSupportedException("kernel compilation failed");
        var streamFailure = new InvalidOperationException("stream release failed");
        var contextReleased = false;
        var error = Assert.Throws<AggregateException>(() => ResourceCleanup.Complete(constructorFailure,
            () => throw streamFailure,
            () => contextReleased = true));

        Assert.Same(constructorFailure, error.InnerExceptions[0]);
        Assert.Same(streamFailure, error.InnerExceptions[1]);
        Assert.True(contextReleased);
    }

    [Fact]
    public void ConstructorFailureIsRethrownUnchangedAfterSuccessfulCleanup()
    {
        var failure = new NotSupportedException("device unavailable");
        var cleanupCalls = 0;
        var actual = Assert.Throws<NotSupportedException>(() => ResourceCleanup.Complete(failure,
            () => cleanupCalls++, () => cleanupCalls++));
        Assert.Same(failure, actual);
        Assert.Equal(2, cleanupCalls);
    }

    [Fact]
    public void SuccessfulCleanupRunsEveryActionInOrder()
    {
        var calls = new List<int>();
        ResourceCleanup.Complete(null, () => calls.Add(1), () => calls.Add(2), () => calls.Add(3));
        Assert.Equal(new[] { 1, 2, 3 }, calls);
    }
}
