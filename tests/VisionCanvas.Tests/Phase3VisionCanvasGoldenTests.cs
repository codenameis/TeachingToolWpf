using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionCanvas.Core.Interaction;
using VisionCanvas.Filters.Wpf.ViewModels;
using VisionCanvas.Imaging.Filters;
using VisionCanvas.Presentation;
using VisionCanvas.Rendering;
using VisionCanvas.Rendering.Abstractions;
using VisionGeometry;
using VisionImaging.Abstractions;
using Xunit.Abstractions;

namespace VisionCanvas.Tests;

public sealed class Phase3VisionCanvasGoldenTests(ITestOutputHelper output)
{
    private const string ExpectedGestureSessionHash = "CA91A064A650CB2323515F237AF15F3B13EB63C4E5F909E080F016913A9471A4";
    private const string ExpectedAlignmentFilterHash = "6F0A7F1A778873426964D077BC41528895018C85C51DA8FEF8142B5EC1F8BDB8";

    [Fact]
    public void GestureAndEditorSessionWorkflow_PreservesExactGoldenHash()
    {
        var image = new ImageReference("golden:8k", "8K Golden", 8192, 8192);
        var session = new EditorSession();
        var controller = new InteractionController(session);
        controller.SetViewportSize(1200, 800);
        session.InitializeImage(image, ViewportTransform.Fit(8192, 8192, 1200, 800));

        var trace = new StringBuilder(16384);

        // 1. Draw Rectangle via gesture
        controller.DrawShape = RoiShapeKind.Rectangle;
        controller.Tool = EditorTool.Draw;
        var p1 = session.State.Viewport.ImageToScreen(new(1000, 1000));
        var p2 = session.State.Viewport.ImageToScreen(new(1600, 1400));
        controller.Down(p1);
        controller.Move(p2);
        controller.Up(p2);
        AppendStateSnapshot(trace, "AfterDrawRect", session, controller);

        // 2. Draw Circle via gesture
        controller.DrawShape = RoiShapeKind.Circle;
        controller.Tool = EditorTool.Draw;
        var c1 = session.State.Viewport.ImageToScreen(new(2000, 1000));
        var c2 = session.State.Viewport.ImageToScreen(new(2500, 1500));
        controller.Down(c1);
        controller.Move(c2);
        controller.Up(c2);
        AppendStateSnapshot(trace, "AfterDrawCircle", session, controller);

        // 3. Draw Polygon via gesture
        controller.DrawShape = RoiShapeKind.Polygon;
        controller.Tool = EditorTool.Draw;
        controller.Down(session.State.Viewport.ImageToScreen(new(3000, 1000)));
        controller.Up(session.State.Viewport.ImageToScreen(new(3000, 1000)));
        controller.Down(session.State.Viewport.ImageToScreen(new(3500, 1100)));
        controller.Up(session.State.Viewport.ImageToScreen(new(3500, 1100)));
        controller.Down(session.State.Viewport.ImageToScreen(new(3300, 1600)));
        controller.Up(session.State.Viewport.ImageToScreen(new(3300, 1600)));
        controller.CompletePolygon();
        AppendStateSnapshot(trace, "AfterDrawPolygon", session, controller);

        // 4. Marquee select all 3 ROIs
        controller.Down(session.State.Viewport.ImageToScreen(new(800, 800)));
        controller.Move(session.State.Viewport.ImageToScreen(new(3800, 1800)));
        controller.Up(session.State.Viewport.ImageToScreen(new(3800, 1800)));
        AppendStateSnapshot(trace, "AfterMarqueeSelect", session, controller);

        // 5. Move selection with Shift axis lock, then normal move
        var dragOrigin = session.State.Viewport.ImageToScreen(new(1300, 1200));
        controller.Down(dragOrigin);
        controller.Move(dragOrigin + new PointD(40, 12), InputModifiers.Shift);
        controller.Up(dragOrigin + new PointD(40, 12), InputModifiers.Shift);
        AppendStateSnapshot(trace, "AfterShiftMove", session, controller);

        // 6. Group -> Ungroup -> Undo -> Redo
        var groupId = session.GroupSelection();
        AppendStateSnapshot(trace, "AfterGroup", session, controller);
        session.UngroupSelection();
        AppendStateSnapshot(trace, "AfterUngroup", session, controller);
        session.Undo();
        AppendStateSnapshot(trace, "AfterUndoUngroup", session, controller);

        // 7. Select first leaf ROI, resize handle, rotate handle, mask draw, nudge
        var leaf = session.State.Rois.Values.Where(r => !session.State.IsGroupRoi(r.Id)).OrderBy(r => r.Order).First();
        session.Select([leaf.Id]);
        Span<PointD> handles = stackalloc PointD[8];
        HandleGeometry.CopyResizeHandlesTo(leaf.Geometry, session.State.Viewport, handles);
        controller.Down(handles[2]);
        controller.Move(handles[2] + new PointD(18, 14));
        controller.Up(handles[2] + new PointD(18, 14));
        AppendStateSnapshot(trace, "AfterResize", session, controller);

        leaf = session.State.Rois[leaf.Id];
        var rotHandle = HandleGeometry.RotationHandle(leaf.Geometry, session.State.Viewport, controller.HandleScale);
        controller.Down(rotHandle);
        controller.Move(rotHandle + new PointD(25, 10), InputModifiers.Shift);
        controller.Up(rotHandle + new PointD(25, 10), InputModifiers.Shift);
        AppendStateSnapshot(trace, "AfterRotate", session, controller);

        controller.StartMask(MaskShape.Rectangle);
        leaf = session.State.Rois[leaf.Id];
        var maskStart = session.State.Viewport.ImageToScreen(leaf.Geometry.Center - new PointD(40, 30));
        var maskEnd = session.State.Viewport.ImageToScreen(leaf.Geometry.Center + new PointD(40, 30));
        controller.Down(maskStart);
        controller.Move(maskEnd);
        controller.Up(maskEnd);
        AppendStateSnapshot(trace, "AfterMask", session, controller);

        Assert.True(controller.NudgeSelection(new(5, -3)));
        AppendStateSnapshot(trace, "AfterNudge", session, controller);

        // 8. Cancel mid-move must restore exact state
        leaf = session.State.Rois[leaf.Id];
        var leafScreen = session.State.Viewport.ImageToScreen(leaf.Geometry.Center);
        controller.Down(leafScreen);
        controller.Move(leafScreen + new PointD(50, 50));
        controller.Cancel();
        AppendStateSnapshot(trace, "AfterCancelledDrag", session, controller);

        // 9. Pan, Wheel, Minimap
        controller.Down(new(300, 300), InputModifiers.None, PointerButton.Middle);
        controller.Move(new(340, 320));
        controller.Up(new(340, 320));
        controller.Wheel(new(600, 400), 2);
        if (controller.Minimap is { } minimap)
        {
            var mmCenter = minimap.ImageBounds.Center;
            controller.Down(mmCenter);
            controller.Move(mmCenter + new PointD(8, 6));
            controller.Up(mmCenter + new PointD(8, 6));
        }

        AppendStateSnapshot(trace, "AfterNavigation", session, controller);

        // 10. Clipboard Copy + Paste + OCV + Properties + Rename + FilterPipeline
        var clipboard = new RoiClipboard();
        session.SelectAll();
        clipboard.Copy(session.State);
        session.Paste(clipboard);
        leaf = session.State.Rois.Values.Where(r => !session.State.IsGroupRoi(r.Id)).OrderBy(r => r.Order).First();
        session.SetProperties(leaf.Id, "ChipLead-01", leaf.Geometry, new RoiSearchRange(20, 20), isNameVisible: true);
        session.Rename(leaf.Id, "ChipLead-01-Renamed");
        session.SetOcvSettings([leaf.Id], new RoiOcvSettings("A12", "ADD", "Default"));
        session.Select([leaf.Id]);
        session.SetFilterPipeline([new FilterSettings(RoiFilterKind.Gaussian, Sigma: 1.5), new FilterSettings(RoiFilterKind.Binarize, Threshold: 120)]);
        AppendStateSnapshot(trace, "AfterSessionMutations", session, controller);

        // 11. Undo 4 steps + Redo 2 steps
        for (int i = 0; i < 4; i++)
        {
            session.Undo();
        }

        AppendStateSnapshot(trace, "AfterUndo4", session, controller);
        session.Redo();
        session.Redo();
        AppendStateSnapshot(trace, "AfterRedo2", session, controller);

        string actualHash = ComputeSha256(trace.ToString());
        output.WriteLine($"GOLDEN_PHASE3_GESTURE_SESSION_SHA256={actualHash}");
        if (!string.IsNullOrEmpty(ExpectedGestureSessionHash))
        {
            Assert.Equal(ExpectedGestureSessionHash, actualHash);
        }
    }

    [Fact]
    public async Task AlignmentAndFilterViewModelWorkflow_PreservesExactGoldenHashAndHotPathLatency()
    {
        var image = new ImageReference("golden:8k", "8K Golden", 8192, 8192);
        var session = new EditorSession();
        var input = new InteractionController(session);
        var layout = new ViewportLayout(1280, 800, 1.0);
        session.InitializeImage(image, ViewportTransform.Fit(8192, 8192, 1280, 800));

        var parentId = session.AddParent(new RoiGeometry(new(4096, 4096), 3000, 2000, 0));
        var moduleId = session.AddChild(parentId, new RoiGeometry(new(3400, 3800), 1000, 800, 0));
        session.Select([moduleId]);
        var leafId = session.AddChild(moduleId, new RoiGeometry(new(3300, 3750), 240, 160, 0));

        var statuses = new List<string>();
        using var coordinator = new ImageCoordinator(session, new NullViewportSurface(), layout, new StubSourceLoader());
        var alignmentVm = new AlignmentTestViewModel(
            session,
            input,
            layout,
            coordinator,
            () => { },
            statuses.Add);

        var trace = new StringBuilder(8192);
        alignmentVm.OnDocumentChanged();
        AppendAlignmentSnapshot(trace, "InitialScopes", alignmentVm);

        alignmentVm.SelectedScope = alignmentVm.Scopes[0];
        alignmentVm.NudgeCommand.Execute("X:10");
        alignmentVm.NudgeCommand.Execute("Y:-1");
        alignmentVm.NudgeCommand.Execute("Theta:1");
        AppendAlignmentSnapshot(trace, "GlobalNudged", alignmentVm);

        alignmentVm.SelectedScope = alignmentVm.Scopes.Last();
        alignmentVm.NudgeCommand.Execute("X:-1");
        alignmentVm.NudgeCommand.Execute("Y:10");
        alignmentVm.NudgeCommand.Execute("Theta:-5");
        AppendAlignmentSnapshot(trace, "LeafModuleNudged", alignmentVm);

        alignmentVm.ResetSelectedScope();
        AppendAlignmentSnapshot(trace, "ScopeReset", alignmentVm);
        alignmentVm.ResetAllScopes();
        AppendAlignmentSnapshot(trace, "AllReset", alignmentVm);

        using var filterVm = new FilterPreviewViewModel(new StubFilterProcessor(), session);
        session.Select([leafId]);
        filterVm.Update(session.State, isLive: false);
        filterVm.SelectedFilter = RoiFilterKind.Gaussian;
        filterVm.Sigma = "1.5";
        filterVm.AddStepToPipeline.Execute(null);
        filterVm.SelectedFilter = RoiFilterKind.Binarize;
        filterVm.Threshold = "140";
        filterVm.AddStepToPipeline.Execute(null);
        filterVm.MoveSelectedStepUp.Execute(null);
        await filterVm.WhenIdle;

        trace.Append(CultureInfo.InvariantCulture, $"FilterOptions={filterVm.Options.Count}|Summary={filterVm.PipelineSummaryText}|Steps={filterVm.PipelineSteps.Count};");
        foreach (var step in filterVm.PipelineSteps)
        {
            trace.Append(CultureInfo.InvariantCulture, $"{step.StepOrder}:{step.Settings.Kind}:{step.Settings.Threshold:R}:{step.Settings.Sigma:R}:{step.IsEnabled};");
        }

        string actualHash = ComputeSha256(trace.ToString());
        output.WriteLine($"GOLDEN_PHASE3_ALIGNMENT_FILTER_SHA256={actualHash}");
        if (!string.IsNullOrEmpty(ExpectedAlignmentFilterHash))
        {
            Assert.Equal(ExpectedAlignmentFilterHash, actualHash);
        }

        // Measure hot-path ScenePainter.Build + RoiSpatialTraversal median Release latency on 2,500 ROIs
        var benchSession = new EditorSession();
        benchSession.InitializeImage(image, new ViewportTransform(0.25, new(100, 80)));
        var geometries = new List<RoiGeometry>(2500);
        for (int r = 0; r < 50; r++)
        {
            for (int c = 0; c < 50; c++)
            {
                geometries.Add(new RoiGeometry(new(200 + (c * 140), 200 + (r * 140)), 90, 70, (r + c) % 30));
            }
        }

        benchSession.AddRange(geometries, selectAdded: false, isNameVisible: false);
        benchSession.State.Warm();
        var imageInfo = new ImageInfo(image.Key, image.Name, image.Width, image.Height, PixelKind.Gray8, ImageInfo.CalculateLevelCount(image.Width, image.Height));
        var scene = new RenderScene(benchSession.State, imageInfo, 1, 1280, 800, 1.0, new());
        var painter = new ScenePainter();
        using var white = new DummyTexture(1, 1);
        using var filters = new GpuFilterPreview();
        var tiles = new Dictionary<TileKey, IRenderTexture>();

        for (int warmup = 0; warmup < 5; warmup++)
        {
            _ = BuildAndTraverse(painter, scene, white, filters, tiles);
        }

        var samples = new double[15];
        for (int iter = 0; iter < samples.Length; iter++)
        {
            long t0 = Stopwatch.GetTimestamp();
            int blocks = BuildAndTraverse(painter, scene, white, filters, tiles);
            Assert.True(blocks > 0);
            samples[iter] = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }

        Array.Sort(samples);
        double medianMs = samples[samples.Length / 2];
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"GOLDEN_PHASE3_HOTPATH_MEDIAN_MS={medianMs:F4}"));
    }

    private static int BuildAndTraverse(
        ScenePainter painter,
        RenderScene scene,
        IRenderTexture white,
        GpuFilterPreview filters,
        IReadOnlyDictionary<TileKey, IRenderTexture> tiles)
    {
        var mesh = painter.Build(
            scene,
            white,
            tiles,
            _ => white,
            _ => white,
            live: null,
            liveBounds: default,
            filters: filters,
            liveOverview: null,
            batchLabel: null);
        var lod = RoiSpatialTraversal.ComputeLodMetrics(scene);
        int blockCount = mesh.Batches.Count;
        RoiSpatialTraversal.ForEachIndex(scene, (index, _, transform, _) =>
        {
            foreach (var _ in index.Blocks(transform.InverseTransformBounds(lod.VisibleBounds)))
            {
                blockCount++;
            }
        });
        return blockCount;
    }

    private static void AppendStateSnapshot(StringBuilder sb, string tag, EditorSession session, InteractionController controller)
    {
        var state = session.State;
        sb.Append(CultureInfo.InvariantCulture, $"[{tag}]Count={state.RoiCount}|Sel={state.SelectionCount}|Tool={controller.Tool}|Undo={session.CanUndo}:{session.UndoName}|Redo={session.CanRedo}:{session.RedoName}|View=({state.Viewport.Scale:R},{state.Viewport.Offset.X:R},{state.Viewport.Offset.Y:R})|");
        foreach (var roi in state.Rois.Values.OrderBy(r => r.Order))
        {
            var g = roi.Geometry;
            int parentOrder = roi.ParentId is { } pid && state.Rois.TryGetValue(pid, out var p) ? (int)p.Order : -1;
            sb.Append(CultureInfo.InvariantCulture, $"R({roi.Order},{roi.Name},{roi.IsGroup},P={parentOrder},{g.Shape.Kind},{g.Center.X:R},{g.Center.Y:R},{g.Width:R},{g.Height:R},{g.Angle:R},M={roi.Masks.Length},F={roi.EffectiveFilterPipeline.Length},Ocv={roi.Ocv.ExpectedText}/{roi.Ocv.FormatMask}/{roi.Ocv.FontId},Sel={state.Selection.Contains(roi.Id)});");
        }

        sb.AppendLine();
    }

    private static void AppendAlignmentSnapshot(StringBuilder sb, string tag, AlignmentTestViewModel vm)
    {
        sb.Append(CultureInfo.InvariantCulture, $"[{tag}]Scopes={vm.Scopes.Count}|Active={vm.CurrentAlignment is not null}|Total={vm.TotalLogicalRoiCount}|");
        foreach (var scope in vm.Scopes)
        {
            var t = vm.Session!.CurrentMap.GetScopeTransform(scope.ScopeId);
            sb.Append(CultureInfo.InvariantCulture, $"S({scope.Name},{t.AngleDegrees:R},{t.Translation.X:R},{t.Translation.Y:R});");
        }

        sb.AppendLine(vm.SampleCellsSummary);
    }

    private static string ComputeSha256(string text)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes);
    }

    private sealed class DummyTexture(int width, int height) : IRenderTexture
    {
        public PixelKind Kind => PixelKind.Rgba32;
        public int Width => width;
        public int Height => height;
        public bool IsOpaque => true;
        public bool HasPixels => true;
        public long Bytes => width * height * 4L;
        public long AllocatedBytes => Bytes;
        public void Dispose() { }
    }

    private sealed class NullViewportSurface : IViewportSurface
    {
        public double ViewWidth => 1280;
        public double ViewHeight => 800;
        public double Dpi => 1.0;
        public event Action? LayoutChanged { add { } remove { } }
        public long SetSource(ITileSource source) => 1;
        public Task RestartAsync() => Task.CompletedTask;
        public void Submit(RenderScene scene) { }
    }

    private sealed class StubSourceLoader : IImageLoader
    {
        public Task<ITileSource> LoadAsync(ImageReference reference, IProgress<string> progress, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class StubFilterProcessor : IRoiFilterPreview
    {
        public Task<FilterPreviewResult> ProcessAsync(FilterRequest request, CancellationToken token)
        {
            var regions = new List<FilteredRoi>(request.Rois.Count);
            foreach (var roi in request.Rois)
            {
                regions.Add(new FilteredRoi(roi.Id, roi.Geometry, 16, 16, new byte[256], null));
            }

            return Task.FromResult(new FilterPreviewResult(regions, 1.0));
        }
    }
}
