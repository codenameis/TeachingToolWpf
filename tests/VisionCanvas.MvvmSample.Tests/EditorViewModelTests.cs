using System.IO;
using System.Windows.Input;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Interaction;
using VisionCanvas.MvvmSample.Services;
using VisionCanvas.MvvmSample.ViewModels;
using VisionCanvas.Presentation;
using VisionGeometry;
using VisionImaging;
using VisionImaging.Abstractions;

namespace VisionCanvas.MvvmSample.Tests;

public sealed class EditorViewModelTests
{
    [Fact]
    public async Task EmptyEditorLoads8KAndDrawsWithUndoRedoThroughCommands()
    {
        using var display = new Display();
        using var model = Create(display);
        Assert.False(model.DrawRectangleCommand.CanExecute(null));
        await model.SampleCommand.ExecuteAsync();
        Assert.Equal(8192, model.Scene.Image!.Width);
        Assert.Equal(8192, model.Scene.Image.Height);
        Assert.False(model.UndoCommand.CanExecute(null));
        model.DrawRectangleCommand.Execute(null);
        model.Input.Down(new(200, 200));
        model.Input.Up(new(300, 300));
        var roi = Assert.Single(model.Scene.Document.Rois).Value;
        Assert.False(roi.IsGroup);
        Assert.True(model.UndoCommand.CanExecute(null));
        await model.UndoCommand.ExecuteAsync();
        Assert.Empty(model.Scene.Document.Rois);
        await model.RedoCommand.ExecuteAsync();
        Assert.Equal(roi, Assert.Single(model.Scene.Document.Rois).Value);
    }

    [Fact]
    public async Task FailedOpenPreservesImageAndHistoryAndReportsError()
    {
        using var display = new Display();
        using var model = Create(display, new Picker(new("missing", "missing", 0, 0)));
        await model.SampleCommand.ExecuteAsync();
        var before = model.Scene;
        await model.OpenCommand.ExecuteAsync();
        Assert.Equal(before.Document, model.Scene.Document);
        Assert.Equal(before.Generation, model.Scene.Generation);
        Assert.Contains("파일 없음", model.Status);
        Assert.True(model.OpenCommand.CanExecute(null));
    }

    [Fact]
    public async Task GroupingClipboardAndKeyboardUseTheSameDocumentHistory()
    {
        using var display = new Display();
        using var model = Create(display);
        await model.SampleCommand.ExecuteAsync();
        Draw(model, new(180, 180), new(230, 230));
        Draw(model, new(280, 200), new(330, 250));
        model.SelectAllCommand.Execute(null);
        Assert.True(model.GroupCommand.CanExecute(null));
        model.GroupCommand.Execute(null);
        var grouped = model.Scene.Document;
        Assert.Equal(3, grouped.Rois.Count);
        Assert.Single(grouped.Rois.Values, roi => roi.IsGroup);
        model.CopyCommand.Execute(null);
        model.PasteCommand.Execute(null);
        Assert.Equal(6, model.Scene.Document.Rois.Count);
        await model.UndoCommand.ExecuteAsync();
        Assert.Equal(grouped.Rois, model.Scene.Document.Rois);
        Assert.True(model.HandleKey(Key.Delete, false, false));
        Assert.Empty(model.Scene.Document.Rois);
        await model.UndoCommand.ExecuteAsync();
        Assert.Equal(grouped.Rois, model.Scene.Document.Rois);
    }

    [Fact]
    public async Task ParentChildDrawingAndUngroupingPreserveGeometryAndHistory()
    {
        using var display = new Display();
        using var model = Create(display);
        await model.SampleCommand.ExecuteAsync();
        model.DrawParentCommand.Execute(null);
        model.Input.Down(new(180, 150));
        model.Input.Up(new(400, 350));
        var parent = Assert.Single(model.Scene.Document.Rois).Value;
        Assert.True(parent.IsGroup);
        Draw(model, new(230, 200), new(300, 260));
        var child = Assert.Single(model.Scene.Document.Rois.Values, roi => !roi.IsGroup);
        Assert.Equal(parent.Id, child.ParentId);
        Assert.True(parent.Geometry.Contains(child.Geometry));
        model.SelectAllCommand.Execute(null);
        model.UngroupCommand.Execute(null);
        var remaining = Assert.Single(model.Scene.Document.Rois).Value;
        Assert.Null(remaining.ParentId);
        Assert.Equal(child.Geometry, remaining.Geometry);
        await model.UndoCommand.ExecuteAsync();
        Assert.Equal(child, model.Scene.Document.Rois[child.Id]);
    }

    [Fact]
    public async Task PolygonCancelDoesNotCreateHistoryAndCompletionCreatesOneEdit()
    {
        using var display = new Display();
        using var model = Create(display);
        await model.SampleCommand.ExecuteAsync();
        model.DrawPolygonCommand.Execute(null);
        Click(new(200, 200));
        Click(new(300, 200));
        Assert.False(model.CompletePolygonCommand.CanExecute(null));
        Assert.True(model.HandleKey(Key.Escape, false, false));
        Assert.Empty(model.Scene.Document.Rois);
        Assert.False(model.UndoCommand.CanExecute(null));
        model.DrawPolygonCommand.Execute(null);
        Click(new(200, 200));
        Click(new(300, 200));
        Click(new(250, 300));
        Assert.True(model.HandleKey(Key.Enter, false, false));
        Assert.Single(model.Scene.Document.Rois);
        await model.UndoCommand.ExecuteAsync();
        Assert.Empty(model.Scene.Document.Rois);

        void Click(PointD point)
        {
            model.Input.Down(point);
            model.Input.Up(point);
        }
    }

    [Fact]
    public async Task UndoOfParentWhileChildToolIsArmedReturnsToSelection()
    {
        using var display = new Display();
        using var model = Create(display);
        await model.SampleCommand.ExecuteAsync();
        model.DrawParentCommand.Execute(null);
        model.Input.Down(new(180, 150));
        model.Input.Up(new(400, 350));
        model.DrawCircleCommand.Execute(null);
        Assert.NotNull(model.Input.DrawParentId);
        await model.UndoCommand.ExecuteAsync();
        Assert.Equal(EditorTool.Select, model.Input.Tool);
        Assert.Empty(model.Scene.Document.Rois);
    }

    [Fact]
    public async Task PickerCancelKeepsExistingDocumentAndNoError()
    {
        using var display = new Display();
        using var model = Create(display);
        await model.SampleCommand.ExecuteAsync();
        var before = model.Scene;
        await model.OpenCommand.ExecuteAsync();
        Assert.Same(before, model.Scene);
        Assert.DoesNotContain("오류", model.Status);
    }

    [Fact]
    public async Task CancellationDisposesLateSourceWithoutPublishingToClosedEditor()
    {
        using var display = new Display();
        var completion = new TaskCompletionSource<ITileSource>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var model = Create(display, loader: new Loader(completion.Task));
        var opening = model.SampleCommand.ExecuteAsync();
        Assert.False(model.IsInputEnabled);
        Assert.False(model.OpenCommand.CanExecute(null));
        model.Dispose();
        var source = new TrackedSource();
        completion.SetResult(source);
        await opening;
        Assert.True(source.Disposed);
        Assert.Equal(0, display.Transfers);
        Assert.False(model.SampleCommand.CanExecute(null));
    }

    private static void Draw(EditorViewModel model, PointD from, PointD to)
    {
        model.DrawRectangleCommand.Execute(null);
        model.Input.Down(from);
        model.Input.Up(to);
    }

    private static EditorViewModel Create(Display display, IImageFilePicker? picker = null, IImageLoader? loader = null)
    {
        var editor = new EditorSession();
        var layout = new ViewportLayout(800, 600);
        return new(editor, new(editor), layout, new(editor, display, layout, loader ?? new Loader()), picker ?? new Picker(null));
    }

    private sealed class Picker(ImageReference? reference) : IImageFilePicker
    {
        public ImageReference? Pick() => reference;
    }

    private sealed class Loader(Task<ITileSource>? pending = null) : IImageLoader
    {
        public Task<ITileSource> LoadAsync(ImageReference reference, IProgress<string> progress, CancellationToken cancellationToken)
        {
            if (pending is not null)
            {
                return pending;
            }
            if (reference.Key == "missing")
            {
                throw new IOException("파일 없음");
            }
            return Task.FromResult<ITileSource>(new ProceduralTileSource(reference.Width, reference.Height));
        }
    }

    private sealed class Display : IImageDisplay, IDisposable
    {
        private ITileSource? _source;
        public int Transfers { get; private set; }
        public long SetSource(ITileSource source)
        {
            _source?.Dispose();
            _source = source;
            return ++Transfers;
        }
        public Task RestartAsync() => Task.CompletedTask;
        public void Dispose() => _source?.Dispose();
    }

    private sealed class TrackedSource : ITileSource
    {
        public bool Disposed { get; private set; }
        public ImageInfo Info { get; } = new("late", "late", 8192, 8192, PixelKind.Gray8, 5);
        public PixelBuffer Read(TileKey key, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void Dispose() => Disposed = true;
    }
}
