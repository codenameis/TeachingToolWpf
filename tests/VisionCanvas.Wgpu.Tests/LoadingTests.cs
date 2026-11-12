using System.IO;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Interaction;
using VisionCanvas.Presentation;
using VisionCanvas.Wgpu.Controls;
using VisionCanvas.SampleShell.Services;
using VisionCanvas.WgpuSample.ViewModels;
using VisionImaging;
using VisionImaging.Abstractions;

namespace VisionCanvas.Wgpu.Tests;

public sealed class LoadingTests
{
    [Fact]
    public Task FailedAndCancelledOpenKeepDocumentAndUndoHistory() => Sta.RunAsync(async () =>
    {
        using var host = new ViewportHost(new WgpuViewport());
        await host.Ready;
        var editor = new EditorSession();
        var layout = new ViewportLayout(600, 400, 1);
        var loader = new DeferredLoader();
        using var model = new EditorViewModel(editor, new InteractionController(editor), layout,
            new ImageCoordinator(editor, host.Viewport, layout, new SourceImageLoader(loader)), new Picker());
        await model.SampleCommand.ExecuteAsync();
        await model.AddCommand.ExecuteAsync();
        var before = editor.State;
        var generation = model.Scene.Generation;
        var pending = model.OpenCommand.ExecuteAsync();
        Assert.False(model.IsInputEnabled);
        Assert.False(model.AddCommand.CanExecute(null));
        Assert.False(model.UndoCommand.CanExecute(null));
        await model.CancelCommand.ExecuteAsync();
        await pending;
        Assert.Same(before, editor.State);
        Assert.Equal(generation, model.Scene.Generation);
        Assert.True(model.IsInputEnabled);
        Assert.True(model.UndoCommand.CanExecute(null));
        Assert.Contains("취소", model.Status);

        pending = model.OpenCommand.ExecuteAsync();
        loader.Pending.SetException(new IOException("검증용 읽기 실패"));
        await pending;
        Assert.Contains("검증용 읽기 실패", model.Status);
        Assert.Same(before, editor.State);
        Assert.Equal(generation, model.Scene.Generation);
        Assert.True(model.IsInputEnabled);
        await model.UndoCommand.ExecuteAsync();
        Assert.Empty(editor.State.Rois);
        Assert.True(model.RedoCommand.CanExecute(null));
    });

    private sealed class DeferredLoader : IImageLoader
    {
        public TaskCompletionSource<ITileSource> Pending { get; private set; } = null!;

        public Task<ITileSource> LoadAsync(ImageReference reference, IProgress<string> progress, CancellationToken cancellationToken)
        {
            Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return Pending.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class Picker : IImageFilePicker
    {
        public ImageReference Pick() => new("deferred", "읽기 시험", 100, 100);
    }
}

