using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using TeachingToolWpf.Commands;
using TeachingToolWpf.Controls;
using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Models;
using TeachingToolWpf.ViewModels;
using VisionCanvas.Controls;
using VisionGeometry;

namespace TeachingToolWpf.Diagnostics;

internal static partial class InspectionSmoke
{
    private static async Task VerifyStartupAsync(Window window, Dx12Viewport viewport, MainWindowViewModel model, Func<string, Task> capture)
    {
        await model.InitializeAsync();
        Require(model.Session.State.Image!.Width == 8192, "default startup image");
        var matching = model.Matching;
        model.Session.Add(new(new(1024, 1024), 512, 512, 0));
        await Invoke(matching.Train);
        Require(!matching.HasModel && matching.Verdict == InspectionVerdict.Warning && matching.Status.Contains("테두리·핀"), "flat chip interior gives actionable training failure");
        Require(!matching.Search.CanExecute(null) && matching.Overlay!.Summary.Contains("학습 실패"), "failed training cannot run empty model");
        Descendants(window).OfType<TextBlock>().First(text => text.Text == matching.Status).BringIntoView();
        await capture("chip-training-warning");
        WindowCapture.Save(window, viewport, "artifacts/chip-training-warning.png", "artifacts/chip-training-warning-app.png");
        await model.Undo.ExecuteAsync();

        model.DrawTool.Execute(null);
        Pointer(0x201, new(224, 464));
        Pointer(0x200, new(1824, 1584));
        Pointer(0x202, new(1824, 1584));
        var roi = model.Session.State.Rois.Values.Single().Geometry;
        Log($"Native ROI: center {roi.Center.X:R}, {roi.Center.Y:R}; size {roi.Width:R} × {roi.Height:R}");
        await Invoke(matching.Train);
        Require(matching.HasModel, "native chip ROI training: " + matching.Status);
        var historyBytes = model.Session.HistoryBytes;
        var timer = Stopwatch.StartNew();
        var search = Invoke(matching.Search);
        Require(matching.IsBusy && matching.Markers.IsEmpty && matching.Overlay!.Summary.Contains("검사 중"), "immediate search activity banner");
        await capture("chip-search-running");
        await search;
        Require(matching.Markers.Length == 16, "startup chip detections: " + matching.Status);
        for (var row = 0; row < 4; row++)
        {
            for (var column = 0; column < 4; column++)
            {
                var expected = roi.Center + new PointD(column * 2048, row * 2048);
                Require(matching.Markers.Any(marker => (marker.ReferencePoint - expected).Length < 1), "each repeated chip source position");
            }
        }
        Require(model.Session.HistoryBytes == historyBytes, "inspection preserves ROI history");
        Log($"Startup chip: {timer.Elapsed.TotalMilliseconds:F1} ms UI command; " + matching.Status);
        await capture("chip-search-complete");
        Require(PixelCounts("artifacts/chip-search-complete.png", matching.Overlay!.Color) > 500, "16 chip GPU location outlines visible");
        WindowCapture.Save(window, viewport, "artifacts/chip-search-complete.png", "artifacts/chip-search-app.png");

        await VerifyOpenCvPinLimitAsync(window, viewport, model, capture, Invoke);
        await VerifyPaperPinLimitAsync(window, viewport, model, capture, Invoke);

        var previous = model.Session.State;
        var learned = matching.Model;
        await Invoke(model.PatternSample);
        Require(model.Session.State.Image is { Width: 640, Height: 400 } && model.Session.State.Rois.Count == 0, "pattern sample button loads bundled image and clears ROIs");
        Require(ReferenceEquals(learned, matching.Model) && matching.Markers.IsEmpty, "sample replacement preserves learned model and clears stale matches");
        await model.Undo.ExecuteAsync();
        Require(model.Session.State.Image == previous.Image && model.Session.State.Rois.Count == 1, "pattern sample undo restores startup image and ROI");
        await model.Redo.ExecuteAsync();
        Require(model.Session.State.Image!.Width == 640 && model.Session.State.Rois.Count == 0, "pattern sample redo");
        Log("PASS: startup flat-ROI warning, native chip ROI, 16 source positions, busy/result GPU overlays, pattern sample button, image Undo/Redo");

        void Pointer(int message, PointD point) => EditingSmokeScenario.Pointer(viewport, message, model.Session.State.Viewport.ImageToScreen(point));

        async Task Invoke(AsyncCommand command)
        {
            var button = Descendants(window).OfType<Button>().Single(item => ReferenceEquals(item.Command, command));
            Require(button.IsEnabled, "startup button enabled");
            await ((AsyncCommand)button.Command).ExecuteAsync();
        }
    }
}
