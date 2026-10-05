using System.IO;
using VisionImaging.Abstractions;

namespace VisionImaging;

public sealed class ImageLoader(string cacheDirectory) : IImageLoader
{
    public Task<ITileSource> LoadAsync(ImageReference reference, IProgress<string> progress, CancellationToken cancellationToken)
        => Task.Run<ITileSource>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (LiveImageSource.TryKind(reference, out _))
            {
                return new LiveImageSource(reference);
            }

            if (reference.Key.StartsWith("sample:", StringComparison.Ordinal))
            {
                return new ProceduralTileSource(reference.Width, reference.Height);
            }

            if (!File.Exists(reference.Key))
            {
                throw new FileNotFoundException("원본 이미지를 찾을 수 없습니다. 현재 이미지와 편집 이력을 유지합니다.", reference.Key);
            }

            if (reference.Version != "" && DiskPyramidSource.Version(reference.Key) != reference.Version)
            {
                throw new IOException("원본 파일이 변경되어 이력의 이미지를 복원할 수 없습니다.");
            }

            return DiskPyramidSource.Open(reference.Key, cacheDirectory, progress, cancellationToken);
        }, cancellationToken);
}
