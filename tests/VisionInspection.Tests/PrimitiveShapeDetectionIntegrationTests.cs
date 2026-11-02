using System.IO;
using TeachingToolWpf.Imaging.Matching;
using VisionAlgorithm;
using VisionAlgorithm.OpenSrc.ShapeDetection;
using VisionGeometry;
using VisionImaging;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Tests;

public sealed class PrimitiveShapeDetectionIntegrationTests
{
    [Fact]
    public async Task PcbCompoundCrossesWindowOwnershipSeamOnceAndRoiMustContainBothComponents()
    {
        const int Width = 1800;
        const int Height = 320;
        var pixels = new byte[Width * Height];
        Array.Fill(pixels, (byte)25);
        foreach (var (kind, center) in new[] { (PrimitiveShapeKind.DoubleSquare, 768), (PrimitiveShapeKind.CrossHash, 1536) })
        {
            var mark = VisionAlgorithm.Tests.PcbMarkFixture.Create(kind, 27);
            for (var row = 0; row < mark.Height; row++)
            {
                mark.Pixels.Span.Slice(row * mark.Stride, mark.Width).CopyTo(pixels.AsSpan((row * Width) + center - 120));
            }
        }
        var service = new PrimitiveShapeDetectionService(new Loader(new(pixels, Width, Height, Width)), () => null);
        var reference = new ImageReference("fixture", "pcb seams", Width, Height);
        var result = await service.DetectAsync(reference, false, null, new(), default);
        Assert.False(result.Incomplete);
        Assert.Equal(2, result.Shapes.Length);
        var pair = Assert.Single(result.Shapes, shape => shape.Kind == PrimitiveShapeKind.DoubleSquare);
        Assert.Single(pair.AdditionalContours);
        Assert.InRange(Math.Abs(pair.Center.X - 768), 0, 1);
        Assert.All(pair.AdditionalContours[0], point => Assert.InRange(point.X, 700, 840));
        var serial = await service.DetectAsync(reference, false, null, new()
        {
            MaximumParallelism = 1
        }, default);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(result.Shapes), System.Text.Json.JsonSerializer.Serialize(serial.Shapes));
        var partial = await service.DetectAsync(reference, false, new(new(743, 125), 45, 100, 0),
            new()
            {
                Shapes = PrimitiveShapeKind.DoubleSquare
            }, default);
        Assert.Empty(partial.Shapes);
    }

    [Fact]
    public async Task CancellationDuringParallelProcessingDrainsAndDisposesReaderAndPreprocessor()
    {
        using var cancellation = new CancellationTokenSource();
        var loader = new Loader(PrimitiveShapeFixture.Create(4096, 256, new PrimitiveFixtureShape(PrimitiveShapeKind.Ring, 128, 128)));
        var preprocessor = new CancelingPreprocessor(cancellation);
        var service = new PrimitiveShapeDetectionService(loader, () => null, () => preprocessor);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DetectAsync(new("fixture", "fixture", 4096, 256),
            false, null, new()
            {
                UseGpu = true
            }, cancellation.Token));
        Assert.Equal(1, loader.Disposed);
        Assert.Equal(1, preprocessor.Disposals);
    }

    private sealed class CancelingPreprocessor(CancellationTokenSource cancellation) : IPrimitivePreprocessor
    {
        public int Disposals;
        public PrimitivePreprocessingDiagnostics Diagnostics { get; } = new("cancellation probe", 0, 0, 0, 0, 0);
        public GrayImage Median3x3(GrayImage image, CancellationToken token, Memory<byte> destination = default)
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Cancellation must be observed.");
        }
        public void Dispose() => Disposals++;
    }

    [Theory]
    [InlineData(128)]
    [InlineData(512)]
    public async Task ParallelWindowsPreserveExactOrderContoursAndSeams(int maximumSize)
    {
        var image = PrimitiveShapeFixture.Create(8192, 768,
            new(PrimitiveShapeKind.Cross, 768, 300, 6), new(PrimitiveShapeKind.Rectangle, 1536, 300, 1),
            new(PrimitiveShapeKind.Circle, 4096, 300), new(PrimitiveShapeKind.Triangle, 7900, 300, 35),
            new(PrimitiveShapeKind.Ring, 2304, 300), new(PrimitiveShapeKind.Circle, 2304, 300, Scale: .3),
            new(PrimitiveShapeKind.RectangleFrame, 3072, 300, 27), new(PrimitiveShapeKind.Rectangle, 3072, 300, 27, .3));
        var loader = new Loader(image);
        var service = new PrimitiveShapeDetectionService(loader, () => null);
        var reference = new ImageReference("fixture", "seams", image.Width, image.Height);
        var options = new PrimitiveDetectionOptions { MaximumParallelism = 1, MaximumSize = maximumSize };
        var serial = await service.DetectAsync(reference, false, null, options, default);
        var parallel = await service.DetectAsync(reference, false, null, options with
        {
            MaximumParallelism = 4
        }, default);
        Assert.Equal(8, serial.Shapes.Length);
        Assert.Equal(serial.Windows, parallel.Windows);
        Assert.False(parallel.Incomplete);
        Assert.Equal(2, loader.Disposed);
        for (var index = 0; index < serial.Shapes.Length; index++)
        {
            var expected = serial.Shapes[index];
            var actual = parallel.Shapes[index];
            Assert.Equal(expected with
            {
                Contour = actual.Contour,
                InnerContours = actual.InnerContours
            }, actual);
            Assert.Equal(expected.Contour.ToArray(), actual.Contour.ToArray());
            Assert.Equal(expected.InnerContours.Length, actual.InnerContours.Length);
            for (var hole = 0; hole < expected.InnerContours.Length; hole++)
            {
                Assert.Equal(expected.InnerContours[hole].ToArray(), actual.InnerContours[hole].ToArray());
                Assert.All(actual.InnerContours[hole], point =>
                {
                    Assert.InRange(Math.Abs(point.X - actual.Center.X), 0, 40);
                    Assert.InRange(Math.Abs(point.Y - actual.Center.Y), 0, 40);
                });
            }
        }
    }

    [Fact]
    public async Task GpuRequestWithoutProviderFailsExplicitlyAndCancellationDoesNotPublishResults()
    {
        var image = PrimitiveShapeFixture.Mixed();
        var loader = new Loader(image);
        var service = new PrimitiveShapeDetectionService(loader, () => null);
        var reference = new ImageReference("fixture", "fixture", image.Width, image.Height);
        await Assert.ThrowsAsync<NotSupportedException>(() => service.DetectAsync(reference, false, null, new() { UseGpu = true }, default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DetectAsync(reference, false, null, new(), new(true)));
        Assert.Equal(0, loader.Disposed);
    }

    [Fact]
    public async Task UnlimitedSearchDoesNotApplyAHiddenPerWindowLimit()
    {
        const int Side = 900;
        var pixels = new byte[Side * Side];
        for (var row = 0; row < 40; row++)
        {
            for (var column = 0; column < 40; column++)
            {
                for (var y = 0; y < 20; y++)
                {
                    pixels.AsSpan(((10 + (row * 22) + y) * Side) + 10 + (column * 22), 20).Fill(220);
                }
            }
        }
        var service = new PrimitiveShapeDetectionService(new Loader(new(pixels, Side, Side, Side)), () => null);
        var result = await service.DetectAsync(new("fixture", "dense", Side, Side), false, null,
            new()
            {
                MinimumSize = 6,
                MaximumSize = 24,
                Polarity = ForegroundPolarity.Bright
            }, default);
        Assert.Equal(1600, result.Shapes.Length);
        Assert.All(result.Shapes, shape => Assert.Equal(PrimitiveShapeKind.Rectangle, shape.Kind));
        Assert.False(result.Incomplete);
    }

    [Fact]
    public async Task ExclusionInsideSolidShapeRejectsItWithoutCreatingCutContours()
    {
        var image = PrimitiveShapeFixture.Create(512, 256, new(PrimitiveShapeKind.Circle, 128, 128),
            new(PrimitiveShapeKind.Rectangle, 380, 128));
        var loader = new Loader(image);
        var service = new PrimitiveShapeDetectionService(loader, () => null);
        var reference = new ImageReference("fixture", "fixture", 512, 256);
        var original = image.Pixels.ToArray();
        var baseline = await service.DetectAsync(reference, false, null, new(), default);
        Assert.Equal(2, baseline.Shapes.Length);
        var exclusion = new RoiExclusion(new(new(128, 128), 20, 20, 30), [new(MaskShape.Ellipse, new(0, 0, 1, 1))]);
        var masked = await service.DetectAsync(reference, false, null, new(), default, exclusion);
        Assert.Equal(PrimitiveShapeKind.Rectangle, Assert.Single(masked.Shapes).Kind);
        Assert.False(masked.Incomplete);
        Assert.Equal(original, image.Pixels.ToArray());
    }

    [Fact]
    public async Task MixedFixtureUsesOriginalPixelsAndExportsUnannotatedPng()
    {
        var image = PrimitiveShapeFixture.Mixed();
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/primitive-shapes-fixture.png"));
        PrimitiveShapeFixture.WritePng(path, image);
        var loader = new Loader(image);
        var result = await new PrimitiveShapeDetectionService(loader, () => null)
            .DetectAsync(new("fixture", "fixture", image.Width, image.Height), false, null, new(), default);
        Assert.Equal(4, result.Shapes.Length);
        Assert.Equal(PrimitiveShapeKind.Triangle | PrimitiveShapeKind.Rectangle | PrimitiveShapeKind.Circle | PrimitiveShapeKind.Cross,
            result.Shapes.Aggregate(PrimitiveShapeKind.None, (set, shape) => set | shape.Kind));
        Assert.False(result.Incomplete);
        Assert.Equal(1, loader.Disposed);
    }

    [Fact]
    public async Task PngLoadsThroughActualImageLoaderWithoutPixelChangesAndFindsFourShapes()
    {
        var image = PrimitiveShapeFixture.Mixed();
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/primitive-png-loader"));
        var path = Path.Combine(directory, "original.png");
        PrimitiveShapeFixture.WritePng(path, image);
        var reference = new ImageReference(path, "primitive fixture", image.Width, image.Height);
        var loader = new ImageLoader(Path.Combine(directory, "cache"));
        using (var source = await loader.LoadAsync(reference, new Progress<string>(), default))
        {
            Assert.Equal(PixelKind.Gray8, source.Info.Pixels);
            Assert.Equal(image.Width, source.Info.Width);
            Assert.Equal(image.Height, source.Info.Height);
        }
        using (var reader = await MatchingImageReader.OpenAsync(reference, false, loader, () => null, default))
        {
            var decoded = reader.Read(0, 0, image.Width, image.Height, default);
            Assert.Equal(image.Pixels.ToArray(), decoded.Pixels.ToArray());
        }
        var output = await new PrimitiveShapeDetectionService(loader, () => null)
            .DetectAsync(reference, false, null, new(), default);
        Assert.Equal(4, output.Shapes.Length);
        Assert.Equal(PrimitiveShapeKind.Triangle | PrimitiveShapeKind.Rectangle | PrimitiveShapeKind.Circle | PrimitiveShapeKind.Cross,
            output.Shapes.Aggregate(PrimitiveShapeKind.None, (set, shape) => set | shape.Kind));
        Assert.False(output.Incomplete);
    }

    [Fact]
    public async Task EightKWidthFindsObjectsAcrossTileSeamsOnceAndKeepsRoiShape()
    {
        var image = PrimitiveShapeFixture.Create(8192, 512, new(PrimitiveShapeKind.Cross, 768, 250, 29),
            new(PrimitiveShapeKind.Circle, 4096, 250), new(PrimitiveShapeKind.Rectangle, 7900, 250));
        var loader = new Loader(image);
        var service = new PrimitiveShapeDetectionService(loader, () => null);
        var reference = new ImageReference("fixture", "fixture", image.Width, image.Height);
        var output = await service.DetectAsync(reference, false, null, new(), default);
        Assert.Equal(3, output.Shapes.Length);
        Assert.False(output.Incomplete);
        Assert.True(output.Windows > 1);
        var circle = RoiGeometry.Create(RoiShapeKind.Circle, new(4096, 250), 110, 110);
        var limited = await service.DetectAsync(reference, false, circle, new(), default);
        Assert.Equal(PrimitiveShapeKind.Circle, Assert.Single(limited.Shapes).Kind);
        Assert.All(limited.Shapes[0].Contour, point => Assert.True(circle.Contains(new PointD(point.X, point.Y))));
        Assert.Equal(2, loader.Disposed);
    }

    [Fact]
    public async Task OutputLimitAndActualImageClippingAreReportedAsIncomplete()
    {
        var image = PrimitiveShapeFixture.Create(640, 480, new(PrimitiveShapeKind.Circle, 160, 150),
            new(PrimitiveShapeKind.Rectangle, 450, 150), new(PrimitiveShapeKind.Circle, 0, 350));
        var loader = new Loader(image);
        var result = await new PrimitiveShapeDetectionService(loader, () => null)
            .DetectAsync(new("fixture", "fixture", image.Width, image.Height), false, null, new()
            {
                MaximumResults = 1
            }, default);
        Assert.Single(result.Shapes);
        Assert.True(result.Incomplete);
        Assert.True(result.ClippedContours > 0);
    }

    [Fact]
    public async Task ExactContourRoiIsAllowedButCrossingAConcaveNotchIsRejected()
    {
        var image = PrimitiveShapeFixture.Create(256, 256, new PrimitiveFixtureShape(PrimitiveShapeKind.Rectangle, 128, 128));
        var service = new PrimitiveShapeDetectionService(new Loader(image), () => null);
        var reference = new ImageReference("fixture", "fixture", image.Width, image.Height);
        var entire = await service.DetectAsync(reference, false, null, new(), default);
        var contour = Assert.Single(entire.Shapes).Contour;
        var exact = RoiGeometry.Polygon(contour.Select(point => new PointD(point.X, point.Y)));
        Assert.Single((await service.DetectAsync(reference, false, exact, new(), default)).Shapes);
        var concave = RoiGeometry.Polygon([new(40, 40), new(220, 40), new(220, 200), new(170, 200),
            new(170, 130), new(150, 130), new(150, 200), new(40, 200)]);
        Assert.All(contour, point => Assert.True(concave.Contains(new PointD(point.X, point.Y))));
        Assert.Empty((await service.DetectAsync(reference, false, concave, new(), default)).Shapes);
    }

    private sealed class Loader(GrayImage image) : IImageLoader
    {
        public int Disposed;

        public Task<ITileSource> LoadAsync(ImageReference imageReference, IProgress<string> progress, CancellationToken token)
            => Task.FromResult<ITileSource>(new Source(this, image));
    }

    private sealed class Source(Loader owner, GrayImage image) : ITileSource
    {
        public ImageInfo Info { get; } = new("fixture", "fixture", image.Width, image.Height, PixelKind.Gray8, 1);

        public PixelBuffer Read(TileKey key, CancellationToken token)
        {
            Assert.Equal(0, key.Level);
            var left = key.X * 512;
            var top = key.Y * 512;
            var width = Math.Min(512, image.Width - left);
            var height = Math.Min(512, image.Height - top);
            var buffer = new PixelBuffer(width, height, PixelKind.Gray8);
            for (var row = 0; row < height; row++)
            {
                image.Pixels.Span.Slice(((top + row) * image.Stride) + left, width).CopyTo(buffer.Span.Slice(row * buffer.Stride, width));
            }
            return buffer;
        }

        public void Dispose() => owner.Disposed++;
    }
}
