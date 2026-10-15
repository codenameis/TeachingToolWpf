using VisionCanvas.Core.Editing;
using VisionCanvas.MvvmSample.Models;
using VisionCanvas.MvvmSample.Services;
using VisionCanvas.MvvmSample.ViewModels;
using VisionCanvas.Presentation;
using VisionImaging;
using VisionImaging.Abstractions;

namespace VisionCanvas.MvvmSample.Tests;

public sealed class InspectionModelTests
{
    [Fact]
    public async Task TwoCamerasLoadIndependentDocumentsAndEveryLeafHasAlgorithmSettings()
    {
        var definition = SampleInspectionModel.Create();
        using var firstDisplay = new Display();
        using var secondDisplay = new Display();
        using var model = new InspectionModelViewModel(definition.Name,
            Create(definition.FirstCamera, firstDisplay), Create(definition.SecondCamera, secondDisplay));
        await model.LoadCommand.ExecuteAsync();
        foreach (var camera in new[] { model.FirstCamera, model.SecondCamera })
        {
            Assert.Equal(7, camera.Scene.Document.Rois.Count);
            Assert.Equal(2, camera.Scene.Document.Rois.Values.Count(roi => roi.IsGroup));
            Assert.Equal(5, camera.RoiItems.Count(item => item.Algorithm is not null));
            Assert.False(camera.UndoCommand.CanExecute(null));
            Assert.Equal(8192, camera.Scene.Image!.Width);
            Assert.DoesNotContain("오류", camera.Status);
        }
        var first = model.FirstCamera;
        var secondBefore = model.SecondCamera.Scene.Document;
        first.SelectedRoi = first.RoiItems.First(item => item.Algorithm is not null);
        var selected = first.SelectedRoi!;
        Assert.Contains(selected.Id, first.Scene.Document.Selection);
        first.DeleteCommand.Execute(null);
        Assert.DoesNotContain(first.RoiItems, item => item.Id == selected.Id);
        await first.UndoCommand.ExecuteAsync();
        Assert.Equal(selected.Algorithm, first.RoiItems.Single(item => item.Id == selected.Id).Algorithm);
        Assert.Equal(secondBefore, model.SecondCamera.Scene.Document);

        // 삭제 후 모델 재로딩도 한 번에 되돌릴 수 있고 다른 카메라의 이력에는 섞이지 않는다.
        first.DeleteCommand.Execute(null);
        await model.LoadCommand.ExecuteAsync();
        Assert.Equal(7, first.Scene.Document.Rois.Count);
        await first.UndoCommand.ExecuteAsync();
        Assert.Equal(6, first.Scene.Document.Rois.Count);
        Assert.Equal(7, model.SecondCamera.Scene.Document.Rois.Count);
        model.Dispose();
        Assert.False(model.LoadCommand.CanExecute(null));
    }

    [Fact]
    public async Task CameraImagesHaveDistinctIdentityAndPixelsAndLoaderDelegatesFiles()
    {
        var fallback = new FallbackLoader();
        var loader = new DemoImageLoader(fallback);
        var definition = SampleInspectionModel.Create();
        using var first = await loader.LoadAsync(definition.FirstCamera.Image, new Progress<string>(), default);
        using var second = await loader.LoadAsync(definition.SecondCamera.Image, new Progress<string>(), default);
        using var firstTile = first.Read(new(4, 0, 0), default);
        using var secondTile = second.Read(new(4, 0, 0), default);
        Assert.NotEqual(first.Info.Key, second.Info.Key);
        Assert.False(firstTile.Span.SequenceEqual(secondTile.Span));
        using var file = await loader.LoadAsync(new("file.png", "파일", 8192, 8192), new Progress<string>(), default);
        Assert.Equal(1, fallback.Calls);
    }

    private static EditorViewModel Create(CameraModel camera, Display display)
    {
        var editor = new EditorSession();
        var layout = new ViewportLayout(800, 600);
        return new(editor, new(editor), layout,
            new(editor, display, layout, new DemoImageLoader(new FallbackLoader())), new Picker(), camera);
    }
    private sealed class Picker : IImageFilePicker
    {
        public ImageReference? Pick() => null;
    }
    private sealed class FallbackLoader : IImageLoader
    {
        public int Calls { get; private set; }
        public Task<ITileSource> LoadAsync(ImageReference reference, IProgress<string> progress, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<ITileSource>(new ProceduralTileSource());
        }
    }
    private sealed class Display : IImageDisplay, IDisposable
    {
        private ITileSource? _source;
        private long _generation;
        public long SetSource(ITileSource source)
        {
            _source?.Dispose();
            _source = source;
            return ++_generation;
        }
        public Task RestartAsync() => Task.CompletedTask;
        public void Dispose() => _source?.Dispose();
    }
}
