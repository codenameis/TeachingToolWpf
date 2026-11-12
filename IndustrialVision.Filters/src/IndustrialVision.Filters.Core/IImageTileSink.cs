namespace IndustrialVision.Filters.Core;

/// <summary>타일은 Write 동안만 유효하다. 실패/취소 시 부분 결과를 완료로 게시하지 않는다.</summary>
public interface IImageTileSink
{
    void Write(int x, int y, ImageView tile, CancellationToken token);
    void Complete();
    void Abort();
}
