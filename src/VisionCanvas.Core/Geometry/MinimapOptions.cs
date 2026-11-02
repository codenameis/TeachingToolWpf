namespace VisionCanvas.Core.Geometry;

/// <summary>좌상단부터 시계 방향으로 정렬한 화면 가장자리 배치.</summary>
public enum MinimapPosition
{
    TopLeft,
    TopCenter,
    TopRight,
    MiddleRight,
    BottomRight,
    BottomCenter,
    BottomLeft,
    MiddleLeft
}

/// <summary>표시와 입력이 공유하는 미니맵 설정. 문서와 편집 이력에는 포함하지 않는다.</summary>
public sealed record MinimapOptions(bool IsVisible = true, MinimapPosition Position = MinimapPosition.BottomRight)
{
    public static MinimapOptions Default { get; } = new();

    public void Validate()
    {
        if (!Enum.IsDefined(Position))
        {
            throw new ArgumentOutOfRangeException(nameof(Position));
        }
    }
}
