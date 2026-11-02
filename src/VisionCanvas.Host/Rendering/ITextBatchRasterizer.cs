using System.Collections.Immutable;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Rendering;

public sealed record TextRasterBatch(PixelBuffer Pixels, ImmutableDictionary<string, BoundsD> Regions) : IDisposable
{
    public void Dispose() => Pixels.Dispose();
}

/// <summary>문구·번호를 생략하지 않는 제한된 크기의 텍스트 atlas. 초과 시 기존 단일 라벨 경로를 사용한다.</summary>
public interface ITextBatchRasterizer : ITextRasterizer
{
    TextRasterBatch? RasterizeBatch(IEnumerable<string> texts, double dpi);
}
