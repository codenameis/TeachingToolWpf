using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TeachingToolWpf.Controls;
using TeachingToolWpf.Core.Geometry;
using TeachingToolWpf.Presentation;

namespace TeachingToolWpf.Diagnostics;

internal static partial class InspectionSmoke
{
    private static async Task VerifyBoundariesAsync(Window window, Dx12Viewport viewport, EditorViewModel model,
        Func<string, Task> capture, Func<AsyncCommand, Task> invoke)
    {
        await model.PatternSample.ExecuteAsync();
        var teaching = RoiGeometry.Polygon([new(110, 110), new(210, 110), new(210, 170), new(198, 170), new(198, 210), new(110, 210)]);
        model.Session.Add(teaching);
        var matching = model.Matching;
        matching.ShowFeatures = false;
        await invoke(matching.Train);
        Require(matching.Model is not null, "concave polygon file training: " + matching.Status);
        var search = RoiGeometry.Polygon([new(330, 145), new(540, 145), new(540, 310), new(480, 310), new(480, 335), new(330, 335)]);
        model.Session.Add(search);
        matching.UseSelection = true;
        await invoke(matching.Search);
        Log("Polygon boundary search: " + matching.Status + Environment.NewLine + matching.Results);
        Require(matching.Markers.Length == 1, "concave selected search finds only the rotated sample");
        var marker = matching.Markers[0];
        Require((marker.ReferencePoint - new PointD(420, 230)).Length < 1, "polygon reference point in source coordinates");
        Require(marker.Bounds.Length == teaching.Outline.Length, "match preserves six teaching vertices instead of a four-point AABB");
        Require(matching.Overlay!.SearchBoundary.SequenceEqual(search.Outline), "search preserves selected polygon vertices");
        for (var index = 0; index < teaching.Outline.Length; index++)
        {
            var expected = new PointD(420, 230) + ((teaching.Outline[index] - teaching.Center).Rotate(20) * 1.2);
            Require((marker.Bounds[index] - expected).Length < 1.5, "rotated/scaled polygon vertex " + index);
        }

        model.Fit.Execute(null);
        matching.ShowOverlay = false;
        await capture("inspection-polygon-overlay-off");
        matching.ShowOverlay = true;
        await capture("inspection-polygon-boundaries");
        WindowCapture.Save(window, viewport, "artifacts/inspection-polygon-boundaries.png", "artifacts/inspection-polygon-boundaries-app.png");
        var before = BoundaryPixels.Read("artifacts/inspection-polygon-overlay-off.png");
        var after = BoundaryPixels.Read("artifacts/inspection-polygon-boundaries.png");
        var view = model.Session.State.Viewport;
        for (var index = 0; index < marker.Bounds.Length; index++)
        {
            var start = marker.Bounds[index];
            var end = marker.Bounds[(index + 1) % marker.Bounds.Length];
            foreach (var fraction in new[] { .35, .65 })
            {
                var pixel = view.ImageToScreen(start + ((end - start) * fraction)) * viewport.Dpi;
                Require(after.HasColorNear(pixel, marker.Color, (int)Math.Ceiling(2 * viewport.Dpi)), "GPU solid polygon edge including closing edge: " + index);
            }
        }

        // 편집 ROI의 실선이 아래에 있어도 결과 검색선의 대시 사이에는 원래 픽셀이 남아야 한다.
        var dashStart = view.ImageToScreen(new(540, 185));
        var dashEnd = view.ImageToScreen(new(540, 280));
        var length = (dashEnd - dashStart).Length;
        var changed = 0;
        var unchanged = 0;
        var transitions = 0;
        bool? previous = null;
        for (var distance = 0; distance < length; distance++)
        {
            var pixel = (dashStart + ((dashEnd - dashStart) * (distance / length))) * viewport.Dpi;
            var differs = after.DiffersNear(before, pixel);
            changed += differs ? 1 : 0;
            unchanged += differs ? 0 : 1;
            transitions += previous.HasValue && previous != differs ? 1 : 0;
            previous = differs;
        }
        Require(changed > length * .25 && unchanged > length * .1 && transitions >= 6,
            $"GPU search dash/gap sequence: dash={changed}, gap={unchanged}, transitions={transitions}");
        Log($"PASS: real polygon training/search, six transformed vertices, all closed GPU edges, dashed search ({changed} dash/{unchanged} gap/{transitions} transitions)");
    }

    private sealed record BoundaryPixels(int Width, int Height, byte[] Pixels)
    {
        public static BoundaryPixels Read(string path)
        {
            using var stream = File.OpenRead(path);
            var bitmap = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
            converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
            return new(converted.PixelWidth, converted.PixelHeight, pixels);
        }

        public bool HasColorNear(PointD point, Vector4 color, int radius)
            => Nearby(point, radius).Any(index => Math.Abs(Pixels[index] - (color.Z * 255)) < 2 &&
                Math.Abs(Pixels[index + 1] - (color.Y * 255)) < 2 && Math.Abs(Pixels[index + 2] - (color.X * 255)) < 2);

        public bool DiffersNear(BoundaryPixels other, PointD point)
        {
            Require(Width == other.Width && Height == other.Height, "stable capture size");
            return Nearby(point, 1).Any(index => Math.Abs(Pixels[index] - other.Pixels[index]) > 20 ||
                Math.Abs(Pixels[index + 1] - other.Pixels[index + 1]) > 20 || Math.Abs(Pixels[index + 2] - other.Pixels[index + 2]) > 20);
        }

        private IEnumerable<int> Nearby(PointD point, int radius)
        {
            var centerX = (int)Math.Round(point.X);
            var centerY = (int)Math.Round(point.Y);
            for (var y = Math.Max(0, centerY - radius); y <= Math.Min(Height - 1, centerY + radius); y++)
            {
                for (var x = Math.Max(0, centerX - radius); x <= Math.Min(Width - 1, centerX + radius); x++)
                {
                    yield return ((y * Width) + x) * 4;
                }
            }
        }
    }
}
