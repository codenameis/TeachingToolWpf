using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Imaging;

public static class TilePlanner
{
    /// <summary>뷰포트에 필요한 타일 주소를 행 우선 순서로 구한다. 픽셀 읽기와 캐시 관리는 하지 않는다.</summary>
    public static IReadOnlyList<TileKey> Visible(ImageInfo image, ViewportTransform view, double width, double height, double dpi)
    {
        if (width <= 0 || height <= 0 || view.Scale <= 0 || dpi <= 0)
        {
            return [];
        }

        // Scale은 DIP/원본 픽셀, dpi는 물리 픽셀/DIP다. 곱한 값으로 표시 해상도를 고른다.
        var level = Math.Clamp((int)Math.Floor(Math.Log2(1 / (view.Scale * dpi))), 0, image.Levels - 1);
        var downsampleFactor = 1 << level;
        var tileSpanInImage = (double)ImageInfo.TileSize * downsampleFactor;
        var imageTopLeft = view.ScreenToImage(new PointD(0, 0));
        var imageBottomRight = view.ScreenToImage(new PointD(width, height));
        var columnCount = (int)Math.Ceiling(image.Width / tileSpanInImage);
        var rowCount = (int)Math.Ceiling(image.Height / tileSpanInImage);

        // 시작은 포함하고 끝은 제외한다. 이미지 밖 여백에는 타일을 요청하지 않는다.
        var firstColumn = Math.Clamp((int)Math.Floor(imageTopLeft.X / tileSpanInImage), 0, columnCount);
        var firstRow = Math.Clamp((int)Math.Floor(imageTopLeft.Y / tileSpanInImage), 0, rowCount);
        var columnEnd = Math.Clamp((int)Math.Ceiling(imageBottomRight.X / tileSpanInImage), 0, columnCount);
        var rowEnd = Math.Clamp((int)Math.Ceiling(imageBottomRight.Y / tileSpanInImage), 0, rowCount);
        var result = new List<TileKey>();
        for (var row = firstRow; row < rowEnd; row++)
        {
            for (var column = firstColumn; column < columnEnd; column++)
            {
                result.Add(new(level, column, row));
            }
        }

        return result;
    }
}
