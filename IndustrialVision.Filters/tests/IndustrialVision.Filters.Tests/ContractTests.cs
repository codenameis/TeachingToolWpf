using System.Buffers.Binary;
using IndustrialVision.Filters.Core;
using IndustrialVision.Filters.Gpu;
using IndustrialVision.Filters.Pipeline;

namespace IndustrialVision.Filters.Tests;

public sealed class ContractTests
{
    [Fact]
    public void ImplementationBugsCannotBecomeFallback()
    {
        var descriptor = new ImageDescriptor(2, 2, PixelFormat.Gray16);
        var recipe = new Recipe(1, new Gaussian(1, 1), new Threshold(0));
        var options = new ExecutionOptions { Mode = ExecutionMode.CpuGpu };
        Assert.Throws<ArgumentException>(() => new FilterEngine(new FailingBackend(false)).Compile(recipe, descriptor, options));
        using var fallback = new FilterEngine(new FailingBackend(true)).Compile(recipe, descriptor, options);
        Assert.All(fallback.Stages, s => Assert.Contains("allocation exhaustion", s.Reason));
    }

    [Fact]
    public void StageDiagnosticsMeasureOnlyWhenRequested()
    {
        using var input = AccuracyTests.MakeInput(PixelFormat.Gray16, 17, 13, "random");
        using var plan = new FilterEngine().Compile(new(1, new Gaussian(1, 1), new Threshold(50)), input.Descriptor,
            new()
            {
                Diagnostics = DiagnosticLevel.Stages
            });
        using var output = new HostImage(plan.Output);
        var report = plan.Execute(input.View, output);
        Assert.Equal(2, report.StageMilliseconds.Count);
        Assert.True(report.StageMilliseconds.Sum() <= report.Milliseconds);
    }

    private sealed class FailingBackend(bool unavailable) : IGpuBackend
    {
        public BackendCapabilities GetCapabilities() => new("test double", "not a GPU", 1024 * 1024, []);
        public string? GetUnsupportedReason(Recipe recipe, ImageDescriptor input, ExecutionOptions options) => null;

        public IGpuProgram Compile(Recipe recipe, ImageDescriptor input, ExecutionOptions options)
        {
            if (unavailable)
            {
                throw new GpuUnavailableException("Reported allocation exhaustion.");
            }
            throw new ArgumentException("Implementation bug must propagate.");
        }
    }

    [Fact]
    public void RejectsStrideOverflowAndNonfiniteValidInput()
    {
        Assert.Throws<ArgumentException>(() => new ImageDescriptor(2, 2, PixelFormat.Gray16, -4));
        Assert.Throws<OverflowException>(() => new ImageDescriptor(int.MaxValue, 1, PixelFormat.Float32));
        Assert.Throws<OverflowException>(() => new ImageDescriptor(1, 1, PixelFormat.Gray8, originX: long.MaxValue));
        Assert.Throws<ArgumentException>(() => new ImageView(new(2, 2, PixelFormat.Gray16), new byte[7]));
        using var input = new HostImage(new(1, 1, PixelFormat.Float32));
        using var plan = new FilterEngine().Compile(new(1, new Threshold(0)), input.Descriptor, new());
        using var output = new HostImage(plan.Output);
        foreach (var value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            BinaryPrimitives.WriteSingleLittleEndian(input.Data.Span, value);
            Assert.Throws<ArgumentException>(() => plan.Execute(input.View, output));
        }
    }

    [Theory]
    [InlineData(12, 4095)]
    [InlineData(14, 16383)]
    [InlineData(16, 65535)]
    public void Gray16RetainsOneUnitAndValidBits(int bits, int maximum)
    {
        using var image = new HostImage(new(2, 1, PixelFormat.Gray16, validBits: bits));
        image.WriteFloat([(float)maximum - 1, maximum]);
        using var plan = new FilterEngine().Compile(new(1, new Threshold(maximum - 1)), image.Descriptor, new());
        using var output = new HostImage(plan.Output);
        plan.Execute(image.View, output);
        Assert.Equal(new byte[] { 0, 255 }, output.Data.ToArray());
        if (bits < 16)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(image.Data.Span, (ushort)(maximum + 1));
            Assert.Throws<ArgumentException>(() => plan.Execute(image.View, output));
        }
    }

    [Fact]
    public void FlatFieldPreservesSignedValuesAndReportsInvalidDenominators()
    {
        using var image = new HostImage(new(5, 1, PixelFormat.Gray16, exposureKey: "10ms"));
        image.WriteFloat([9, 20, 20, 20, 20]);
        var flat = new FlatField(new(image.Descriptor, [20, 10, 9, 10.000001f, 30], 1),
            dark: new(image.Descriptor, [10, 10, 10, 10, 10], 1), minimumDenominator: 0.01f);
        using var plan = new FilterEngine().Compile(new(1, flat), image.Descriptor, new());
        using var output = new HostImage(plan.Output);
        plan.Execute(image.View, output);
        var values = AccuracyTests.Read(output.View);
        Assert.Equal(-0.1f, values[0]);
        Assert.Equal(0.5f, values[4]);
        Assert.Equal(new byte[] { 255, 0, 0, 0, 255 }, output.Validity.ToArray());
        using var maskPlan = new FilterEngine().Compile(new(1, flat, new Threshold(0)), image.Descriptor, new());
        using var mask = new HostImage(maskPlan.Output);
        maskPlan.Execute(image.View, mask);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 255 }, mask.Data.ToArray());
        Assert.Equal(output.Validity.ToArray(), mask.Validity.ToArray());
    }

    [Fact]
    public void CalibrationIsImmutableAndChecksCoordinatesAndExposure()
    {
        var descriptor = new ImageDescriptor(2, 1, PixelFormat.Gray16, originX: 100, exposureKey: "10ms");
        float[] values = [1, 2];
        var map = new CalibrationMap(descriptor, values, 1);
        values[0] = 100;
        Assert.Equal(1, map.Values[0]);
        var recipe = new Recipe(7, new FlatField(map));
        var engine = new FilterEngine();
        Assert.Throws<ArgumentException>(() => engine.Compile(recipe, new(2, 1, PixelFormat.Gray16, exposureKey: "10ms"), new()));
        Assert.Throws<ArgumentException>(() => engine.Compile(recipe, new(2, 1, PixelFormat.Gray16, originX: 100, exposureKey: "20ms"), new()));
        using var plan = engine.Compile(recipe, descriptor, new());
        Assert.Equal(7, plan.RecipeVersion);
    }

    [Fact]
    public void FallbackIsExplicitAndInputErrorsAreNotFallbacks()
    {
        var engine = new FilterEngine();
        var descriptor = new ImageDescriptor(4, 4, PixelFormat.Gray16);
        var recipe = new Recipe(1, new Gaussian(1, 1), new Threshold(5));
        using var plan = engine.Compile(recipe, descriptor, new()
        {
            Mode = ExecutionMode.CpuGpu
        });
        Assert.All(plan.Stages, stage => Assert.Contains("No GPU backend", stage.Reason));
        Assert.Throws<NotSupportedException>(() => engine.Compile(recipe, descriptor,
            new()
            {
                Mode = ExecutionMode.CpuGpu,
                Fallback = FallbackPolicy.Throw
            }));
        Assert.Throws<MemoryBudgetException>(() => engine.Compile(recipe, descriptor, new() { MemoryBudgetBytes = 32 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.Compile(recipe, descriptor, new() { MaxDegreeOfParallelism = 0 }));
    }

    [Fact]
    public void RoiViewPreservesPaddingOriginAndInput()
    {
        using var image = AccuracyTests.MakeInput(PixelFormat.Gray16, 19, 11, "ramp");
        var original = image.Data.ToArray();
        var view = image.View.Region(4, 3, 5, 7);
        Assert.Equal(4, view.Descriptor.OriginX);
        Assert.Equal(3, view.Descriptor.OriginY);
        Assert.Equal(image.Descriptor.StrideBytes, view.Descriptor.StrideBytes);
        using var plan = new FilterEngine().Compile(new(1, new Gaussian(2, 1)), view.Descriptor, new());
        using var output = new HostImage(plan.Output);
        plan.Execute(view, output);
        AccuracyTests.Compare(ReferenceFilters.Execute(AccuracyTests.Read(view), 5, 7, new(1, new Gaussian(2, 1))), output.View);
        Assert.Equal(original, image.Data.ToArray());
    }

    [Fact]
    public void RejectsAliasingAndCancellationDoesNotPublish()
    {
        using var image = new HostImage(new(9, 9, PixelFormat.Gray8));
        using var plan = new FilterEngine().Compile(new(1, new Threshold(0)), image.Descriptor, new());
        Assert.Throws<ArgumentException>(() => plan.Execute(image.View, image));
        using var output = new HostImage(plan.Output);
        output.Data.Span.Fill(42);
        Assert.Throws<OperationCanceledException>(() => plan.Execute(image.View, output, new CancellationToken(true)));
        Assert.All(output.Data.ToArray(), b => Assert.Equal(42, b));
        plan.Dispose();
        Assert.Throws<ObjectDisposedException>(() => plan.Execute(image.View, output));
    }

    [CudaFact]
    public void CudaInvalidMaskBudgetFallbackAndCancellation()
    {
        using var backend = new CudaBackend();
        using var input = AccuracyTests.MakeInput(PixelFormat.Gray16, 17, 13, "random");
        input.Validity.Span[91] = 0;
        var recipe = new Recipe(1, new Gaussian(2, 1), new Morphology(MorphologyKind.BlackHat, 2), new Threshold(100));
        var engine = new FilterEngine(backend);
        using var plan = engine.Compile(recipe, input.Descriptor, new()
        {
            Mode = ExecutionMode.CpuGpu
        });
        using var output = new HostImage(plan.Output);
        plan.Execute(input.View, output);
        var values = AccuracyTests.Read(input.View);
        AccuracyTests.Compare(ReferenceFilters.Execute(values, 17, 13, recipe), output.View);
        Assert.Throws<InvalidOperationException>(() => backend.Dispose());
        Assert.Throws<OperationCanceledException>(() => plan.ExecuteDevice(new CancellationToken(true)));
        Assert.Throws<InvalidOperationException>(() => plan.CopyOutputToHost(output));
        plan.ExecuteDevice();
        plan.CopyOutputToHost(output);
        using var fallback = engine.Compile(recipe, input.Descriptor, new()
        {
            Mode = ExecutionMode.CpuGpu,
            GpuMemoryBudgetBytes = 1
        });
        Assert.False(fallback.UsesGpu);
        Assert.All(fallback.Stages, s => Assert.Contains("GPU workspace", s.Reason));
    }
}
