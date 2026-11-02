using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Core.Geometry;

public sealed partial class RoiSpatialIndex
{
    /// <summary>Exact rotated rectangle union bounds, using four branch-and-bound support queries.</summary>
    public BoundsD RotatedBounds(PointD pivot, double angleDegrees)
    {
        if (!pivot.IsFinite)
        {
            throw new ArgumentOutOfRangeException(nameof(pivot));
        }

        if (!double.IsFinite(angleDegrees))
        {
            throw new ArgumentOutOfRangeException(nameof(angleDegrees));
        }

        if (_root.Count == 0)
        {
            return default;
        }

        var angle = angleDegrees % 360;
        if (angle == 0)
        {
            return _root.Bounds;
        }

        var (sin, cos) = Math.SinCos(angle * Math.PI / 180);
        return new(pivot.X - Support(pivot, new(-cos, sin)), pivot.Y - Support(pivot, new(-sin, -cos)),
            pivot.X + Support(pivot, new(cos, -sin)), pivot.Y + Support(pivot, new(sin, cos)));
    }

    private double Support(PointD pivot, PointD direction)
    {
        var best = double.NegativeInfinity;
        Search(_root, ProjectionUpperBound(_root.Bounds, pivot, direction));
        return best;

        void Search(Node node, double upperBound)
        {
            if (node.Count == 0 || upperBound <= best)
            {
                return;
            }

            if (node.Entries is { } entries)
            {
                foreach (var entry in entries)
                {
                    var rectangle = entry.Roi.Geometry;
                    var projection = rectangle.Support(direction) - (pivot.X * direction.X) - (pivot.Y * direction.Y);
                    best = Math.Max(best, projection);
                }
                return;
            }
            var left = node.Left!;
            var right = node.Right!;
            var leftBound = ProjectionUpperBound(left.Bounds, pivot, direction);
            var rightBound = ProjectionUpperBound(right.Bounds, pivot, direction);
            if (leftBound >= rightBound)
            {
                Search(left, leftBound);
                Search(right, rightBound);
            }
            else
            {
                Search(right, rightBound);
                Search(left, leftBound);
            }
        }
    }

    private static double ProjectionUpperBound(BoundsD bounds, PointD pivot, PointD direction)
    {
        var x = direction.X >= 0 ? bounds.Right : bounds.Left;
        var y = direction.Y >= 0 ? bounds.Bottom : bounds.Top;
        var projection = ((x - pivot.X) * direction.X) + ((y - pivot.Y) * direction.Y);
        // Only the pruning bound is expanded; the returned support still uses the actual rectangle.
        return projection + CoordinateRoundoff(bounds, pivot);
    }

    private static double CoordinateRoundoff(BoundsD bounds, PointD pivot)
        => 32 * 2.2204460492503131e-16 * Math.Max(1,
            Math.Max(Math.Max(Math.Abs(bounds.Left), Math.Abs(bounds.Right)),
                Math.Max(Math.Max(Math.Abs(bounds.Top), Math.Abs(bounds.Bottom)), Math.Max(Math.Abs(pivot.X), Math.Abs(pivot.Y)))));

    /// <summary>
    /// First image-boundary exit in each direction. Compute once per immutable selection/pivot;
    /// later drag samples can clamp without visiting ROI. Worst-case preparation remains O(N).
    /// </summary>
    /// <summary>선택 영역을 <paramref name="pivot"/> 주위로 돌릴 때 이미지 안에 남는 최대 회전 각도(도)를 양방향으로 구한다.</summary>
    /// <remarks>
    /// 읽는 순서는 세 단계다.
    /// <para>1. <c>BoundsExits</c>가 BVH 노드의 경계 상자만 보고 "이 각도면 이미 나간다"는 하한을 만든다. 하한이 현재 최선보다 나쁜 가지는 펼치지 않는다.</para>
    /// <para>2. 잎에서는 각 ROI의 바깥 점(원은 중심+반지름, 사각형은 네 모서리, 그 외는 윤곽)이 이미지 경계를 벗어나는 각도를 <c>IncludePoint</c>로 모아 가장 이른 값만 남긴다.
    /// 점 하나는 <c>a·cosθ + b·sinθ</c>를 따르므로 벗어나는 각도는 <c>phase ± acos(limit/radius)</c>로 계산된다.</para>
    /// <para>3. 해석해는 등호 조건이라 반올림에 약하다. <c>SafeEndpoint</c>가 실제 포함 여부를 확인하고, 아니면 48회 이분법으로 안쪽 값을 찾는다.</para>
    /// 결과는 드래그 제한에만 쓰고 문서·이력을 바꾸지 않는다. 양수 방향과 음수 방향을 따로 돌려준다.
    /// </remarks>
    public RotationRange RotationLimits(PointD pivot, ImageReference image, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!pivot.IsFinite)
        {
            throw new ArgumentOutOfRangeException(nameof(pivot));
        }

        ArgumentNullException.ThrowIfNull(image);
        if (image.Width <= 0 || image.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(image));
        }

        if (_root.Count == 0)
        {
            return RotationRange.Unbounded;
        }

        var limits = new BoundsD(-1e-7 - pivot.X, -1e-7 - pivot.Y,
            image.Width + 1e-7 - pivot.X, image.Height + 1e-7 - pivot.Y);
        var best = RotationExits.Unbounded;
        Search(_root, BoundsExits(_root.Bounds));
        return new(SafeEndpoint(best.Counterclockwise, -1), SafeEndpoint(best.Clockwise, 1));

        double SafeEndpoint(double radians, int direction)
        {
            if (double.IsPositiveInfinity(radians))
            {
                return direction * double.PositiveInfinity;
            }
            // The analytic root is an equality. Keep the published rectangle on its inside even when
            // angle normalization, SinCos, and final world-coordinate additions round differently.
            var degrees = Math.Max(0, (radians * 180 / Math.PI) - 1e-10);
            if (degrees == 0 || FitsWithRoundoff(degrees * direction))
            {
                return degrees * direction;
            }

            var inside = 0d;
            var outside = degrees;
            for (var iteration = 0; iteration < 48; iteration++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var middle = (inside + outside) / 2;
                if (FitsWithRoundoff(middle * direction))
                {
                    inside = middle;
                }
                else
                {
                    outside = middle;
                }
            }
            return inside * direction;
        }

        bool FitsWithRoundoff(double degrees)
        {
            var bounds = RotatedBounds(pivot, degrees);
            var error = CoordinateRoundoff(bounds, pivot);
            return bounds.Left >= -1e-7 + error && bounds.Top >= -1e-7 + error
                && bounds.Right <= image.Width + 1e-7 - error && bounds.Bottom <= image.Height + 1e-7 - error;
        }

        RotationExits BoundsExits(BoundsD bounds)
        {
            // An outward-rounded AABB contains every descendant corner, so its first exit is a lower bound.
            var margin = CoordinateRoundoff(bounds, pivot);
            var left = bounds.Left - pivot.X - margin;
            var right = bounds.Right - pivot.X + margin;
            var top = bounds.Top - pivot.Y - margin;
            var bottom = bounds.Bottom - pivot.Y + margin;
            var exits = RotationExits.Unbounded;
            IncludePoint(ref exits, new(left, top), limits);
            IncludePoint(ref exits, new(right, top), limits);
            IncludePoint(ref exits, new(right, bottom), limits);
            IncludePoint(ref exits, new(left, bottom), limits);
            return exits;
        }

        void Search(Node node, RotationExits lowerBound)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node.Count == 0 || (lowerBound.Clockwise >= best.Clockwise && lowerBound.Counterclockwise >= best.Counterclockwise))
            {
                return;
            }

            if (node.Entries is { } entries)
            {
                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var rectangle = entry.Roi.Geometry;
                    var center = rectangle.Center - pivot;
                    if (rectangle.Radius > 0)
                    {
                        var radius = rectangle.Radius;
                        IncludePoint(ref best, center, new(limits.Left + radius, limits.Top + radius, limits.Right - radius, limits.Bottom - radius));
                        continue;
                    }
                    if (rectangle.Shape.Kind != RoiShapeKind.Rectangle)
                    {
                        foreach (var point in rectangle.Outline)
                        {
                            IncludePoint(ref best, point - pivot, limits);
                        }

                        continue;
                    }
                    var (sin, cos) = Math.SinCos(rectangle.Angle * Math.PI / 180);
                    var horizontal = new PointD(cos * (rectangle.Width / 2), sin * (rectangle.Width / 2));
                    var vertical = new PointD(-sin * (rectangle.Height / 2), cos * (rectangle.Height / 2));
                    IncludePoint(ref best, center - horizontal - vertical, limits);
                    IncludePoint(ref best, center + horizontal - vertical, limits);
                    IncludePoint(ref best, center + horizontal + vertical, limits);
                    IncludePoint(ref best, center - horizontal + vertical, limits);
                }
                return;
            }
            var left = node.Left!;
            var right = node.Right!;
            var leftBound = BoundsExits(left.Bounds);
            var rightBound = BoundsExits(right.Bounds);
            if (leftBound.Earliest <= rightBound.Earliest)
            {
                Search(left, leftBound);
                Search(right, rightBound);
            }
            else
            {
                Search(right, rightBound);
                Search(left, leftBound);
            }
        }
    }

    private readonly record struct RotationExits(double Clockwise, double Counterclockwise)
    {
        public static RotationExits Unbounded => new(double.PositiveInfinity, double.PositiveInfinity);

        public double Earliest => Math.Min(Clockwise, Counterclockwise);
    }

    private static void IncludePoint(ref RotationExits exits, PointD point, BoundsD limits)
    {
        IncludeEdge(ref exits, point.X, -point.Y, limits.Right);
        IncludeEdge(ref exits, -point.X, point.Y, -limits.Left);
        IncludeEdge(ref exits, point.Y, point.X, limits.Bottom);
        IncludeEdge(ref exits, -point.Y, -point.X, -limits.Top);
    }

    private static void IncludeEdge(ref RotationExits exits, double a, double b, double limit)
    {
        // A corner follows a*cos(theta)+b*sin(theta). The forbidden arc starts at phase-acos(limit/radius).
        var radius = double.Hypot(a, b);
        if (radius <= limit)
        {
            return; // Tangency alone never leaves the image.
        }

        if (a > limit || limit <= -radius)
        {
            exits = new(0, 0);
            return;
        }
        var phase = Math.Atan2(b, a);
        var halfArc = Math.Acos(Math.Clamp(limit / radius, -1, 1));
        var clockwise = a == limit && b >= 0 ? 0 : PositiveAngle(phase - halfArc);
        var counterclockwise = a == limit && b <= 0 ? 0 : PositiveAngle(-phase - halfArc);
        exits = new(Math.Min(exits.Clockwise, clockwise), Math.Min(exits.Counterclockwise, counterclockwise));
    }

    private static double PositiveAngle(double radians)
    {
        var result = radians % (2 * Math.PI);
        return result < 0 ? result + (2 * Math.PI) : result;
    }
}
