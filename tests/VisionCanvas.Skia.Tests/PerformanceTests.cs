using System.Diagnostics;
using System.IO;
using System.Text.Json;
using SkiaSharp;
using VisionCanvas.Core.Editing;
using VisionCanvas.Rendering;
using VisionCanvas.Skia.Rendering;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Skia.Tests;

/// <summary>앱에 계측 코드를 넣지 않고 같은 장면·이동 경로·표본 수로 CPU 렌더를 비교한다.</summary>
public sealed class PerformanceTests
{
    [PerformanceFact]
    public void MeasureOrdinaryRoisAndImageDrawing()
    {
        var results = new List<object>();
        using var bitmap = new SKBitmap(1200, 800, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        using var rois = new SkiaRois();
        RenderScene[] detailScenes = [];
        var editor = new EditorSession();
        editor.InitializeImage(new("perf", "perf", 8192, 8192), ViewportTransform.Identity);
        var geometries = new List<RoiGeometry>(101 * 101);
        const int sideCount = 101;
        var pitch = 8192 * .45 / sideCount;
        var width = pitch * .65;
        var half = (sideCount - 1) / 2d;
        for (var row = 0; row < sideCount; row++)
        {
            var y = 4096 + ((row - half) * pitch);
            for (var column = 0; column < sideCount; column++)
            {
                var x = 4096 + ((column - half) * pitch);
                geometries.Add(new(new(x, y), width, width, 0));
            }
        }
        editor.AddRange(geometries, selectAdded: true, isNameVisible: false);
        var baseDocument = editor.State;
        foreach (var (name, scale, _) in new (string, double, double)[]
        {
            ("fit", .09, 0), ("summary", 7, 0), ("detail", 9, 0),
            ("rotated-detail", 9, 17), ("close", 32, 0), ("rotated-close", 32, 17)
        })
        {
            var scenes = Enumerable.Range(0, 24).Select(frame => new RenderScene(
                baseDocument.WithViewport(new(scale, new(600 - (4096 * scale) + (frame * .37), 400 - (4096 * scale) + (frame * .23)))),
                null, 0, 1200, 800, 1, new())).ToArray();
            if (name == "detail")
            {
                detailScenes = scenes;
            }
            results.Add(Compare(name, frame => rois.Draw(canvas, scenes[frame % scenes.Length]),
                frame => rois.Draw(canvas, scenes[frame % scenes.Length]), canvas, bitmap));
        }

        using var pixels = new PixelBuffer(1024, 1024, PixelKind.Gray8);
        for (var index = 0; index < pixels.Bytes.Length; index++)
        {
            pixels.Bytes[index] = (byte)((index * 17) + (index / pixels.Width));
        }
        using var texture = new SkiaTexture(pixels.Width, pixels.Height, pixels.Kind);
        texture.Update(pixels);
        using var sprites = new SkiaSprites();
        using var referenceSprites = new ReferenceSprites();
        var mesh = new SpriteMesh();
        mesh.Begin(1200, 800);
        mesh.Rectangle(texture, new(0, 0, 1200, 800), System.Numerics.Vector4.One);
        results.Add(Compare("image", _ => referenceSprites.Draw(canvas, mesh, 0, mesh.Batches.Count, 1200, 800),
            _ => sprites.Draw(canvas, mesh, 0, mesh.Batches.Count, 1200, 800), canvas, bitmap));
        results.Add(Compare("image-and-detail", frame =>
        {
            referenceSprites.Draw(canvas, mesh, 0, mesh.Batches.Count, 1200, 800);
            rois.Draw(canvas, detailScenes[frame % detailScenes.Length]);
        }, frame =>
        {
            sprites.Draw(canvas, mesh, 0, mesh.Batches.Count, 1200, 800);
            rois.Draw(canvas, detailScenes[frame % detailScenes.Length]);
        }, canvas, bitmap));

        using var colorPixels = new PixelBuffer(1024, 1024, PixelKind.Bgra32);
        for (var index = 0; index < pixels.Bytes.Length; index++)
        {
            colorPixels.Bytes[index * 4] = pixels.Bytes[index];
            colorPixels.Bytes[(index * 4) + 1] = (byte)(255 - pixels.Bytes[index]);
            colorPixels.Bytes[(index * 4) + 2] = 128;
            colorPixels.Bytes[(index * 4) + 3] = 255;
        }
        using var colorTexture = new SkiaTexture(1024, 1024, PixelKind.Bgra32);
        colorTexture.Update(colorPixels);
        mesh.Begin(1200, 800);
        mesh.Rectangle(colorTexture, new(0, 0, 1200, 800), System.Numerics.Vector4.One);
        results.Add(Compare("image-bgra", _ => referenceSprites.Draw(canvas, mesh, 0, mesh.Batches.Count, 1200, 800),
            _ => sprites.Draw(canvas, mesh, 0, mesh.Batches.Count, 1200, 800), canvas, bitmap));

        var path = SampleTests.ArtifactPath($"performance-{Environment.GetEnvironmentVariable("VISIONCANVAS_SKIA_PERF")}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            Timestamp = DateTimeOffset.Now,
            Output = "1200x800 CPU raster; clear/hash excluded; 8 warmup + 48 measured frames; 24 panning positions",
            Results = results
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static object Compare(string name, Action<int> before, Action<int> after, SKCanvas canvas, SKBitmap bitmap)
    {
        var beforeTimes = new List<double>();
        var afterTimes = new List<double>();
        var beforeAllocations = new List<long>();
        var afterAllocations = new List<long>();
        var differences = 0;
        var maximumDifference = 0;
        var original = new byte[bitmap.ByteCount];
        var current = new byte[bitmap.ByteCount];
        for (var frame = -8; frame < 48; frame++)
        {
            var index = Math.Max(0, frame);
            double beforeMs;
            double afterMs;
            long beforeBytes;
            long afterBytes;
            if (frame % 2 == 0)
            {
                (beforeMs, beforeBytes) = Draw(before, index, original);
                (afterMs, afterBytes) = Draw(after, index, current);
            }
            else
            {
                (afterMs, afterBytes) = Draw(after, index, current);
                (beforeMs, beforeBytes) = Draw(before, index, original);
            }
            if (frame >= 0)
            {
                beforeTimes.Add(beforeMs);
                afterTimes.Add(afterMs);
                beforeAllocations.Add(beforeBytes);
                afterAllocations.Add(afterBytes);
                if (!original.SequenceEqual(current))
                {
                    differences++;
                    for (var pixel = 0; pixel < original.Length; pixel++)
                    {
                        maximumDifference = Math.Max(maximumDifference, Math.Abs(original[pixel] - current[pixel]));
                    }
                }
            }
        }
        Assert.InRange(maximumDifference, 0, name.StartsWith("image", StringComparison.Ordinal) ? 1 : 0);
        return new
        {
            Name = name,
            BeforeMedianMs = beforeTimes.Order().ElementAt(24),
            AfterMedianMs = afterTimes.Order().ElementAt(24),
            BeforeP95Ms = beforeTimes.Order().ElementAt(45),
            AfterP95Ms = afterTimes.Order().ElementAt(45),
            DifferentFrames = differences,
            MaxChannelDifference = maximumDifference,
            BeforeBytes = beforeAllocations.Average(),
            AfterBytes = afterAllocations.Average(),
            Before = beforeTimes,
            After = afterTimes
        };

        (double, long) Draw(Action<int> action, int frame, byte[] pixels)
        {
            canvas.Clear(new SKColor(24, 28, 36));
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            action(frame);
            canvas.Flush();
            var duration = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            bitmap.GetPixelSpan().CopyTo(pixels);
            return (duration, bytes);
        }
    }
}

public sealed class PerformanceFactAttribute : FactAttribute
{
    public PerformanceFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VISIONCANVAS_SKIA_PERF")))
        {
            Skip = "VISIONCANVAS_SKIA_PERF에 결과 이름을 지정하면 같은 조건의 CPU 렌더를 측정합니다.";
        }
    }
}
