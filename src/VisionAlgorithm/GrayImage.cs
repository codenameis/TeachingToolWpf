namespace VisionAlgorithm;

/// <summary>호출이 끝날 때까지 소유자가 변경/반환하지 않는 Gray8 픽셀 뷰. 좌표는 픽셀 경계 기준이다.</summary>
public sealed class GrayImage
{
    /// <summary>원본 좌표에서 제외한 픽셀은 255. 빈 메모리는 제외 없음이며 stride 없이 width×height이다.</summary>
    public ReadOnlyMemory<byte> ExcludedPixels
    {
        get;
    }

    public ReadOnlyMemory<byte> Pixels
    {
        get;
    }
    public int Width
    {
        get;
    }
    public int Height
    {
        get;
    }
    public int Stride
    {
        get;
    }

    public GrayImage(ReadOnlyMemory<byte> pixels, int width, int height, int stride, ReadOnlyMemory<byte> excludedPixels = default)
    {
        if (width < 1 || height < 1 || stride < width)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "양수 크기와 width 이상의 stride가 필요합니다.");
        }
        var length = checked(((height - 1) * stride) + width);
        if (pixels.Length < length)
        {
            throw new ArgumentException("픽셀 버퍼가 stride와 크기보다 작습니다.", nameof(pixels));
        }
        Pixels = pixels;
        if (!excludedPixels.IsEmpty && excludedPixels.Length != checked(width * height))
        {
            throw new ArgumentException("제외 마스크 크기가 영상과 다릅니다.", nameof(excludedPixels));
        }
        ExcludedPixels = excludedPixels;
        Width = width;
        Height = height;
        Stride = stride;
    }
}
