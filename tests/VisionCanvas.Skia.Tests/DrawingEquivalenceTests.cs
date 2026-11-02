using System.Numerics;
using SkiaSharp;
using VisionCanvas.Core.Editing;
using VisionCanvas.Rendering;
using VisionCanvas.Skia.Rendering;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Skia.Tests;

public sealed class DrawingEquivalenceTests
{
    [Theory]
    [InlineData(PixelKind.Gray8, false, true)]
    [InlineData(PixelKind.Gray8, true, true)]
    [InlineData(PixelKind.Gray16, false, true)]
    [InlineData(PixelKind.Bgra32, false, false)]
    [InlineData(PixelKind.Rgba32, false, false)]
    [InlineData(PixelKind.Rgba32, true, false)]
    [InlineData(PixelKind.Bgra32, false, true)]
    [InlineData(PixelKind.Rgba32, false, true)]
    [InlineData(PixelKind.Rgba32, true, true)]
    public void ImageSamplingMatchesOriginalTriangles(PixelKind kind, bool pointSampling, bool opaque)
    {
        using var pixels = new PixelBuffer(32, 24, kind);
        new Random(234).NextBytes(pixels.Bytes);
        if (opaque && kind is PixelKind.Bgra32 or PixelKind.Rgba32)
        {
            for (var index = 3; index < pixels.Bytes.Length; index += 4)
            {
                pixels.Bytes[index] = 255;
            }
        }
        using var texture = new SkiaTexture(32, 24, kind);
        texture.Update(pixels);
        using var original = new ReferenceSprites();
        using var current = new SkiaSprites();
        using var expected = new SKBitmap(320, 240);
        using var actual = new SKBitmap(320, 240);
        using var beforeCanvas = new SKCanvas(expected);
        using var afterCanvas = new SKCanvas(actual);
        foreach (var dpi in new[] { 1f, 1.5f })
        {
            foreach (var source in new[] { new BoundsD(0, 0, 1, 1), new BoundsD(.123, .17, .83, .92) })
            {
                foreach (var angle in new[] { 0d, 17d })
                {
                    foreach (var scale in new[] { .6, 1, 4.17 })
                    {
                        var corners = new RoiGeometry(new(83.25, 76.37), 32 * scale, 24 * scale, angle).FrameCorners;
                        var mesh = new SpriteMesh();
                        mesh.Begin(320, 240);
                        mesh.Quad(texture, corners[0], corners[1], corners[2], corners[3], Vector4.One, pointSampling, source);
                        beforeCanvas.ResetMatrix();
                        afterCanvas.ResetMatrix();
                        beforeCanvas.Clear(SKColors.DarkSlateGray);
                        afterCanvas.Clear(SKColors.DarkSlateGray);
                        beforeCanvas.Scale(dpi);
                        afterCanvas.Scale(dpi);
                        original.Draw(beforeCanvas, mesh, 0, mesh.Batches.Count, 320, 240);
                        current.Draw(afterCanvas, mesh, 0, mesh.Batches.Count, 320, 240);
                        var difference = LargestDifference(expected, actual);
                        Assert.True(difference <= 1, $"{kind}, point={pointSampling}, dpi={dpi}, uv={source}, angle={angle}, scale={scale}: max difference={difference}");
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData(.09, 0, 1)]
    [InlineData(9, 0, 1.5)]
    [InlineData(9, 17, 1)]
    [InlineData(32, -31, 1.5)]
    public void OrdinaryRoisAndSelectionKeepDeterministicPixels(double scale, double angle, double dpi)
    {
        var editor = new EditorSession();
        editor.InitializeImage(new("grid", "grid", 8192, 8192), ViewportTransform.Identity);
        var geometries = new List<RoiGeometry>(25);
        for (var row = -2; row <= 2; row++)
        {
            for (var column = -2; column <= 2; column++)
            {
                geometries.Add(new RoiGeometry(new(4096 + (column * 12), 4096 + (row * 12)), 8, 6, angle));
            }
        }
        var ids = editor.AddRange(geometries, selectAdded: true, isNameVisible: false);
        var document = editor.State.WithViewport(new(scale, new(200.37 - (4096 * scale), 150.23 - (4096 * scale))));
        using var first = new SkiaRois();
        using var second = new SkiaRois();
        using var expected = new SKBitmap((int)(400 * dpi), (int)(300 * dpi));
        using var actual = new SKBitmap(expected.Width, expected.Height);
        using var beforeCanvas = new SKCanvas(expected);
        using var afterCanvas = new SKCanvas(actual);
        foreach (var partial in new[] { false, true })
        {
            var state = partial
                ? document.WithSelection(RoiIdSet.CreateRange(ids.Take(10)))
                : document;
            var scene = new RenderScene(state, null, 0, 400, 300, dpi, new());
            beforeCanvas.ResetMatrix();
            afterCanvas.ResetMatrix();
            beforeCanvas.Clear(SKColors.DarkSlateGray);
            afterCanvas.Clear(SKColors.DarkSlateGray);
            beforeCanvas.Scale((float)dpi);
            afterCanvas.Scale((float)dpi);
            first.Draw(beforeCanvas, scene);
            second.Draw(afterCanvas, scene);
            Assert.Equal(expected.GetPixelSpan().ToArray(), actual.GetPixelSpan().ToArray());
        }
    }

    private static int LargestDifference(SKBitmap expected, SKBitmap actual)
    {
        var before = expected.GetPixelSpan();
        var after = actual.GetPixelSpan();
        var largest = 0;
        for (var index = 0; index < before.Length; index++)
        {
            largest = Math.Max(largest, Math.Abs(before[index] - after[index]));
        }
        return largest;
    }
}
