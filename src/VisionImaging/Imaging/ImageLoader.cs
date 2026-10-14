using VisionImaging.Abstractions;

namespace VisionImaging;

public sealed class ImageLoader(string cacheDirectory) : IImageLoader
{
    private readonly SourceImageLoader _sources = new(new FileLoader(cacheDirectory));

    public Task<ITileSource> LoadAsync(ImageReference reference, IProgress<string> progress, CancellationToken cancellationToken)
        => _sources.LoadAsync(reference, progress, cancellationToken);

    private sealed class FileLoader(string directory) : IImageLoader
    {
        public Task<ITileSource> LoadAsync(ImageReference reference, IProgress<string> progress, CancellationToken cancellationToken)
        => Task.Run<ITileSource>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ImageFileVersion.Validate(reference);
            return DiskPyramidSource.Open(reference.Key, directory, progress, cancellationToken);
        }, cancellationToken);
    }
}
