using IndustrialVision.Filters.Core;
using IndustrialVision.Filters.Gpu;
using IndustrialVision.Filters.Pipeline;

namespace IndustrialVision.Filters.Tests;

public sealed class TileTests
{
    [Theory]
    [InlineData(PixelFormat.Gray8)]
    [InlineData(PixelFormat.Gray16)]
    [InlineData(PixelFormat.Float32)]
    public void TiledAndFullAgreeAcrossAccumulatedHalo(PixelFormat format)
    {
        VerifyTiles(new FilterEngine(), new ExecutionOptions(), format);
    }

    [CudaFact]
    public void ActualCudaTilesAgreeWithFull()
    {
        using var backend = new CudaBackend();
        VerifyTiles(new FilterEngine(backend), new ExecutionOptions { Mode = ExecutionMode.CpuGpu, Fallback = FallbackPolicy.Throw }, PixelFormat.Gray16);
    }

    private static void VerifyTiles(FilterEngine engine, ExecutionOptions options, PixelFormat format)
    {
        using var input = AccuracyTests.MakeInput(format, 37, 29, "random");
        var flat = new FlatField(new(input.Descriptor, Enumerable.Repeat(1.1f, 37 * 29).ToArray(), 1), FlatFieldKind.ReciprocalGain);
        var recipe = new Recipe(1, flat, new Gaussian(2, 1.3), new Morphology(MorphologyKind.BlackHat, 3));
        using var plan = engine.Compile(recipe, input.Descriptor, options);
        Assert.Equal(8, plan.RequiredHalo);
        using var full = new HostImage(plan.Output);
        using var tiled = new HostImage(plan.Output);
        plan.Execute(input.View, full);
        var sink = new MemorySink(tiled);
        var report = new TileExecutor(engine).Execute(recipe, new MemoryTileSource(input.View), sink, options, 7, 5);
        Assert.Equal(36, report.Tiles);
        Assert.True(sink.Completed);
        Assert.False(sink.Aborted);
        Assert.Equal(full.Data.ToArray(), tiled.Data.ToArray());
        Assert.Equal(full.Validity.ToArray(), tiled.Validity.ToArray());
    }

    [Fact]
    public void RoiContextAndIsolationHaveDifferentMeaning()
    {
        using var image = new HostImage(new(13, 11, PixelFormat.Gray16));
        var pixels = new float[143];
        pixels[(5 * 13) + 2] = 60000;
        image.WriteFloat(pixels);
        var recipe = new Recipe(1, new Gaussian(2, 1));
        var region = new ImageRegion(3, 3, 5, 5);
        var options = new ExecutionOptions();
        using var contextual = new HostImage(new(5, 5, PixelFormat.Float32));
        using var isolated = new HostImage(new(5, 5, PixelFormat.Float32));
        var executor = new TileExecutor(new FilterEngine());
        executor.Execute(recipe, new MemoryTileSource(image.View), new MemorySink(contextual), options, 2, 2, region);
        executor.Execute(recipe, new MemoryTileSource(image.View), new MemorySink(isolated), options, 2, 2, region, RoiBoundaryMode.Isolated);
        Assert.True(AccuracyTests.Read(contextual.View).Sum() > 0);
        Assert.Equal(0, AccuracyTests.Read(isolated.View).Sum());
    }

    [Fact]
    public void CanceledTilesAbortWithoutCompletion()
    {
        using var image = AccuracyTests.MakeInput(PixelFormat.Gray8, 17, 13, "random");
        using var output = new HostImage(new(17, 13, PixelFormat.Gray8));
        var sink = new MemorySink(output);
        Assert.Throws<OperationCanceledException>(() => new TileExecutor(new FilterEngine()).Execute(
            new(1, new Threshold(0)), new MemoryTileSource(image.View), sink, new(), token: new CancellationToken(true)));
        Assert.True(sink.Aborted);
        Assert.False(sink.Completed);
    }

    [Fact]
    public void SourceLargerThanASingleManagedArrayIsReadAsTiles()
    {
        var source = new LargeGeneratedSource();
        Assert.True(source.Descriptor.RequiredBytes > int.MaxValue);
        using var output = new HostImage(new(32, 32, PixelFormat.Gray8));
        var report = new TileExecutor(new FilterEngine()).Execute(new(1, new Threshold(10000)), source,
            new MemorySink(output), new()
            {
                MemoryBudgetBytes = 1024 * 1024
            }, 16, 16, new(65000, 65000, 32, 32));
        Assert.Equal(4, report.Tiles);
        Assert.True(report.PeakHostBytes < 1024 * 1024);
        for (var y = 0; y < 32; y++)
        {
            for (var x = 0; x < 32; x++)
            {
                Assert.Equal((130000 + x + y) % 16384 > 10000 ? (byte)255 : (byte)0, output.Data.Span[(y * 32) + x]);
            }
        }
    }

    [Fact]
    public void BudgetAutomaticallyReducesTileSize()
    {
        using var image = AccuracyTests.MakeInput(PixelFormat.Gray16, 97, 79, "random");
        using var output = new HostImage(new(97, 79, PixelFormat.Gray8));
        var report = new TileExecutor(new FilterEngine()).Execute(new(1, new Gaussian(1, 1), new Threshold(20)),
            new MemoryTileSource(image.View), new MemorySink(output),
            new()
            {
                MemoryBudgetBytes = 32 * 1024,
                MaxDegreeOfParallelism = 1
            }, 512, 256);
        Assert.True(report.TileWidth < 512);
        Assert.True(report.PeakHostBytes <= 32 * 1024);
    }

    private sealed class LargeGeneratedSource : IImageTileSource
    {
        public ImageDescriptor Descriptor { get; } = new(65536, 65536, PixelFormat.Gray16, validBits: 14);

        public void Read(ImageRegion region, HostImage destination, CancellationToken token)
        {
            for (var y = 0; y < region.Height; y++)
            {
                token.ThrowIfCancellationRequested();
                for (var x = 0; x < region.Width; x++)
                {
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(
                        destination.Data.Span.Slice((y * destination.Descriptor.StrideBytes) + (x * 2), 2),
                        (ushort)((region.X + x + region.Y + y) % 16384));
                }
            }
        }
    }

    private sealed class MemorySink(HostImage image) : IImageTileSink
    {
        public bool Completed
        {
            get; private set;
        }
        public bool Aborted
        {
            get; private set;
        }

        public void Write(int x, int y, ImageView tile, CancellationToken token)
        {
            var bytes = tile.Descriptor.BytesPerPixel;
            for (var row = 0; row < tile.Descriptor.Height; row++)
            {
                token.ThrowIfCancellationRequested();
                tile.Data.Span.Slice(row * tile.Descriptor.StrideBytes, tile.Descriptor.Width * bytes)
                    .CopyTo(image.Data.Span.Slice(((row + y) * image.Descriptor.StrideBytes) + (x * bytes)));
                tile.Validity.Span.Slice(row * tile.MaskStride, tile.Descriptor.Width)
                    .CopyTo(image.Validity.Span.Slice(((row + y) * image.Descriptor.Width) + x));
            }
        }

        public void Complete() => Completed = true;
        public void Abort() => Aborted = true;
    }
}
