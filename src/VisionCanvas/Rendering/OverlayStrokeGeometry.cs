using TeachingToolWpf.Core.Geometry;

namespace TeachingToolWpf.Rendering;

internal readonly record struct OverlayStrokeSegment(PointD Start, PointD End);

/// <summary>화면 DIP의 닫힌 윤곽을 자른 뒤 획을 생성해 화면 밖 길이만큼 점선을 만들지 않는다.</summary>
internal static class OverlayStrokeGeometry
{
    public static IEnumerable<OverlayStrokeSegment> ClosedOutline(IReadOnlyList<PointD> points, BoundsD clip,
        double dashLength = 0, double gapLength = 0)
    {
        var cycle = dashLength + gapLength;
        var dashed = dashLength > 0 && gapLength > 0;
        var phase = 0d;
        for (var index = 0; index < points.Count; index++)
        {
            var start = points[index];
            var delta = points[(index + 1) % points.Count] - start;
            var length = delta.Length;
            if (!start.IsFinite || !double.IsFinite(length) || length < .001)
            {
                continue;
            }
            if (TryClip(start, delta, clip, out var entry, out var exit))
            {
                var direction = delta / length;
                var clippedStart = start + (delta * entry);
                var visibleLength = (exit - entry) * length;
                if (!dashed)
                {
                    yield return new(clippedStart, start + (delta * exit));
                }
                else
                {
                    var clippedPhase = (phase + (entry * length % cycle)) % cycle;
                    for (var distance = -clippedPhase; distance < visibleLength; distance += cycle)
                    {
                        var from = Math.Max(0, distance);
                        var to = Math.Min(visibleLength, distance + dashLength);
                        if (to > from)
                        {
                            yield return new(clippedStart + (direction * from), clippedStart + (direction * to));
                        }
                    }
                }
            }
            if (dashed)
            {
                // 화면 밖으로 나간 구간과 꼭짓점에서도 점선의 위상을 연속으로 유지한다.
                phase = (phase + (length % cycle)) % cycle;
            }
        }
    }

    private static bool TryClip(PointD start, PointD delta, BoundsD clip, out double entry, out double exit)
    {
        entry = 0;
        exit = 1;
        return ClipAxis(start.X, delta.X, clip.Left, clip.Right, ref entry, ref exit) &&
            ClipAxis(start.Y, delta.Y, clip.Top, clip.Bottom, ref entry, ref exit) && exit > entry;
    }

    private static bool ClipAxis(double origin, double direction, double minimum, double maximum, ref double entry, ref double exit)
    {
        if (direction == 0)
        {
            return origin >= minimum && origin <= maximum;
        }
        var first = (minimum - origin) / direction;
        var second = (maximum - origin) / direction;
        entry = Math.Max(entry, Math.Min(first, second));
        exit = Math.Min(exit, Math.Max(first, second));
        return exit >= entry;
    }
}
