using IndustrialVision.Filters.Core;
using IndustrialVision.Filters.Cpu;

namespace IndustrialVision.Filters.Tests;

public sealed class CpuCancellationTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task RunningMorphologyCancelsWithoutPublishingAndCanBeReused(int parallelism)
    {
        using var input = new HostImage(new(1024, 1024, PixelFormat.Gray16));
        input.Data.Span.Fill(127);
        var filters = Enumerable.Range(0, 32)
            .Select(_ => (FilterSpec)new Morphology(MorphologyKind.BlackHat, 31)).ToArray();
        using var program = new CpuProgram(new(1, filters), input.Descriptor, new()
        {
            MaxDegreeOfParallelism = parallelism,
            Diagnostics = DiagnosticLevel.Stages
        });
        using var output = new HostImage(new(1024, 1024, PixelFormat.Float32));
        output.Data.Span.Fill(42);
        output.Validity.Span.Fill(17);
        using var cancellation = new CancellationTokenSource();
        var execution = Task.Factory.StartNew(() => program.Execute(input.View, output, cancellation.Token),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            // A completed first stage proves this is in-flight cancellation, not a pre-canceled call.
            Assert.True(SpinWait.SpinUntil(() =>
            {
                Thread.MemoryBarrier();
                return program.StageMilliseconds.Span[0] > 0 || execution.IsCompleted;
            }, TimeSpan.FromSeconds(10)));
            Assert.False(execution.IsCompleted);
            cancellation.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.DoesNotContain(output.Data.ToArray(), value => value != 42);
            Assert.DoesNotContain(output.Validity.ToArray(), value => value != 17);
        }
        finally
        {
            cancellation.Cancel();
            try
            {
                await execution;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
        }

        program.Execute(input.View, output, CancellationToken.None);
        Assert.DoesNotContain(output.Data.ToArray(), value => value != 0);
        Assert.DoesNotContain(output.Validity.ToArray(), value => value != 255);
    }
}
