using VisionImaging.Abstractions;

namespace VisionCanvas.Manual;

/// <summary>8K 전체 배열 없이 요청한 타일만 만드는 교육용 소스. 실제 카메라/파일 검증용이 아니다.</summary>
internal sealed class CheckerTileSource : ITileSource
{
    private bool _disposed;

    public ImageInfo Info
    {
        get;
    } = new("manual-checker", "8K 체커 학습 영상", 8192, 8192,
        PixelKind.Gray8, ImageInfo.CalculateLevelCount(8192, 8192));

    public PixelBuffer Read(TileKey key, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (key.Level < 0 || key.Level >= Info.Levels)
        {
            throw new ArgumentOutOfRangeException(nameof(key));
        }

        var factor = 1 << key.Level;
        var tileCount = Info.Width / (ImageInfo.TileSize * factor);
        if (key.X < 0 || key.X >= tileCount || key.Y < 0 || key.Y >= tileCount)
        {
            throw new ArgumentOutOfRangeException(nameof(key));
        }

        var pixels = new PixelBuffer(ImageInfo.TileSize, ImageInfo.TileSize, PixelKind.Gray8);
        try
        {
            for (var y = 0; y < pixels.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var imageY = ((key.Y * ImageInfo.TileSize) + y) * factor;
                for (var x = 0; x < pixels.Width; x++)
                {
                    var imageX = ((key.X * ImageInfo.TileSize) + x) * factor;
                    // 256px 체커 경계는 이 고정 영상의 모든 피라미드 단계 경계에 정렬된다.
                    var isLight = ((imageX / 256) + (imageY / 256)) % 2 == 0;
                    pixels.Bytes[(y * pixels.Stride) + x] = isLight ? (byte)192 : (byte)64;
                }
            }
            return pixels; // 호출자에게 소유권을 넘긴다.
        }
        catch
        {
            pixels.Dispose(); // 취소/실패에서는 이전할 호출자가 없으므로 여기서 반환한다.
            throw;
        }
    }

    public void Dispose() => _disposed = true;
}
