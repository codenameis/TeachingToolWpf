namespace CpuMatching.Core;

/// <summary>원본 전체를 연속 메모리에 올리지 않는 읽기 계약. 소유권과 동시 읽기 직렬화는 공급자가 맡는다.</summary>
public interface IGrayImageSource
{
    int Width
    {
        get;
    }
    int Height
    {
        get;
    }

    /// <summary>
    /// 원본 region을 읽고 1, 2, 4, 8, 16 배 축소한다. 각 2×2 단계마다 (합+2)/4로 반올림하며
    /// region 원점에서 시작한다. 반환 영상은 독립 소유 버퍼이고 크기는 region 크기/reduction이다.
    /// </summary>
    GrayImage Read(SearchRegion region, int reduction, CancellationToken token);
}

internal sealed class SearchImage(int width, int height, Func<SearchRegion, GrayImage> read)
{
    public SearchImage(GrayImage image) : this(image.Width, image.Height, image.Crop) { }
    public int Width { get; } = width;
    public int Height { get; } = height;
    public GrayImage Crop(SearchRegion region) => read(region);
    public GrayImage ReadAll() => Crop(new(0, 0, Width, Height));
}
