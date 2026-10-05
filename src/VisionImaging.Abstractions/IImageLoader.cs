namespace VisionImaging.Abstractions;

/// <summary>Creates an independently owned source. The caller transfers or disposes it after a successful load.</summary>
public interface IImageLoader
{
    Task<ITileSource> LoadAsync(ImageReference reference, IProgress<string> progress, CancellationToken cancellationToken);
}
