using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VisionCanvas.Core.Interaction;
using VisionCanvas.Layers;
using VisionGeometry;

namespace VisionCanvas.Sample;

internal static class SampleLayerSmoke
{
    internal static async Task RunAsync(SampleWindow window, string directory)
    {
        var before = window.Session.State;
        var undo = window.Session.UndoName;
        var layer = window.ResultLayer;
        var inspection = window.Viewport.Layers["Inspection"];
        var previousCount = inspection.Count;
        var extraId = window.AddInspectionGraphic();
        window.Viewport.Submit(window.CreateScene());
        if (inspection.Count != previousCount + 1 || !inspection.Remove(extraId) || inspection.Count != previousCount)
        {
            throw new InvalidOperationException("기존 검사 레이어 추가/장면 재제출/개별 삭제 계약 실패");
        }
        var id = layer.Add(new ShapeGraphic(new(new(4096, 4096), 1800, 900, 25), new(1, .2f, .3f, 1), 2));
        layer.Add(new TextGraphic(new(3200, 3400), "사용자 레이어", Vector4.One));
        window.GuideLayer.IsVisible = true;
        await CaptureAsync(window, directory, "layers-added", true);
        if (!layer.Remove(id) || layer.Remove(id))
        {
            throw new InvalidOperationException("ID 삭제 계약 실패");
        }
        layer.Clear();
        window.Viewport.Layers["Inspection"].Clear();
        await CaptureAsync(window, directory, "layers-cleared-guide-retained", true);
        if (!ReferenceEquals(before, window.Session.State) || undo != window.Session.UndoName)
        {
            throw new InvalidOperationException("레이어 변경이 ROI/이력을 변경했습니다.");
        }
        window.Session.SetViewport(before.Viewport.ZoomAt(new(200, 200), 1.4).Pan(new(30, 20)));
        window.Width += 120;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await CaptureAsync(window, directory, "guide-after-navigation-resize", true);
        if (window.Viewport.Layers["Inspection"].Count != 0)
        {
            throw new InvalidOperationException("탐색이 지운 검사 결과를 복원했습니다.");
        }
        await window.Viewport.RestartAsync();
        await CaptureAsync(window, directory, "guide-after-restart", true);
        window.GuideLayer.IsVisible = false;
        await CaptureAsync(window, directory, "guide-hidden", false);
        window.Session.SetViewport(before.Viewport);
        window.Input.Tool = EditorTool.Draw;
        window.Input.DrawParentId = null;
        window.Input.DrawShape = RoiShapeKind.Rectangle;
        var center = new PointD(window.Viewport.ViewWidth / 2, window.Viewport.ViewHeight / 2);
        window.Input.Down(center - new PointD(100, 60));
        window.Input.Move(center + new PointD(100, 60));
        await CaptureAsync(window, directory, "drag-area", false);
        window.Input.Cancel();
        if (window.Input.Overlay.DragArea is not null)
        {
            throw new InvalidOperationException("취소 후 드래그 영역이 남았습니다.");
        }
        await MeasureFillAsync(window, directory);
        window.Input.Tool = EditorTool.Select;
        window.Session.SetViewport(before.Viewport);
    }

    private static async Task CaptureAsync(SampleWindow window, string directory, string name, bool guideVisible)
    {
        var path = Path.Combine(directory, name + ".png");
        window.Viewport.Capture(path);
        var timer = Stopwatch.StartNew();
        while (true)
        {
            await Task.Delay(50);
            try
            {
                using var stream = File.OpenRead(path);
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                var bitmap = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
                var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                var source = window.Session.State.Image ?? throw new InvalidOperationException("이미지 없음");
                var center = window.Session.State.Viewport.ImageToScreen(new(source.Width / 2d, source.Height / 2d));
                var x = (int)Math.Round((center.X + 12) * window.Viewport.Dpi);
                var y = (int)Math.Round(center.Y * window.Viewport.Dpi);
                var found = false;
                for (var dy = -2; dy <= 2; dy++)
                {
                    var offset = (((y + dy) * bitmap.PixelWidth) + x) * 4;
                    found |= pixels[offset] < 100 && pixels[offset + 1] > 150 && pixels[offset + 2] > 220;
                }
                if (found != guideVisible)
                {
                    throw new InvalidOperationException($"{name}: 중앙선 픽셀/숨김 검증 실패");
                }
                return;
            }
            catch (IOException) when (timer.Elapsed < TimeSpan.FromSeconds(30))
            {
                // 렌더 스레드의 비동기 파일 저장만 기다린다. 판정 실패는 재시도하지 않는다.
            }
        }
    }

    private static async Task MeasureFillAsync(SampleWindow window, string directory)
    {
        var phases = new List<object>();
        var scene = window.CreateScene();
        for (var phase = 0; phase < 4; phase++)
        {
            var fill = phase % 2 != 0;
            long start = 0;
            for (var frame = 0; frame < 140; frame++)
            {
                if (frame == 20)
                {
                    start = Stopwatch.GetTimestamp();
                }
                var area = new BoundsD(30, 30, scene.Width - 30 + (frame % 4), scene.Height - 30);
                // 같은 장면·같은 테두리에서 내부 채우기 유무만 교대로 바꾼다.
                var first = scene.Document.Viewport.ScreenToImage(new(area.Left, area.Top));
                var last = scene.Document.Viewport.ScreenToImage(new(area.Right, area.Bottom));
                var draft = new RoiGeometry((first + last) / 2, last.X - first.X, last.Y - first.Y, 0);
                window.Viewport.Submit(scene with
                {
                    Overlay = new(Draft: draft)
                    {
                        DragArea = fill ? area : null
                    }
                });
                await Task.Delay(16);
            }
            phases.Add(new
            {
                Fill = fill,
                StartTicks = start,
                EndTicks = Stopwatch.GetTimestamp(),
                Stopwatch.Frequency,
                Inputs = 120
            });
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "fill-phases.json"), JsonSerializer.Serialize(phases, new JsonSerializerOptions { WriteIndented = true }));
    }
}
