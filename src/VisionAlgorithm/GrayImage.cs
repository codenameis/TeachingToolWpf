namespace VisionAlgorithm;

/// <summary>호출이 끝날 때까지 소유자가 변경/반환하지 않는 Gray8 픽셀 뷰. 좌표는 픽셀 경계 기준이다.</summary>
public sealed class GrayImage
{
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

    public GrayImage(ReadOnlyMemory<byte> pixels, int width, int height, int stride)
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
        Width = width;
        Height = height;
        Stride = stride;
    }
}
