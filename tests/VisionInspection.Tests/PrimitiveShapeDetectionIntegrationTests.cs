using System.IO;
using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;
using TeachingToolWpf.Core.Imaging;
using TeachingToolWpf.Imaging.Matching;
using VisionAlgorithm;
using VisionAlgorithm.OpenSrc.ShapeDetection;

namespace TeachingToolWpf.Imaging.Tests;

public sealed class PrimitiveShapeDetectionIntegrationTests
{
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
        Assert.Equal(PrimitiveShapeKind.All, result.Shapes.Aggregate(PrimitiveShapeKind.None, (set, shape) => set | shape.Kind));
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
        Assert.Equal(PrimitiveShapeKind.All, output.Shapes.Aggregate(PrimitiveShapeKind.None, (set, shape) => set | shape.Kind));
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
