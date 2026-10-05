using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Interaction;
using VisionCanvas.Rendering;
using VisionGeometry;
using VisionImaging;
using VisionImaging.Abstractions;

namespace VisionCanvas.Sample;

internal static class SampleSmoke
{
    internal static async Task RunAsync(SampleWindow window, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var report = new Dictionary<string, object?>();
        using var log = new StreamWriter(Path.Combine(outputDirectory, "smoke.log")) { AutoFlush = true };
        var exitCode = 1;
        try
        {
            report["Renderer"] = await window.RendererReady.WaitAsync(TimeSpan.FromSeconds(30));
            log.WriteLine(report["Renderer"]);

            await window.LoadImageAsync(SampleWindow.SampleReference(8192, 8192));
            var source8K = window.Source!;
            CheckEditing(window);
            await PresentAndCaptureAsync(window, outputDirectory, "8k", "8K · 독립 ROI 편집");
            Assert(source8K.ReadCount > 0, "8K 타일 읽기");
            report["Image8K"] = TileReport(source8K);
            log.WriteLine("PASS 8192×8192 · 사각형/원/폴리곤 · 계층 회전/삭제/Undo/Redo · 취소 · overlay fence · 캡처");

            var previous = window.Session.State;
            await window.LoadImageAsync(SampleWindow.SampleReference(24576, 8192));
            var source24K = window.Source!;
            Assert(window.Session.State.Rois.Count == 0, "이미지 교체 ROI 초기화");
            await PresentAndCaptureAsync(window, outputDirectory, "24k", "24K 폭 · 원본 전체 할당 없는 타일 표시");
            Assert(source24K.ReadCount > 0 && source24K.MaximumBufferBytes <= 512 * 512 * 4, "24K 최대 대여 타일 배열 1 MiB");
            await WaitUntilAsync(() => source8K.IsDisposed, "교체된 8K 원본 해제");
            report["Image24K"] = TileReport(source24K);
            log.WriteLine("PASS 24576×8192 · 타일 최대 대여 배열 1 MiB 이하 · 이전 reader 해제 · overlay fence · 캡처");

            await window.HistoryAsync(false);
            Assert(window.Session.State.Image == previous.Image && SameRois(previous, window.Session.State), "이미지 교체 Undo");
            await window.HistoryAsync(true);
            Assert(window.Session.State.Image?.Width == 24576 && window.Session.State.Rois.Count == 0, "이미지 교체 Redo");
            log.WriteLine("PASS 이미지/ROI 통합 Undo/Redo");

            var file = Path.Combine(outputDirectory, "file-input.png");
            WriteFileFixture(file);
            await window.LoadImageAsync(new(file, "파일 샘플", 0, 0, DiskPyramidSource.Version(file)));
            Assert(window.Session.State.Image is { Width: 320, Height: 240 }, "파일 원본 크기");
            await PresentAndCaptureAsync(window, outputDirectory, "file", "파일 원본 · ImageLoader 공개 API");
            log.WriteLine("PASS NetVips 파일 읽기 · 원본 크기 · overlay fence · 캡처");
            var beforeFailure = window.Session.State;
            var undoBeforeFailure = window.Session.UndoName;
            try
            {
                await window.LoadImageAsync(new(Path.Combine(outputDirectory, $"missing-{Guid.NewGuid():N}.png"), "없는 파일", 0, 0));
                throw new InvalidOperationException("없는 파일 열기가 성공했습니다.");
            }
            catch (FileNotFoundException)
            {
                Assert(window.Session.State == beforeFailure && window.Session.UndoName == undoBeforeFailure, "파일 열기 실패 시 문서/이력 유지");
            }
            log.WriteLine("PASS 파일 열기 실패 시 문서/이력 유지");

            var loaded = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetName().Name).Order().ToArray();
            string[] forbidden = ["TeachingToolWpf", "TeachingToolWpf.Core", "VisionInspection", "VisionAlgorithm", "VisionCompute.ILGPU"];
            Assert(!loaded.Intersect(forbidden).Any(), "앱/검사/알고리즘 어셈블리 없이 실행");
            Assert(window.RendererErrors.Count == 0, string.Join(Environment.NewLine, window.RendererErrors));
            report["LoadedAssemblies"] = loaded;
            using var process = Process.GetCurrentProcess();
            report["PrivateBytesAtEnd"] = process.PrivateMemorySize64;
            report["Status"] = "PASS";
            report["Limitations"] = "합성 타일 소스와 작은 PNG의 기능 smoke. 24K 실파일·카메라·프레임 속도·전체 프로세스 메모리 상한·물리 화면 scan-out은 검증하지 않음.";
            log.WriteLine("PASS 독립 호스트 실행 · 검사 어셈블리 미로드 · 렌더 오류 없음");
            exitCode = 0;
        }
        catch (Exception ex)
        {
            report["Status"] = "FAIL";
            report["Error"] = ex.ToString();
            log.WriteLine(ex);
        }
        finally
        {
            report["ExitCode"] = exitCode;
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Application.Current.Shutdown(exitCode);
        }
    }

    private static void CheckEditing(SampleWindow window)
    {
        var session = window.Session;
        var parent = session.Add(new(new(4096, 4096), 2400, 2400, 0));
        var child = session.AddChild(parent, new(new(3600, 4000), 500, 500, 0, RoiShape.Circle));
        window.Input.DrawParentId = parent;
        window.Input.DrawShape = RoiShapeKind.Polygon;
        window.Input.Tool = EditorTool.Draw;
        PointD[] polygon = [new(4600, 3400), new(5050, 3600), new(4900, 4100), new(4500, 3900)];
        foreach (var point in polygon)
        {
            var screen = session.State.Viewport.ImageToScreen(point);
            window.Input.Down(screen, InputModifiers.None, false);
            window.Input.Up(screen, InputModifiers.None);
        }
        window.Input.CompletePolygon();
        Assert(session.State.Rois.Count == 3 && session.State.Rois.Values.Count(roi => roi.ParentId == parent) == 2, "실제 점 입력 폴리곤과 명시적 부모");
        Assert(window.Input.Tool == EditorTool.Select, "그리기 성공 후 선택 도구");
        session.Select([parent]);
        var before = session.State;
        session.RotateSelection(17, before.Rois[parent].Geometry.Center);
        Assert(session.State.Rois[child].Geometry != before.Rois[child].Geometry, "부모 회전 시 자식 변환");
        var rotated = session.State;
        session.Undo();
        Assert(SameRois(session.State, before), "회전 Undo");
        session.Redo();
        Assert(SameRois(session.State, rotated), "회전 Redo");
        session.BeginPreview();
        session.PreviewTranslation(new(150, 70));
        session.CancelPreview();
        Assert(SameRois(session.State, rotated), "편집 취소");
        session.DeleteSelection();
        Assert(session.State.Rois.Count == 0, "부모 삭제 시 전체 자손 삭제");
        session.Undo();
        Assert(SameRois(session.State, rotated) && session.State.Selection.SetEquals(rotated.Selection), "연쇄 삭제 Undo");
        session.Redo();
        Assert(session.State.Rois.Count == 0, "연쇄 삭제 Redo");
        session.Undo();
        var scale = session.State.Viewport.Scale;
        window.Zoom(1.25);
        Assert(session.State.Viewport.Scale > scale, "확대");
        window.Fit();
    }

    private static bool SameRois(DocumentState first, DocumentState second)
        => first.Rois.Count == second.Rois.Count && first.Rois.All(pair => second.Rois.TryGetValue(pair.Key, out var other) && pair.Value == other);

    private static object TileReport(ObservedTileSource source)
        => new
        {
            source.Info.Width,
            source.Info.Height,
            source.ReadCount,
            source.MaximumBufferBytes
        };

    private static async Task PresentAndCaptureAsync(SampleWindow window, string directory, string name, string label)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Guid expected = default;
        void Presented(OverlayPresentation presentation)
        {
            if (presentation.PresentationId == expected)
            {
                completion.TrySetResult();
            }
        }
        window.Viewport.OverlayPresented += Presented;
        try
        {
            expected = window.ShowOverlay(label).PresentationId;
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var capture = Path.Combine(directory, $"{name}-{expected:N}.png");
            window.Viewport.Capture(capture);
            await WaitUntilAsync(() => CaptureIsReadable(capture), $"{name} 캡처 완료");
        }
        finally
        {
            window.Viewport.OverlayPresented -= Presented;
        }
    }

    private static bool CaptureIsReadable(string path)
    {
        try
        {
            using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = BitmapDecoder.Create(file, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            return decoder.Frames[0].PixelWidth > 0 && decoder.Frames[0].PixelHeight > 0;
        }
        catch (IOException)
        {
            // 비동기 캡처가 아직 생성/쓰기 중이다. 제한 시간 안에서만 재시도한다.
            return false;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, string name)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(30))
            {
                throw new TimeoutException(name);
            }
            await Task.Delay(50);
        }
    }

    private static void WriteFileFixture(string path)
    {
        var pixels = new byte[320 * 240];
        for (var y = 0; y < 240; y++)
        {
            for (var x = 0; x < 320; x++)
            {
                pixels[(y * 320) + x] = (byte)((x + y) % 256);
            }
        }
        var bitmap = BitmapSource.Create(320, 240, 96, 96, PixelFormats.Gray8, null, pixels, 320);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException("검증 실패: " + name);
        }
    }
}
