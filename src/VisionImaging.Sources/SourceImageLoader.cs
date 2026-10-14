using VisionImaging.Abstractions;

namespace VisionImaging;

/// <summary>생성/라이브 식별은 코덱을 호출하지 않고 파일 요청만 주입된 로더로 전달한다.</summary>
public sealed class SourceImageLoader(IImageLoader fileLoader) : IImageLoader
{
    public Task<ITileSource> LoadAsync(ImageReference reference, IProgress<string> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (LiveImageSource.TryKind(reference, out _))
        {
            return Task.FromResult<ITileSource>(new LiveImageSource(reference));
        }
        if (reference.Key.StartsWith("sample:", StringComparison.Ordinal))
        {
            return Task.FromResult<ITileSource>(new ProceduralTileSource(reference.Width, reference.Height));
        }
        return fileLoader.LoadAsync(reference, progress, cancellationToken);
    }
}
