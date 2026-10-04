using System.Collections.Immutable;
using VisionAlgorithm.OpenSrc.PatternMatching;

namespace VisionAlgorithm.OpenSrc.Comparisons;

internal static class BoldLineGeometry
{
    public static ImmutableArray<ImagePoint> CreateBounds(GrayImage image, TrainingRegion region,
        CancellationToken cancellationToken)
    {
        var left = 0;
        var top = 0;
        var right = image.Width;
        var bottom = image.Height;
        if (!region.Mask.IsEmpty)
        {
            left = image.Width;
            top = image.Height;
            right = 0;
            bottom = 0;
            for (var y = 0; y < image.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var x = 0; x < image.Width; x++)
                {
                    if (region.Mask.Span[(y * image.Width) + x] == 0)
                    {
                        continue;
                    }

                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x + 1);
                    bottom = Math.Max(bottom, y + 1);
                }
            }

            if (right == 0 || bottom == 0)
            {
                throw new ArgumentException("BOLD 학습 마스크에 유효한 픽셀이 없습니다.", nameof(region));
            }
        }

        var cos = Math.Cos(region.ReferenceAngle);
        var sin = Math.Sin(region.ReferenceAngle);
        ImagePoint ToModelCoordinates(double x, double y)
        {
            var relativeX = x - region.ReferencePoint.X;
            var relativeY = y - region.ReferencePoint.Y;
            return new((cos * relativeX) + (sin * relativeY), (-sin * relativeX) + (cos * relativeY));
        }

        return
        [
            ToModelCoordinates(left, top),
            ToModelCoordinates(right, top),
            ToModelCoordinates(right, bottom),
            ToModelCoordinates(left, bottom)
        ];
    }

    public static bool IsInsideTrainingMask(LineSegment segment, GrayImage image, TrainingRegion region)
    {
        if (region.Mask.IsEmpty)
        {
            return true;
        }

        var samples = Math.Max(2, (int)Math.Ceiling(segment.Length));
        for (var index = 0; index < samples; index++)
        {
            var fraction = index / (double)(samples - 1);
            var x = (int)Math.Floor(segment.Start.X + (fraction * (segment.End.X - segment.Start.X)));
            var y = (int)Math.Floor(segment.Start.Y + (fraction * (segment.End.Y - segment.Start.Y)));
            if (x < 0 || y < 0 || x >= image.Width || y >= image.Height ||
                region.Mask.Span[(y * image.Width) + x] == 0)
            {
                return false;
            }
        }

        return true;
    }

    public static bool TryCanonicalize(LineSegment line, GradientImage gradients, out LineSegment canonical)
    {
        var center = line.Middle;
        var centerX = (int)Math.Round(center.X - 0.5);
        var centerY = (int)Math.Round(center.Y - 0.5);
        EdgePoint? nearest = null;
        var nearestDistance = double.PositiveInfinity;
        for (var y = Math.Max(0, centerY - 2); y <= Math.Min(gradients.Height - 1, centerY + 2); y++)
        {
            for (var x = Math.Max(0, centerX - 2); x <= Math.Min(gradients.Width - 1, centerX + 2); x++)
            {
                if (gradients.EdgeAt(x, y) is not { } edge)
                {
                    continue;
                }

                var dx = edge.X - center.X;
                var dy = edge.Y - center.Y;
                var distance = (dx * dx) + (dy * dy);
                if (distance < nearestDistance)
                {
                    nearest = edge;
                    nearestDistance = distance;
                }
            }
        }

        if (nearest is null)
        {
            canonical = default;
            return false;
        }

        var directionX = line.End.X - line.Start.X;
        var directionY = line.End.Y - line.Start.Y;
        var polarity = (directionX * nearest.Value.NormalY) - (directionY * nearest.Value.NormalX);
        if (Math.Abs(polarity) <= 1e-9)
        {
            canonical = default;
            return false;
        }

        canonical = polarity > 0 ? line : new(line.End, line.Start);
        return true;
    }
}
