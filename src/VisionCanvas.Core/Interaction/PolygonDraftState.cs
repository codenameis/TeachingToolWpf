using System.Collections.Immutable;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Core.Interaction;

/// <summary>폴리곤 그리기 초안의 꼭짓점·시작 이미지·닫힘 판정·오버레이 생성을 담당한다.</summary>
internal sealed class PolygonDraftState
{
    private const double ClosureDistanceDip = 8;
    private const double MinimumPointDistance = .000001;

    private readonly List<PointD> _points = [];
    private ImageReference? _startImage;

    public int Count => _points.Count;

    public bool HasPoints => _points.Count > 0;

    public bool CanComplete => _points.Count >= 3;

    public bool IsImageChanged(ImageReference? currentImage) => _points.Count > 0 && _startImage != currentImage;

    public void Clear()
    {
        _points.Clear();
        _startImage = null;
    }

    public void RemoveLastPoint()
    {
        if (_points.Count > 0)
        {
            _points.RemoveAt(_points.Count - 1);
        }
    }

    public PointD[]? TryAddPointOrClose(PointD screen, ViewportTransform viewport, ImageReference? image)
    {
        var imagePoint = RoiImageBounds.Clamp(viewport.ScreenToImage(screen), image);
        if (PolygonClosure.Find(_points, imagePoint) is { } closedPolygon)
        {
            return closedPolygon;
        }

        if (CanComplete && (viewport.ImageToScreen(_points[0]) - screen).Length < ClosureDistanceDip)
        {
            return BuildClosedPoints();
        }

        if (_points.Count == 0)
        {
            _startImage = image;
        }

        if (_points.Count == 0 || (_points[^1] - imagePoint).Length > MinimumPointDistance)
        {
            _points.Add(imagePoint);
        }

        return null;
    }

    public PointD[] BuildClosedPoints()
        => PolygonClosure.Find(_points, _points[0]) ?? _points.ToArray();

    public InteractionOverlay CreateOverlay(bool drawAsGroup, PointD? cursor = null)
    {
        if (cursor is { } point && PolygonClosure.Find(_points, point) is { Length: >= 3 } closed)
        {
            return new(PolygonPoints: closed.Append(closed[0]).ToImmutableArray())
            {
                DraftIsGroup = drawAsGroup
            };
        }

        return new(PolygonPoints: (cursor is { } end ? _points.Append(end) : _points).ToImmutableArray())
        {
            DraftIsGroup = drawAsGroup
        };
    }
}
