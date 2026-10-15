using VisionImaging;
using VisionImaging.Abstractions;

namespace VisionCanvas.MvvmSample.Services;

// 예제 카메라 식별자만 처리하고, 실제 파일과 기존 sample: 요청은 주입받은 로더에 맡긴다.
// 새 코덱이나 카메라 SDK 없이 IImageLoader를 대체할 수 있음을 보여 주는 작은 어댑터다.
public sealed class DemoImageLoader(IImageLoader files) : IImageLoader
{
    public Task<ITileSource> LoadAsync(ImageReference reference, IProgress<string> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return reference.Key is "demo:camera:1" or "demo:camera:2"
            ? Task.FromResult<ITileSource>(new CameraSource(reference))
            : files.LoadAsync(reference, progress, cancellationToken);
    }

    private sealed class CameraSource(ImageReference image) : ITileSource
    {
        private readonly ProceduralTileSource _source = new(image.Width, image.Height);
        public ImageInfo Info { get; } = new(image.Key, image.Name, image.Width, image.Height,
            PixelKind.Rgba32, ImageInfo.CalculateLevelCount(image.Width, image.Height));

        public PixelBuffer Read(TileKey key, CancellationToken cancellationToken)
        {
            var pixels = _source.Read(key, cancellationToken);
            try
            {
                // 카메라 2는 청색 계열의 다른 합성 화면으로 구별한다.
                // 전체 8K 배열을 만들지 않고 요청된 타일만 바꾼다. 실제 카메라 영상은 아니다.
                if (image.Key == "demo:camera:2")
                {
                    for (var y = 0; y < pixels.Height; y++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var row = pixels.Span.Slice(y * pixels.Stride, pixels.Width * 4);
                        for (var x = 0; x < row.Length; x += 4)
                        {
                            (row[x + 1], row[x + 2]) = (row[x + 2], row[x + 1]);
                        }
                    }
                }
                return pixels;
            }
            catch
            {
                pixels.Dispose();
                throw;
            }
        }

        public void Dispose() => _source.Dispose();
    }
}
