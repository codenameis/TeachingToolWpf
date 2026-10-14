using VisionImaging.Abstractions;

namespace VisionImaging.Pyramid;

/// <summary>파일 디코더를 주입받아 기존 Canvas/검사 계약에 독립 원본 reader를 제공한다.</summary>
public sealed class RawImageLoader(string cacheDirectory, IRasterDecoder decoder,
    long cacheBudgetBytes = 32L * 1024 * 1024 * 1024) : IImageLoader
{
    public Task<ITileSource> LoadAsync(ImageReference reference, IProgress<string> progress, CancellationToken cancellationToken)
        => Task.Run<ITileSource>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ImageFileVersion.Validate(reference);
            var source = RawPyramidSource.Open(reference.Key, cacheDirectory, decoder, progress, cancellationToken, cacheBudgetBytes);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                ImageFileVersion.Validate(reference);
                return source;
            }
            catch
            {
                source.Dispose();
                throw;
            }
        }, cancellationToken);
}
