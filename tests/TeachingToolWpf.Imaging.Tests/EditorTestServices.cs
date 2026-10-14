using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.ViewModels;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Tests;

internal static class EditorTestServices
{
    internal static InspectionToolsViewModel Inspections()
    {
        var loader = new UnusedLoader();
        return new(new(new ShapeMatchingService(loader, () => null, pipeline: ShapeSearchPipeline.Paper),
            new TemplateMatchingService(loader, () => null)), new(new PrimitiveShapeDetectionService(loader, () => null)));
    }

    private sealed class UnusedLoader : IImageLoader
    {
        public Task<ITileSource> LoadAsync(ImageReference image, IProgress<string> progress, CancellationToken token)
            => throw new NotSupportedException("This editor test does not load inspection images.");
    }
}
