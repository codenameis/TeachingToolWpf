using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;
using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Models;
using TeachingToolWpf.ViewModels;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionGeometry;
using VisionImaging;
using VisionImaging.Abstractions;
using VisionOcr.PpOcr;
using Xunit;
using Xunit.Abstractions;

namespace TeachingToolWpf.Fluent2.Tests.OcrBenchmark;

public sealed class PpOcrLivePreviewStabilityTests(ITestOutputHelper output)
{
    private const double PositionTolerancePx = 1e-3;
    private const int LiveUpdatePollIntervalMs = 10;
    private const int LiveUpdateTimeoutMs = 4000;

    [Fact]
    public async Task DragAndResizeRoi_WithPpOcrOnnx_EliminatesGuideBlinking_Tracks60FpsPose_AndUpdatesRealtime()
    {
        (_, string repoRootPath, _) = OcrBenchmarkDataset.LoadFromDefaultLocation();
        string modelDir = Path.Combine(repoRootPath, "models", "ppocr");
        string tempDir = Path.Combine(Path.GetTempPath(), $"TeachingToolWpf_PpOcrLive_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        SynchronizationContext? previousContext = SynchronizationContext.Current;
        var immediateContext = new ImmediateSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(immediateContext);

        try
        {
            string imgPath = Path.Combine(tempDir, "ppocr_live_drag.png");
            CreateTwoStripSyntheticImage(imgPath, out int imgW, out int imgH);

            IImageLoader loader = ImageLoaderHost.Create(Path.Combine(tempDir, "cache"));
            using var ppOcrService = new PpOcrInferenceService(PpOcrModelOptions.FromDirectory(modelDir, PpOcrExecutionDevice.Cpu));
            var suiteService = new InspectionSuiteService(loader, () => null, ppOcrService);
            using var suiteVm = new InspectionSuiteViewModel(suiteService)
            {
                SelectedCategory = InspectionSuiteCategory.Ocr,
                SelectedAlgorithm = InspectionSuiteAlgorithmKind.PpOcrOnnx
            };

            var session = new EditorSession();
            var imageRef = new ImageReference(imgPath, "ppocr_live_drag.png", imgW, imgH);
            session.InitializeImage(imageRef, new ViewportTransform(1.0, default));

            // Place ROI on Top Strip ("ALPHA2026" around y=80)
            Guid roiId = session.Add(new RoiGeometry(new PointD(240, 80), 310, 52, 0.0));
            session.Select([roiId]);

            suiteVm.Update(new InspectionUpdate(session.State, Generation: 1, Live: false, CanExecute: true, ToolAvailable: true, TransformPreview: null));
            await suiteVm.ExecuteSelected.ExecuteAsync();

            Assert.NotNull(suiteVm.Overlay);
            Assert.Contains("ALPHA2026", suiteVm.OcrLiveRecognizedText, StringComparison.Ordinal);
            Assert.DoesNotContain("[가이드 미리보기]", suiteVm.Overlay!.Summary, StringComparison.Ordinal);
            Assert.Single(suiteVm.OcrLiveRoiItems);

            int resetCollectionEvents = 0;
            suiteVm.OcrLiveRoiItems.CollectionChanged += (_, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Reset)
                {
                    Interlocked.Increment(ref resetCollectionEvents);
                }
            };

            int guideBlinkEvents = 0;
            suiteVm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(InspectionSuiteViewModel.Overlay) &&
                    suiteVm.Overlay is { } ov &&
                    ov.Summary.Contains("[가이드 미리보기]", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref guideBlinkEvents);
                }
            };

            // 1) Simulate 30 frames of continuous 60FPS pointer drag from y=80 down to y=220 ("BETA7788")
            session.BeginPreview();
            var syncSw = Stopwatch.StartNew();
            const int dragSteps = 30;
            for (int step = 1; step <= dragSteps; step++)
            {
                double dy = (140.0 * step) / dragSteps;
                session.PreviewTranslation(new PointD(0.0, dy));

                suiteVm.UpdateForTest(new InspectionUpdate(
                    session.State,
                    Generation: 1,
                    Live: false,
                    CanExecute: false,
                    ToolAvailable: true,
                    TransformPreview: session.TransformPreview), triggerAsyncMeasurement: true);

                Assert.NotNull(suiteVm.Overlay);
                // Must NEVER strobe back to cyan "[가이드 미리보기]" during drag!
                Assert.DoesNotContain("[가이드 미리보기]", suiteVm.Overlay!.Summary, StringComparison.Ordinal);

                // Marker ReferencePoint must track the dragged ROI center synchronously in 0 ms!
                PointD expectedCenter = new(240.0, 80.0 + dy);
                PointD actualCenter = suiteVm.Overlay.Matches[0].ReferencePoint;
                Assert.InRange(Math.Abs(actualCenter.X - expectedCenter.X), 0.0, PositionTolerancePx);
                Assert.InRange(Math.Abs(actualCenter.Y - expectedCenter.Y), 0.0, PositionTolerancePx);
            }
            syncSw.Stop();

            double avgSyncTickMs = syncSw.Elapsed.TotalMilliseconds / dragSteps;
            output.WriteLine($"30-frame continuous ROI drag UI update avg={avgSyncTickMs:F3} ms/tick (total={syncSw.Elapsed.TotalMilliseconds:F2} ms)");
            Assert.Equal(0, guideBlinkEvents);
            Assert.Equal(0, resetCollectionEvents);

            // 2) While STILL holding mouse drag (before CommitPreview!), real-time live preview must recognize "BETA7788"
            await WaitUntilConditionAsync(
                () => suiteVm.OcrLiveRecognizedText.Contains("BETA7788", StringComparison.Ordinal),
                LiveUpdateTimeoutMs,
                () => $"Current OcrLiveRecognizedText='{suiteVm.OcrLiveRecognizedText}', Status='{suiteVm.Status}', SyncCtx='{SynchronizationContext.Current?.GetType().Name ?? "null"}'");

            Assert.Contains("BETA7788", suiteVm.OcrLiveRecognizedText, StringComparison.Ordinal);
            Assert.Equal(0, guideBlinkEvents);
            Assert.Equal(0, resetCollectionEvents);

            // 3) Commit drag (MouseUp) -> full-precision settle pass runs without blinking
            session.CommitPreview("ROI 이동");
            suiteVm.UpdateForTest(new InspectionUpdate(
                session.State,
                Generation: 1,
                Live: false,
                CanExecute: true,
                ToolAvailable: true,
                TransformPreview: null), triggerAsyncMeasurement: true);

            // 4) Simulate handle resize (PreviewResize mutates State.Rois while CanExecute=false and TransformPreview=null)
            session.BeginPreview();
            for (int rStep = 1; rStep <= 10; rStep++)
            {
                session.PreviewResize(roiId, new RoiGeometry(new PointD(240.0, 220.0), 310.0 + (rStep * 3.0), 52.0 + rStep, 0.0));
                suiteVm.UpdateForTest(new InspectionUpdate(
                    session.State,
                    Generation: 1,
                    Live: false,
                    CanExecute: false,
                    ToolAvailable: true,
                    TransformPreview: session.TransformPreview), triggerAsyncMeasurement: true);

                Assert.NotNull(suiteVm.Overlay);
                Assert.DoesNotContain("[가이드 미리보기]", suiteVm.Overlay!.Summary, StringComparison.Ordinal);
            }
            session.CommitPreview("ROI 크기 조절");
            suiteVm.UpdateForTest(new InspectionUpdate(
                session.State,
                Generation: 1,
                Live: false,
                CanExecute: true,
                ToolAvailable: true,
                TransformPreview: null), triggerAsyncMeasurement: true);

            await WaitUntilConditionAsync(
                () => suiteVm.OcrLiveRecognizedText.Contains("BETA7788", StringComparison.Ordinal),
                LiveUpdateTimeoutMs);

            Assert.Equal(0, guideBlinkEvents);
            Assert.Equal(0, resetCollectionEvents);
            Assert.Single(suiteVm.OcrLiveRoiItems);
            Assert.Contains("BETA7788", suiteVm.OcrLiveRoiItems[0].DecodedText, StringComparison.Ordinal);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch
            {
                // Ignore transient file handle release delay.
            }
        }
    }

    [Fact]
    public void FastInteractivePreviewOption_RunsFasterOrEqualSinglePass_WhilePreservingRecognition()
    {
        (_, string repoRootPath, _) = OcrBenchmarkDataset.LoadFromDefaultLocation();
        string modelDir = Path.Combine(repoRootPath, "models", "ppocr");
        using var service = new PpOcrInferenceService(PpOcrModelOptions.FromDirectory(modelDir, PpOcrExecutionDevice.Cpu));

        using var mat = new Mat(64, 340, MatType.CV_8UC1, Scalar.All(242));
        Cv2.PutText(mat, "REALTIME99", new Point(18, 46), HersheyFonts.HersheySimplex, 1.15, Scalar.All(18), thickness: 2, LineTypes.AntiAlias);

        int matWidth = mat.Width;
        int matHeight = mat.Height;
        byte[] raw = new byte[matWidth * matHeight];
        for (int y = 0; y < matHeight; y++)
        {
            for (int x = 0; x < matWidth; x++)
            {
                raw[(y * matWidth) + x] = mat.At<byte>(y, x);
            }
        }

        var roiView = new VisionAlgorithm.OpenSrc.Common.ReadOnlyImageView2D<byte>(raw, matWidth, matHeight, matWidth);

        // Warmup
        _ = service.RecognizeRoi(roiView, new PpOcrRunOptions(FastInteractivePreview: true));

        var sw = Stopwatch.StartNew();
        PpOcrRecognitionResult fastRes = service.RecognizeRoi(roiView, new PpOcrRunOptions(FastInteractivePreview: true));
        sw.Stop();

        output.WriteLine($"FastInteractivePreview latency={sw.Elapsed.TotalMilliseconds:F2} ms, text='{fastRes.Region.FullText}', conf={fastRes.Region.MeanConfidence:F3}");
        Assert.Equal("REALTIME99", fastRes.Region.FullText);
        Assert.Equal(3, service.SessionCreationCount);
    }

    private static void CreateTwoStripSyntheticImage(string outputPath, out int width, out int height)
    {
        width = 480;
        height = 320;
        using var mat = new Mat(height, width, MatType.CV_8UC1, Scalar.All(240));
        Cv2.PutText(mat, "ALPHA2026", new Point(108, 92), HersheyFonts.HersheySimplex, 1.15, Scalar.All(18), thickness: 2, LineTypes.AntiAlias);
        Cv2.PutText(mat, "BETA7788", new Point(116, 232), HersheyFonts.HersheySimplex, 1.15, Scalar.All(18), thickness: 2, LineTypes.AntiAlias);
        Cv2.ImWrite(outputPath, mat);
    }

    private static async Task WaitUntilConditionAsync(Func<bool> predicate, int timeoutMs, Func<string>? diagnosticMessage = null)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(LiveUpdatePollIntervalMs);
        }

        string extra = diagnosticMessage is not null ? $" ({diagnosticMessage()})" : string.Empty;
        Assert.True(predicate(), $"Condition was not satisfied within {timeoutMs} ms{extra}.");
    }

    private sealed class ImmediateSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);

        public override void Send(SendOrPostCallback d, object? state) => d(state);
    }
}
