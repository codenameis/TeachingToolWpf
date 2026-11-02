using VisionCanvas.Core.Editing;
using VisionGeometry;

namespace VisionCanvas.Core.Interaction;

/// <summary>화면 좌표가 어떤 ROI를 잡았는지 계산한다. 제스처와 도구 상태는 모른다.</summary>
/// <remarks>
/// <para>입력은 문서와 화면 좌표뿐이라 단위 테스트로 규칙을 고정할 수 있다.</para>
/// <para>후보가 여럿이면 ①면적이 작은 것 ②나중에 만든 것 순으로 고른다.
/// Alt 순환 선택은 지금 선택된 다음 후보를 돌려준다.</para>
/// <para>미니맵 위에서는 일반 ROI를 잡지 않는다. 미니맵 드래그가 우선이기 때문이다.</para>
/// </remarks>
public readonly struct RoiHitTester
{
    /// <summary>화면 DIP 기준 클릭 허용 반경. 확대할수록 원본 픽셀 기준으로는 좁아진다.</summary>
    private const double HitToleranceDip = 3;

    private readonly DocumentState _state;
    private readonly bool _overMinimap;

    public RoiHitTester(DocumentState state, bool overMinimap)
    {
        _state = state;
        _overMinimap = overMinimap;
    }

    /// <summary>일반 ROI 하나를 찾는다. 없으면 null.</summary>
    /// <param name="cycleSelection">true면 겹친 후보를 순환하는 순서로 돌려준다(Alt+클릭).</param>
    public Guid? HitOrdinary(PointD screen, bool cycleSelection = false)
    {
        if (_overMinimap)
        {
            return null;
        }

        var viewport = _state.Viewport;
        var imagePoint = viewport.ScreenToImage(screen);
        var hitToleranceInImage = HitToleranceDip / viewport.Scale;
        if (!cycleSelection)
        {
            return _state.Snapshot.Spatial.HitTest(imagePoint, hitToleranceInImage)?.Id;
        }

        // 회전 사각형은 경계 상자보다 넓게 잡히므로 대각선만큼 넓게 후보를 모은 뒤 실제 도형으로 거른다.
        var searchMarginInImage = hitToleranceInImage * Math.Sqrt(2);
        var hitRoiIds = _state.Snapshot.Spatial.Intersecting(new(
                imagePoint.X - searchMarginInImage,
                imagePoint.Y - searchMarginInImage,
                imagePoint.X + searchMarginInImage,
                imagePoint.Y + searchMarginInImage))
            .Where(r => r.Geometry.Contains(imagePoint, hitToleranceInImage))
            .OrderBy(r => r.Geometry.Area)
            .ThenByDescending(r => r.Order)
            .Select(r => r.Id)
            .ToArray();
        if (hitRoiIds.Length == 0)
        {
            return null;
        }

        if (_state.Selection.TryGetOnly(out var selected))
        {
            var selectedIndex = Array.IndexOf(hitRoiIds, selected);
            return hitRoiIds[(selectedIndex + 1) % hitRoiIds.Length];
        }

        return hitRoiIds[0];
    }
}
