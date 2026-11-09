using TeachingToolWpf.ViewModels;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Interaction;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Tests;

public sealed class RoiMaskViewModelTests
{
    [Fact]
    public void ApplyingDisplayedValuesPreservesExactMaskAndOnlyChangesEditedAxis()
    {
        var session = new EditorSession();
        var input = new InteractionController(session);
        var model = new RoiMaskViewModel(session, input, _ => { });
        var id = session.Add(new(new(200, 200), 100, 100, 0));
        var mask = new MaskRegion(MaskShape.Rectangle, new(.123456789, .234567891, .678912345, .789123456));
        session.AddMask(id, mask);
        model.Update(true);
        var bytes = session.HistoryBytes;
        model.Apply.Execute(null);
        Assert.Equal(mask, session.State.Rois[id].Masks[0]);
        Assert.Equal(bytes, session.HistoryBytes);

        model.X = "20";
        model.Apply.Execute(null);
        var bounds = session.State.Rois[id].Masks[0].Bounds;
        Assert.Equal(.2, bounds.Left);
        Assert.Equal(mask.Bounds.Width, bounds.Width, 14);
        Assert.Equal(mask.Bounds.Top, bounds.Top);
        Assert.Equal(mask.Bounds.Bottom, bounds.Bottom);
    }

    [Fact]
    public void CommandsDraftValidationAndVisibilityKeepSeparateContracts()
    {
        var session = new EditorSession();
        var input = new InteractionController(session);
        var model = new RoiMaskViewModel(session, input, _ => { });
        model.Update(true);
        Assert.False(model.DrawRectangle.CanExecute(null));
        var id = session.Add(new(new(200, 200), 100, 100, 0));
        model.Update(true);
        model.DrawEllipse.Execute(null);
        Assert.Equal(EditorTool.Mask, input.Tool);
        session.AddMask(id, new(MaskShape.Ellipse, new(.1, .2, .6, .7)));
        model.Update(true);
        var bytes = session.HistoryBytes;
        model.ShowMasks = false;
        model.Update(true);
        Assert.False(model.ShowMasks);
        Assert.Equal(bytes, session.HistoryBytes);
        model.X = "NaN";
        model.Apply.Execute(null);
        Assert.NotEmpty(model.Error);
        Assert.Equal(bytes, session.HistoryBytes);
        model.X = "20";
        model.Apply.Execute(null);
        Assert.Empty(model.Error);
        Assert.Equal(.2, session.State.Rois[id].Masks[0].Bounds.Left);
        session.Undo();
        Assert.Equal(.1, session.State.Rois[id].Masks[0].Bounds.Left);
        model.Update(false);
        Assert.False(model.Apply.CanExecute(null));
    }

    [Fact]
    public void PatternMaskEditorViewModel_InteractiveDrawEraseAndCommit_UpdatesSessionMasksAndExcludesEdgeFeatures()
    {
        var session = new EditorSession();
        var roiGeometry = new RoiGeometry(new PointD(64, 64), 48, 32, 0);
        var id = session.Add(roiGeometry);

        var editorVm = new PatternMaskEditorViewModel(session, new NullImageLoader(), () => null);

        var patch = new byte[48 * 32];
        for (var y = 0; y < 32; y++)
        {
            for (var x = 0; x < 48; x++)
            {
                patch[(y * 48) + x] = (byte)(x >= 10 && x <= 38 && y >= 6 && y <= 26 ? 220 : 30);
            }
        }

        editorVm.LoadFromMemoryPatch(id, "패턴ROI", roiGeometry, patch, 48, 32, []);
        Assert.True(editorVm.HasTargetRoi);
        var initialActiveFeatures = editorVm.ActiveFeatureCount;
        Assert.True(initialActiveFeatures > 0);
        Assert.Equal(0, editorVm.ExcludedFeatureCount);

        // Add rectangle mask covering left edge (x: 6..16, y: 4..28)
        Assert.True(editorVm.TryAddMaskFromLocalPixels(MaskShape.Rectangle, 6, 4, 16, 28));
        Assert.Equal(1, editorVm.MaskCount);
        Assert.True(editorVm.ExcludedFeatureCount > 0);
        Assert.True(editorVm.ActiveFeatureCount < initialActiveFeatures);

        // Stamp brush mask on right edge (x: 38, y: 16)
        editorVm.BrushSizePixels = 10;
        Assert.True(editorVm.TryStampBrushAtLocalPixel(38, 16));
        Assert.Equal(2, editorVm.MaskCount);

        // Commit to EditorSession and verify undo/redo contract
        Assert.True(editorVm.CommitMasksToSession());
        Assert.Equal(2, session.State.Rois[id].Masks.Length);

        // Erase the brush stamp at (38, 16) and commit again
        Assert.True(editorVm.TryEraseMasksAtLocalPixel(38, 16));
        Assert.Equal(1, editorVm.MaskCount);
        Assert.True(editorVm.CommitMasksToSession());
        Assert.Single(session.State.Rois[id].Masks);
    }

    [Fact]
    public void PatternMaskEditorViewModel_FreehandBrushStrokeSegment_InterpolatesContinuousMaskCoverageAndExcludesEdges()
    {
        var session = new EditorSession();
        var roiGeometry = new RoiGeometry(new PointD(100, 100), 80, 60, 0);
        var id = session.Add(roiGeometry);
        var editorVm = new PatternMaskEditorViewModel(session, new NullImageLoader(), () => null);

        var patch = new byte[80 * 60];
        for (var y = 0; y < 60; y++)
        {
            for (var x = 0; x < 80; x++)
            {
                patch[(y * 80) + x] = (byte)(y >= 20 && y <= 40 && x >= 10 && x <= 70 ? 225 : 25);
            }
        }

        editorVm.LoadFromMemoryPatch(id, "브러시스트로크ROI", roiGeometry, patch, 80, 60, []);
        var beforeActive = editorVm.ActiveFeatureCount;
        Assert.True(beforeActive > 20);

        editorVm.ActiveTool = PatternMaskDrawTool.Brush;
        editorVm.BrushSizePixels = 8.0;

        // Paint a fast horizontal brush stroke across the top horizontal edge (y = 20, x = 12 -> 68) in a single segment
        Assert.True(editorVm.TryPaintBrushStrokeSegment(12, 20, 68, 20));
        Assert.True(editorVm.MaskCount > 4);
        Assert.True(editorVm.ExcludedFeatureCount > 10);
        Assert.True(editorVm.ActiveFeatureCount < beforeActive);

        // Verify midpoint (40, 20) along the fast drag segment was continuously covered without gaps
        Assert.True(editorVm.HitTestMaskAtLocalPixel(40, 20) >= 0);

        // Continuous eraser drag across the same segment removes all masks along the stroke
        Assert.True(editorVm.TryEraseMasksAlongSegment(10, 20, 70, 20));
        Assert.Equal(0, editorVm.MaskCount);
        Assert.Equal(0, editorVm.ExcludedFeatureCount);
    }

    [Fact]
    public void PatMaxMatchingService_BuildExcludedMaskFast_WithCircleRoiShape_ExcludesExteriorCornerPixels()
    {
        var circleRoi = new RoiGeometry(new PointD(32, 32), 40, 40, 0, RoiShape.Circle);
        var excluded = TeachingToolWpf.Imaging.Matching.PatMaxMatchingService.BuildExcludedMaskFast(
            width: 64,
            height: 64,
            left: 0,
            top: 0,
            roi: circleRoi,
            exclusion: null,
            token: CancellationToken.None);

        // Center pixel (32.5, 32.5) is inside the radius-20 circle -> not excluded (0)
        Assert.Equal(0, excluded[(32 * 64) + 32]);

        // Corner of bounding box (14.5, 14.5) has local coordinates (-17.5, -17.5), radius = 24.75 > 20 -> excluded (255)
        Assert.Equal(byte.MaxValue, excluded[(14 * 64) + 14]);
        Assert.Equal(byte.MaxValue, excluded[(50 * 64) + 50]);
    }

    [Fact]
    public void PatternMaskEditorViewModel_WhenDipolesExceed600_ReportsExactTotalCountsWhileSubsamplingOverlayMarkers()
    {
        var session = new EditorSession();
        const int size = 128;
        var roiGeometry = new RoiGeometry(new PointD(64, 64), size, size, 0);
        var id = session.Add(roiGeometry);
        var editorVm = new PatternMaskEditorViewModel(session, new NullImageLoader(), () => null);

        // Dense grid of vertical step bars producing > 1,200 edge dipoles
        var patch = new byte[size * size];
        for (var y = 4; y < size - 4; y++)
        {
            for (var x = 4; x < size - 4; x++)
            {
                patch[(y * size) + x] = (byte)(((x / 4) & 1) == 0 ? 230 : 25);
            }
        }

        editorVm.LoadFromMemoryPatch(id, "고밀도ROI", roiGeometry, patch, size, size, []);
        var totalDipoles = editorVm.ActiveFeatureCount + editorVm.ExcludedFeatureCount;
        Assert.True(totalDipoles > 1000);
        Assert.True(editorVm.FeaturePoints.Length <= 601);

        // Mask the left half and verify exact counts still sum to totalDipoles
        Assert.True(editorVm.TryAddMaskFromLocalPixels(MaskShape.Rectangle, 0, 0, size / 2, size));
        Assert.Equal(totalDipoles, editorVm.ActiveFeatureCount + editorVm.ExcludedFeatureCount);
        Assert.True(editorVm.ExcludedFeatureCount > 400);
    }

    [Fact]
    public void PatternMaskEditorViewModel_UnlimitedBrushStrokes_SustainsWellAbove60FpsAndNeverHitsMaskCountLimit()
    {
        var session = new EditorSession();
        const int size = 256;
        var roiGeometry = new RoiGeometry(new PointD(128, 128), size, size, 0);
        var id = session.Add(roiGeometry);
        var editorVm = new PatternMaskEditorViewModel(session, new NullImageLoader(), () => null);

        var patch = new byte[size * size];
        for (var y = 8; y < size - 8; y++)
        {
            for (var x = 8; x < size - 8; x++)
            {
                patch[(y * size) + x] = (byte)((((x / 8) + (y / 8)) & 1) == 0 ? 220 : 35);
            }
        }

        editorVm.LoadFromMemoryPatch(id, "무제한브러시ROI", roiGeometry, patch, size, size, []);
        editorVm.ActiveTool = PatternMaskDrawTool.Brush;
        editorVm.BrushSizePixels = 12.0;

        // Simulate 2,000 continuous MouseMove brush segments across the 256x256 ROI (far exceeding MaximumMasksPerRoi = 512)
        const int totalSegments = 2000;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        editorVm.BeginInteractiveStroke();

        double prevX = 16.0;
        double prevY = 16.0;
        for (var i = 1; i <= totalSegments; i++)
        {
            double angle = i * 0.11;
            double radius = 10.0 + ((i % 100) * 1.05);
            double nextX = 128.0 + (Math.Cos(angle) * radius);
            double nextY = 128.0 + (Math.Sin(angle) * radius);

            Assert.True(editorVm.TryPaintBrushStrokeSegment(prevX, prevY, nextX, nextY));
            Assert.Empty(editorVm.Error);
            prevX = nextX;
            prevY = nextY;
        }

        editorVm.EndInteractiveStroke();
        sw.Stop();

        // 2,000 segments at 60 FPS (16.67 ms/frame) would allow 33,333 ms; require < 1.0 ms per segment (< 2000 ms total)
        double avgMsPerMove = sw.Elapsed.TotalMilliseconds / totalSegments;
        Assert.True(avgMsPerMove < 1.0, $"Expected < 1.0 ms per MouseMove (>> 60 FPS), actual: {avgMsPerMove:F4} ms/move.");

        // RLE compaction guarantees MaskCount <= MaximumMasksPerRoi and CommitMasksToSession succeeds
        Assert.True(editorVm.MaskCount > 0 && editorVm.MaskCount <= EditorSession.MaximumMasksPerRoi);
        Assert.True(editorVm.ExcludedFeatureCount > 100);
        Assert.True(editorVm.CommitMasksToSession());
    }

    [Fact]
    public void PatternMaskEditorViewModel_PolygonModeAndFastEraser_RasterizesPolygonAndErasesAtWellAbove60Fps()
    {
        var session = new EditorSession();
        const int size = 256;
        var roiGeometry = new RoiGeometry(new PointD(128, 128), size, size, 0);
        var id = session.Add(roiGeometry);
        var editorVm = new PatternMaskEditorViewModel(session, new NullImageLoader(), () => null);

        var patch = new byte[size * size];
        for (var y = 8; y < size - 8; y++)
        {
            for (var x = 8; x < size - 8; x++)
            {
                patch[(y * size) + x] = (byte)((((x / 8) + (y / 8)) & 1) == 0 ? 225 : 30);
            }
        }

        editorVm.LoadFromMemoryPatch(id, "폴리곤지우개ROI", roiGeometry, patch, size, size, []);
        var initialActive = editorVm.ActiveFeatureCount;
        Assert.True(initialActive > 200);

        // 1. Draw a pentagon mask using interactive Polygon mode
        editorVm.PolygonToolCommand.Execute(null);
        Assert.True(editorVm.IsPolygonTool);

        editorVm.AddDraftPolygonPoint(40, 40);
        editorVm.AddDraftPolygonPoint(200, 40);
        editorVm.AddDraftPolygonPoint(220, 140);
        editorVm.AddDraftPolygonPoint(120, 220);
        editorVm.AddDraftPolygonPoint(30, 140);
        editorVm.AddDraftPolygonPoint(99, 99); // extra vertex to test undo
        Assert.Equal(6, editorVm.DraftPolygonPoints.Count);
        Assert.True(editorVm.UndoLastDraftPolygonPoint());
        Assert.Equal(5, editorVm.DraftPolygonPoints.Count);
        Assert.True(editorVm.IsNearDraftPolygonStart(42, 41, snapRadiusLocalPixels: 5.0));

        Assert.True(editorVm.TryCompleteDraftPolygon());
        Assert.False(editorVm.HasDraftPolygon);
        Assert.True(editorVm.MaskCount > 0);
        var excludedAfterPolygon = editorVm.ExcludedFeatureCount;
        Assert.True(excludedAfterPolygon > 100);
        Assert.True(editorVm.HitTestMaskAtLocalPixel(120, 120) >= 0);

        // 2. Also add a Rectangle vector mask and verify the Eraser smoothly carves through both Polygon & Rectangle masks at >> 60 FPS
        Assert.True(editorVm.TryAddMaskFromLocalPixels(MaskShape.Rectangle, 10, 10, 80, 80));

        editorVm.EraserToolCommand.Execute(null);
        Assert.True(editorVm.IsEraserTool);
        editorVm.BrushSizePixels = 16.0;

        const int totalEraseMoves = 2000;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        editorVm.BeginInteractiveStroke();

        double prevX = 20.0;
        double prevY = 20.0;
        for (var i = 1; i <= totalEraseMoves; i++)
        {
            double t = (double)i / totalEraseMoves;
            double nextX = 20.0 + (t * 200.0);
            double nextY = 120.0 + (Math.Sin(i * 0.15) * 60.0);

            editorVm.TryEraseMasksAlongSegment(prevX, prevY, nextX, nextY);
            prevX = nextX;
            prevY = nextY;
        }

        editorVm.EndInteractiveStroke();
        sw.Stop();

        double avgEraseMsPerMove = sw.Elapsed.TotalMilliseconds / totalEraseMoves;
        Assert.True(avgEraseMsPerMove < 1.0, $"Expected < 1.0 ms per Eraser MouseMove (>> 60 FPS), actual: {avgEraseMsPerMove:F4} ms/move.");
        Assert.True(editorVm.ExcludedFeatureCount < excludedAfterPolygon);
        Assert.True(editorVm.CommitMasksToSession());
    }

    private sealed class NullImageLoader : IImageLoader
    {
        public Task<ITileSource> LoadAsync(ImageReference reference, IProgress<string> progress, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
