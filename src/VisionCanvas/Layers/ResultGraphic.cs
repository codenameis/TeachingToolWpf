using VisionCanvas.Rendering;

namespace VisionCanvas.Layers;

/// <summary>기존 검사 표시 묶음. 판정은 호스트가 정하며 이 항목의 ID로 묶음 전체를 제거한다.</summary>
public sealed record ResultGraphic(ResultOverlay Result) : CanvasGraphic
{
    internal override void Validate(LayerSpace space)
    {
        ArgumentNullException.ThrowIfNull(Result);
        ArgumentNullException.ThrowIfNull(Result.Summary);
        if (space != LayerSpace.Image || Result.Matches.IsDefault)
        {
            throw new ArgumentException("검사 표시 묶음은 Image 레이어와 초기화된 결과 목록을 사용해야 합니다.");
        }
        CheckStyle(Result.Color);
        if (!Result.SearchBoundary.IsDefaultOrEmpty)
        {
            foreach (var point in Result.SearchBoundary)
            {
                CheckPoint(point);
            }
        }
        foreach (var marker in Result.Matches)
        {
            ArgumentNullException.ThrowIfNull(marker);
            ArgumentNullException.ThrowIfNull(marker.Label);
            CheckPoint(marker.ReferencePoint);
            CheckStyle(marker.Color);
            if (marker.Bounds.IsDefault || marker.Features.IsDefault || !double.IsFinite(marker.AngleRadians))
            {
                throw new ArgumentException("검사 표시의 윤곽·특징점·각도가 유효하지 않습니다.");
            }
            foreach (var point in marker.Bounds)
            {
                CheckPoint(point);
            }
            foreach (var point in marker.Features)
            {
                CheckPoint(point);
            }
        }
    }
}
