using System.Runtime.ExceptionServices;
using TeachingToolWpf.Presentation;
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
    [InlineData(false)]
    [InlineData(true)]
    public void PolygonAddStartsPointDrawingAndCrossingCommitsOneRoi(bool child) => OnSta(() =>
    {
        using var surface = new Surface();
        using var model = CreateModel(surface);
        LoadImage(model);
        var parent = model.Session.Add(new(new(500, 500), 200, 200, 0));
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
        Assert.True(model.IsSelect);
        model.AddChildRoi.Execute(null);
        var child = model.Session.State.Rois[model.Session.State.Selection.Single()];
        Assert.Equal(parent.Id, child.ParentId);
        Assert.Equal(kind, child.Geometry.Shape.Kind);
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

    private static EditorViewModel CreateModel(Surface surface) => new(surface, new TestLoader(),
        new LivePreview(surface, _ => throw new NotSupportedException()), new UnusedFilter());

    private static void LoadImage(EditorViewModel model)
    {
        model.Images.ReplaceAsync(new("test", "test", 1000, 1000)).GetAwaiter().GetResult();
        model.Session.SetViewport(ViewportTransform.Identity);
    }

    private static void Click(EditorViewModel model, params PointD[] points)
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
