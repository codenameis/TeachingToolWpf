using System.Runtime.ExceptionServices;
using TeachingToolWpf.Services;
using TeachingToolWpf.ViewModels;
using VisionCanvas.Core.Editing;
using VisionCanvas.Imaging.Filters;
using VisionCanvas.Presentation;
using VisionCanvas.Rendering;
using VisionGeometry;
using VisionImaging;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Tests;

public class PolygonDrawingPresentationTests
{
    [Theory]
    [InlineData(RoiShapeKind.Rectangle, false)]
    [InlineData(RoiShapeKind.Circle, false)]
    [InlineData(RoiShapeKind.Triangle, false)]
    [InlineData(RoiShapeKind.Polygon, false)]
    [InlineData(RoiShapeKind.Rectangle, true)]
    [InlineData(RoiShapeKind.Circle, true)]
    [InlineData(RoiShapeKind.Triangle, true)]
    [InlineData(RoiShapeKind.Polygon, true)]
    public void ParentButtonsCreateOneGroupBeforeAnyChildExists(RoiShapeKind kind, bool draw) => OnSta(() =>
    {
        using var surface = new Surface();
        using var model = CreateModel(surface);
        Assert.False(model.AddParentRoi.CanExecute(null));
        Assert.False(model.DrawParentRoi.CanExecute(null));
        LoadImage(model);
        model.SelectedShape = kind;
        (draw ? model.DrawParentRoi : model.AddParentRoi).Execute(null);
        if (kind == RoiShapeKind.Polygon)
        {
            Click(model, new(10, 10), new(110, 10), new(110, 110));
            Assert.True(model.Input.Overlay.DraftIsGroup);
            model.CompletePolygon.Execute(null);
        }
        else if (draw)
        {
            model.Input.Down(new(10, 10));
            model.Input.Move(new(110, 110));
            Assert.True(model.Input.Overlay.DraftIsGroup);
            model.Input.Up(new(110, 110));
        }
        var group = Assert.Single(model.Session.State.Rois).Value;
        Assert.Null(group.ParentId);
        Assert.True(group.IsGroup);
        Assert.Equal(kind, group.Geometry.Shape.Kind);
        Assert.Equal(RoiRole.Parent, RoiStyleRoles.Resolve(model.Scene!.Document, group.Id));
        model.Session.Undo();
        Assert.Empty(model.Session.State.Rois);
        model.Session.Redo();
        Assert.Equal(group, model.Session.State.Rois[group.Id]);
        model.SelectedShape = RoiShapeKind.Rectangle;
        model.AddChildRoi.Execute(null);
        var child = model.Session.State.Rois[model.Session.State.Selection.Single()];
        Assert.Equal(group.Id, child.ParentId);
        Assert.False(child.IsGroup);
        Assert.Equal(2, model.Session.State.Rois.Count);
    });

    [Fact]
    public void CancellingParentDrawingThenSwitchingToolsDoesNotLeakGroupIntent() => OnSta(() =>
    {
        using var surface = new Surface();
        using var model = CreateModel(surface);
        LoadImage(model);
        model.DrawParentRoi.Execute(null);
        model.Input.Down(new(10, 10));
        model.Input.Move(new(110, 110));
        Assert.False(model.DrawTool.CanExecute(null));
        model.Input.Cancel();
        model.DrawTool.Execute(null);
        Assert.Null(model.Input.Overlay.Draft);
        Assert.Empty(model.Session.State.Rois);
        Assert.False(model.Input.DrawAsGroup);
        model.Input.Down(new(10, 10));
        model.Input.Up(new(110, 110));
        var leaf = Assert.Single(model.Session.State.Rois).Value;
        Assert.False(leaf.IsGroup);
        model.DrawParentRoi.Execute(null);
        model.DrawChildRoi.Execute(null);
        Assert.False(model.Input.DrawAsGroup);
        Assert.Equal(leaf.Id, model.Input.DrawParentId);
        model.DrawParentRoi.Execute(null);
        Assert.Null(model.Input.DrawParentId);
        Assert.True(model.Input.DrawAsGroup);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PolygonAddStartsPointDrawingAndCrossingCommitsOneRoi(bool child) => OnSta(() =>
    {
        using var surface = new Surface();
        using var model = CreateModel(surface);
        LoadImage(model);
        var parent = model.Session.Add(new(new(100, 100), 200, 200, 0));
        model.SelectedShape = RoiShapeKind.Polygon;
        var before = model.Session.State;
        var bytes = model.Session.HistoryBytes;
        var command = child ? model.AddChildRoi : model.AddRoi;
        Assert.True(command.CanExecute(null));
        command.Execute(null);

        Assert.Same(before, model.Session.State);
        Assert.Equal(bytes, model.Session.HistoryBytes);
        Assert.True(model.IsDraw);
        Assert.Equal(child ? parent : (Guid?)null, model.Input.DrawParentId);
        Assert.Equal(0, model.Input.PolygonPointCount);
        Assert.Contains("교차", model.ToolHint);
        Click(model, new(10, 10), new(110, 110), new(10, 110));
        Assert.Single(model.Session.State.Rois);
        Assert.True(model.CompletePolygon.CanExecute(null));
        Click(model, new PointD(110, 10));

        Assert.True(model.IsSelect);
        Assert.Equal(2, model.Session.State.Rois.Count);
        var polygon = model.Session.State.Rois[model.Session.State.Selection.Single()];
        Assert.Equal(RoiShapeKind.Polygon, polygon.Geometry.Shape.Kind);
        Assert.Equal(3, polygon.Geometry.Outline.Length);
        Assert.Equal(child ? parent : (Guid?)null, polygon.ParentId);
        Assert.False(polygon.IsGroup);
        Assert.Equal(RoiRole.Child, RoiStyleRoles.Resolve(model.Scene!.Document, polygon.Id));
        model.Session.Undo();
        Assert.Single(model.Session.State.Rois);
        model.Session.Redo();
        Assert.Equal(polygon, model.Session.State.Rois[polygon.Id]);
    });

    [Fact]
    public void CancelAndRemovePointCommandsPreserveRedoAndDocument() => OnSta(() =>
    {
        using var surface = new Surface();
        using var model = CreateModel(surface);
        LoadImage(model);
        model.Session.Add(new(new(500, 500), 200, 200, 0));
        model.Session.Undo();
        var before = model.Session.State;
        model.SelectedShape = RoiShapeKind.Polygon;
        model.AddRoi.Execute(null);
        Click(model, new(10, 10), new(110, 10), new(110, 110));
        model.RemovePolygonPoint.Execute(null);
        Assert.Equal(2, model.Input.PolygonPointCount);
        Assert.False(model.CompletePolygon.CanExecute(null));
        model.CancelPolygon.Execute(null);
        Assert.Equal(0, model.Input.PolygonPointCount);
        Assert.Same(before, model.Session.State);
        Assert.True(model.Session.CanRedo);
    });

    [Theory]
    [InlineData(RoiShapeKind.Rectangle)]
    [InlineData(RoiShapeKind.Circle)]
    [InlineData(RoiShapeKind.Triangle)]
    public void OtherShapeAddButtonsStillCreateImmediately(RoiShapeKind kind) => OnSta(() =>
    {
        using var surface = new Surface();
        using var model = CreateModel(surface);
        LoadImage(model);
        model.SelectedShape = kind;
        model.AddRoi.Execute(null);
        var parent = Assert.Single(model.Session.State.Rois).Value;
        Assert.Equal(kind, parent.Geometry.Shape.Kind);
        Assert.Null(parent.ParentId);
        Assert.False(parent.IsGroup);
        Assert.Equal(RoiRole.Child, RoiStyleRoles.Resolve(model.Scene!.Document, parent.Id));
        Assert.True(model.IsSelect);
        model.AddChildRoi.Execute(null);
        var child = model.Session.State.Rois[model.Session.State.Selection.Single()];
        Assert.Equal(parent.Id, child.ParentId);
        Assert.Equal(kind, child.Geometry.Shape.Kind);
    });

    [Theory]
    [InlineData(RoiShapeKind.Rectangle)]
    [InlineData(RoiShapeKind.Circle)]
    [InlineData(RoiShapeKind.Triangle)]
    public void DefaultDrawButtonCreatesIndependentLeafAfterChildDrawMode(RoiShapeKind kind) => OnSta(() =>
    {
        using var surface = new Surface();
        using var model = CreateModel(surface);
        LoadImage(model);
        var parent = model.Session.Add(new(new(500, 500), 100, 100, 0));
        model.DrawChildRoi.Execute(null);
        Assert.Equal(parent, model.Input.DrawParentId);
        model.SelectedShape = kind;
        model.DrawTool.Execute(null);
        Assert.Null(model.Input.DrawParentId);
        model.Input.Down(new(10, 10));
        model.Input.Up(new(110, 110));
        var leaf = model.Session.State.Rois[model.Session.State.Selection.Single()];
        Assert.Equal(2, model.Session.State.Rois.Count);
        Assert.Null(leaf.ParentId);
        Assert.False(leaf.IsGroup);
        Assert.Equal(kind, leaf.Geometry.Shape.Kind);
        Assert.Equal(RoiRole.Child, RoiStyleRoles.Resolve(model.Scene!.Document, leaf.Id));
        model.Session.Undo();
        Assert.Single(model.Session.State.Rois);
        model.Session.Redo();
        Assert.Equal(leaf, model.Session.State.Rois[leaf.Id]);
    });

    [Fact]
    public void PolygonAddRequiresImageAndChildRequiresOneParent() => OnSta(() =>
    {
        using var surface = new Surface();
        using var model = CreateModel(surface);
        model.SelectedShape = RoiShapeKind.Polygon;
        Assert.False(model.AddRoi.CanExecute(null));
        Assert.False(model.AddChildRoi.CanExecute(null));
        LoadImage(model);
        Assert.True(model.AddRoi.CanExecute(null));
        Assert.False(model.AddChildRoi.CanExecute(null));
    });

    private static MainWindowViewModel CreateModel(Surface surface) => new(surface, new(surface.ViewWidth, surface.ViewHeight, surface.Dpi), new TestLoader(),
        new LivePreview(surface, _ => throw new NotSupportedException()), new UnusedFilter(), EditorTestServices.Inspections());

    private static void LoadImage(MainWindowViewModel model)
    {
        model.Images.ReplaceAsync(new("test", "test", 1000, 1000)).GetAwaiter().GetResult();
        model.Session.SetViewport(ViewportTransform.Identity);
    }

    private static void Click(MainWindowViewModel model, params PointD[] points)
    {
        foreach (var point in points)
        {
            model.Input.Down(point);
            model.Input.Up(point);
        }
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private sealed class Surface : IViewportSurface, IFramePresenter, IDisposable
    {
        private ITileSource? _source;

        public double ViewWidth => 1000;

        public double ViewHeight => 700;

        public double Dpi => 1;

        public event Action? LayoutChanged
        {
            add
            {
            }
            remove
            {
            }
        }

        public long SetSource(ITileSource source)
        {
            _source?.Dispose();
            _source = source;
            return 1;
        }

        public void Dispose() => _source?.Dispose();

        public void Submit(RenderScene scene)
        {
        }

        public Task RestartAsync() => Task.CompletedTask;

        public void PublishFrame(VideoFrame frame) => throw new NotSupportedException();

        public void ClearFrames()
        {
        }
    }

    private sealed class TestLoader : IImageLoader
    {
        public Task<ITileSource> LoadAsync(ImageReference reference, IProgress<string> progress, CancellationToken token)
            => Task.FromResult<ITileSource>(new ProceduralTileSource(1000, 1000));
    }

    private sealed class UnusedFilter : IRoiFilterPreview
    {
        public Task<FilterPreviewResult> ProcessAsync(FilterRequest request, CancellationToken token)
            => throw new NotSupportedException();
    }
}
