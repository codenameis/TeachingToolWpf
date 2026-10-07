using System.Collections.Immutable;
using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Presentation;
using VisionAlgorithm;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionAlgorithm.Tests;
using VisionCanvas.Core.Editing;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Tests;

public sealed class ShapeMatchingIntegrationTests
{
    [Fact]
    public async Task OpenCvModeUsesOriginalPixelsAndKeepsIndependentModelsAndHistory()
    {
        var loader = new Loader();
        var editor = new EditorSession();
        editor.InitializeImage(new("fixture", "fixture", 1536, 256), ViewportTransform.Identity);
        editor.Add(new(new(80, 90), 65, 65, 0));
        using var viewModel = new ShapeMatchingViewModel(new ShapeMatchingService(loader, () => null), new TemplateMatchingService(loader, () => null));
        viewModel.Update(editor.State, 1, false, true);
        await viewModel.Train.ExecuteAsync();
        var shape = viewModel.Model;
        var state = editor.State;
        viewModel.Method = PatternMatchingMethod.OpenCvTemplate;
        viewModel.MinimumScore = .98;
        Assert.False(viewModel.Search.CanExecute(null));
        await viewModel.Train.ExecuteAsync();
        var template = viewModel.TemplateModel;
        Assert.NotNull(template);
        await viewModel.Search.ExecuteAsync();
        var marker = Assert.Single(viewModel.Markers);
        Assert.Equal(new PointD(80, 90), marker.ReferencePoint);
        Assert.Empty(marker.Features);
        Assert.Equal(InspectionVerdict.Pass, viewModel.Verdict);
        Assert.Same(state, editor.State);
        Assert.Equal(loader.Opened, loader.Disposed);
        viewModel.Method = PatternMatchingMethod.Shape;
        Assert.Same(shape, viewModel.Model);
        Assert.Empty(viewModel.Markers);
        viewModel.Method = PatternMatchingMethod.OpenCvTemplate;
        Assert.Same(template, viewModel.TemplateModel);
        viewModel.Update(editor.State, 2, false, true);
        Assert.Same(template, viewModel.TemplateModel);
    }

    [Fact]
    public async Task ExistingRoiTrainsAndSearchesAcrossTileBoundaryWithoutEditingDocument()
    {
        var loader = new Loader();
        var service = new ShapeMatchingService(loader, () => null);
        var editor = new EditorSession();
        editor.InitializeImage(new("fixture", "fixture", 1536, 256), ViewportTransform.Identity);
        editor.Add(new(new(80, 90), 65, 65, 17));
        using var model = new ShapeMatchingViewModel(service) { MinimumAngle = -40, MaximumAngle = 40, AngleStep = 5 };
        model.Update(editor.State, 1, false, true);
        var state = editor.State;
        await model.Train.ExecuteAsync();
        Assert.NotNull(model.Model);
        await model.Search.ExecuteAsync();
        Assert.Equal(2, model.Markers.Length);
        Assert.Contains(model.Markers, m => Math.Abs(m.ReferencePoint.X - 769.3) < 0.5);
        Assert.Same(state, editor.State);
        Assert.Equal(loader.Opened, loader.Disposed);
        editor.Select([]);
        model.Update(editor.State, 1, false, true);
        Assert.Equal(2, model.Markers.Length);
        model.Update(editor.State, 2, false, true);
        Assert.Empty(model.Markers);
        Assert.NotNull(model.Model);
    }

    [Fact]
    public async Task LateTrainingResultIsDiscardedAfterSourceGenerationChanges()
    {
        var delayed = new DelayedService();
        using var viewModel = new ShapeMatchingViewModel(delayed);
        var editor = new EditorSession();
        editor.InitializeImage(new("fixture", "fixture", 200, 200), ViewportTransform.Identity);
        editor.Add(new(new(80, 90), 60, 60, 0));
        viewModel.Update(editor.State, 1, false, true);
        var pending = viewModel.Train.ExecuteAsync();
        viewModel.Update(editor.State, 2, false, true);
        delayed.Completion.SetResult(new(ShapeTrainer.Train(SyntheticScene.Create(96, 96, new MatchPose(48, 48, 0)),
            new(ReadOnlyMemory<byte>.Empty, new(48, 48)), new()), 0, 0));
        await pending;
        Assert.Null(viewModel.Model);
        Assert.Empty(viewModel.Markers);
    }

    [Fact]
    public void CircleSearchAdapterUsesExactCircleInsteadOfDisplayPolygon()
    {
        var geometry = RoiGeometry.Create(RoiShapeKind.Circle, new(100, 100), 80, 80);
        var region = ShapeMatchingService.ToSearchRegion(geometry, 30, 20)!;
        Assert.True(region.Contains(70, 120));
        Assert.False(region.Contains(100, 110));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OutsideImageRegionFailsBeforeOpeningReader(bool training)
    {
        var loader = new Loader();
        var service = new ShapeMatchingService(loader, () => null);
        var image = new ImageReference("fixture", "fixture", 1536, 256);
        var outside = new RoiGeometry(new(1530, 128), 100, 100, 0);
        if (training)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => service.TrainAsync(image, false, outside, new(), default));
        }
        else
        {
            var model = ShapeTrainer.Train(SyntheticScene.Create(96, 96, new MatchPose(48, 48, 0)),
                new(ReadOnlyMemory<byte>.Empty, new(48, 48)), new());
            await Assert.ThrowsAsync<ArgumentException>(() => service.SearchAsync(image, false, model, outside, new(), default));
        }
        Assert.Equal(0, loader.Opened);
    }

    private sealed class DelayedService : IShapeMatchingService
    {
        public TaskCompletionSource<ShapeTrainingOutput> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ShapeTrainingOutput> TrainAsync(ImageReference image, bool live, RoiGeometry roi, TrainingOptions options, CancellationToken token, RoiExclusion? exclusion = null)
            => Completion.Task;
        public Task<ShapeSearchOutput> SearchAsync(ImageReference image, bool live, ShapeModel model, RoiGeometry? region, SearchOptions options, CancellationToken token, RoiExclusion? exclusion = null)
            => throw new NotSupportedException();
    }

    private sealed class Loader : IImageLoader
    {
        public int Opened;
        public int Disposed;
        private readonly GrayImage _image = SyntheticScene.Create(1536, 256, new MatchPose(80, 90, 0.1), new MatchPose(769.3, 140.7, -0.1));
        public Task<ITileSource> LoadAsync(ImageReference reference, IProgress<string> progress, CancellationToken cancellationToken)
        {
            Opened++;
            return Task.FromResult<ITileSource>(new Source(_image, () => Disposed++));
        }
    }

    private sealed class Source(GrayImage image, Action disposed) : ITileSource
    {
        public ImageInfo Info { get; } = new("fixture", "fixture", image.Width, image.Height, PixelKind.Gray8, 1);
        public PixelBuffer Read(TileKey key, CancellationToken cancellationToken)
        {
            Assert.Equal(0, key.Level);
            var output = new PixelBuffer(Math.Min(512, image.Width - (key.X * 512)), Math.Min(512, image.Height - (key.Y * 512)), PixelKind.Gray8);
            for (var y = 0; y < output.Height; y++)
            {
                image.Pixels.Span.Slice((((key.Y * 512) + y) * image.Stride) + (key.X * 512), output.Width).CopyTo(output.Span.Slice(y * output.Stride));
            }
            return output;
        }
        public void Dispose() => disposed();
    }
}
