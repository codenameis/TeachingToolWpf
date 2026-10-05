using TeachingToolWpf.Core.Imaging;

namespace TeachingToolWpf.Imaging;

/// <summary>원본 RGBA/BGRA의 (77R+150G+29B+128)/256과 동일한 Gray8을 직접 공급하는 선택 계약.</summary>
public interface IOriginalGrayTileSource
{
    PixelBuffer ReadOriginalGray8(int tileX, int tileY, CancellationToken cancellationToken);
}
