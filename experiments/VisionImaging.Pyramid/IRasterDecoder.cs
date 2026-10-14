using VisionImaging.Abstractions;

namespace VisionImaging.Pyramid;

public interface IRasterDecoder
{
    string CacheIdentity
    {
        get;
    }
    IRasterReader Open(string path, CancellationToken token);
}

/// <summary>연속 행을 tightly packed Gray8/Gray16(little-endian)/RGBA32로 복사한다. 호출자는 버퍼를 소유한다.</summary>
public interface IRasterReader : IDisposable
{
    int Width
    {
        get;
    }
    int Height
    {
        get;
    }
    PixelKind Kind
    {
        get;
    }
    void ReadRows(int top, int height, Span<byte> destination, CancellationToken token);
}
