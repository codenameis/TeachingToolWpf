using System.Collections.Immutable;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

public static class MatchSuppression
{
    public static ImmutableArray<ShapeMatch> Apply(ShapeModel model, IEnumerable<ShapeMatch> matches, double maximumOverlap, int maximumMatches)
    {
        var result = ImmutableArray.CreateBuilder<ShapeMatch>();
        foreach (var match in matches.OrderByDescending(m => m.Score).ThenByDescending(m => m.Support)
            .ThenBy(m => double.IsNaN(m.Refinement.FinalResidual) ? double.PositiveInfinity : m.Refinement.FinalResidual))
        {
            if (result.All(existing => Overlap(model, match.Pose, existing.Pose) <= maximumOverlap))
            {
                result.Add(match);
                if (result.Count == maximumMatches)
                {
                    break;
                }
            }
        }
        return result.ToImmutable();
    }

    public static ImmutableArray<ImagePoint> TransformBounds(ShapeModel model, MatchPose pose)
    {
        var cos = Math.Cos(pose.Angle);
        var sin = Math.Sin(pose.Angle);
        return model.Bounds.Select(p => new ImagePoint(pose.X + (pose.Scale * ((cos * p.X) - (sin * p.Y))),
            pose.Y + (pose.Scale * ((sin * p.X) + (cos * p.Y))))).ToImmutableArray();
    }

    internal static double Overlap(ShapeModel model, MatchPose a, MatchPose b)
        => Overlap(model.Bounds, a, b);

    internal static double Overlap(ImmutableArray<ImagePoint> bounds, MatchPose a, MatchPose b)
    {
        var polygon = TransformBounds(bounds, a).ToList();
        var boundary = TransformBounds(bounds, b);
        var areaA = Area(polygon);
        var areaB = Area(boundary);
        for (var i = 0; i < boundary.Length && polygon.Count > 0; i++)
        {
            var start = boundary[i];
            var end = boundary[(i + 1) % 4];
            var input = polygon;
            polygon = [];
            var previous = input[^1];
            var previousDistance = Cross(start, end, previous);
            foreach (var current in input)
            {
                var distance = Cross(start, end, current);
                if ((distance >= 0) != (previousDistance >= 0))
                {
                    var t = previousDistance / (previousDistance - distance);
                    polygon.Add(new(previous.X + (t * (current.X - previous.X)), previous.Y + (t * (current.Y - previous.Y))));
                }
                if (distance >= 0)
                {
                    polygon.Add(current);
                }
                previous = current;
                previousDistance = distance;
            }
        }
        var intersection = Area(polygon);
        return intersection / Math.Max(1e-12, areaA + areaB - intersection);
    }

    private static ImmutableArray<ImagePoint> TransformBounds(ImmutableArray<ImagePoint> bounds, MatchPose pose)
    {
        var cos = Math.Cos(pose.Angle);
        var sin = Math.Sin(pose.Angle);
        return bounds.Select(point => new ImagePoint(pose.X + (pose.Scale * ((cos * point.X) - (sin * point.Y))),
            pose.Y + (pose.Scale * ((sin * point.X) + (cos * point.Y))))).ToImmutableArray();
    }

    private static double Cross(ImagePoint a, ImagePoint b, ImagePoint p)
        => ((b.X - a.X) * (p.Y - a.Y)) - ((b.Y - a.Y) * (p.X - a.X));

    private static double Area(IReadOnlyList<ImagePoint> polygon)
    {
        var sum = 0d;
        for (var i = 0; i < polygon.Count; i++)
        {
            var next = polygon[(i + 1) % polygon.Count];
            sum += (polygon[i].X * next.Y) - (next.X * polygon[i].Y);
        }
        return Math.Abs(sum) / 2;
    }
}
