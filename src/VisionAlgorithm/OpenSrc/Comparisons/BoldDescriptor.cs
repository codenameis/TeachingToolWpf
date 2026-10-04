using System.Collections.Immutable;
using VisionAlgorithm.OpenSrc.PatternMatching;

namespace VisionAlgorithm.OpenSrc.Comparisons;

internal static class BoldDescriptor
{
    private const int BinsPerDimension = 12;
    private const double FullTurn = 2 * Math.PI;
    private static readonly int[] s_bunchSizes = [5, 10, 15, 20];

    public static ImmutableArray<float> Compute(IReadOnlyList<LineSegment> segments, int centerIndex)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (centerIndex < 0 || centerIndex >= segments.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(centerIndex));
        }

        var center = segments[centerIndex];
        if (center.Length <= 1e-9)
        {
            return [];
        }

        var neighbors = Enumerable.Range(0, segments.Count)
            .Where(index => index != centerIndex && segments[index].Length > 1e-9)
            .Select(index => new Neighbor(index, segments[index], DistanceSquared(center.Middle, segments[index].Middle)))
            .OrderBy(neighbor => neighbor.DistanceSquared)
            .ThenBy(neighbor => neighbor.Index)
            .ToArray();
        if (neighbors.Length == 0)
        {
            return [];
        }

        var descriptor = new float[s_bunchSizes.Length * BinsPerDimension * BinsPerDimension];
        var activeBunches = 0;
        for (var bunchIndex = 0; bunchIndex < s_bunchSizes.Length; bunchIndex++)
        {
            var neighborCount = Math.Min(s_bunchSizes[bunchIndex], neighbors.Length);
            var histogram = new double[BinsPerDimension * BinsPerDimension];
            for (var neighborIndex = 0; neighborIndex < neighborCount; neighborIndex++)
            {
                AddPair(histogram, center, neighbors[neighborIndex].Segment);
            }

            var magnitude = Math.Sqrt(histogram.Sum(value => value * value));
            if (magnitude <= 1e-12)
            {
                continue;
            }

            activeBunches++;
            var offset = bunchIndex * histogram.Length;
            for (var bin = 0; bin < histogram.Length; bin++)
            {
                descriptor[offset + bin] = (float)(histogram[bin] / magnitude);
            }
        }

        if (activeBunches == 0)
        {
            return [];
        }

        var normalization = Math.Sqrt(descriptor.Sum(value => (double)value * value));
        if (normalization <= 1e-12)
        {
            return [];
        }

        for (var index = 0; index < descriptor.Length; index++)
        {
            descriptor[index] = (float)(descriptor[index] / normalization);
        }

        return descriptor.ToImmutableArray();
    }

    private static void AddPair(double[] histogram, LineSegment center, LineSegment neighbor)
    {
        var displacementX = neighbor.Middle.X - center.Middle.X;
        var displacementY = neighbor.Middle.Y - center.Middle.Y;
        if ((displacementX * displacementX) + (displacementY * displacementY) <= 1e-12)
        {
            return;
        }

        var displacementAngle = Math.Atan2(displacementY, displacementX);
        var alpha = NormalizeAngle(displacementAngle - center.Angle);
        var beta = NormalizeAngle(displacementAngle + Math.PI - neighbor.Angle);
        AddBilinear(histogram, alpha, beta);
    }

    private static void AddBilinear(double[] histogram, double alpha, double beta)
    {
        var alphaPosition = alpha * BinsPerDimension / FullTurn;
        var betaPosition = beta * BinsPerDimension / FullTurn;
        var alphaFloor = (int)Math.Floor(alphaPosition);
        var betaFloor = (int)Math.Floor(betaPosition);
        var alphaLower = alphaFloor % BinsPerDimension;
        var betaLower = betaFloor % BinsPerDimension;
        var alphaUpper = (alphaLower + 1) % BinsPerDimension;
        var betaUpper = (betaLower + 1) % BinsPerDimension;
        var alphaWeight = alphaPosition - alphaFloor;
        var betaWeight = betaPosition - betaFloor;

        AddBin(histogram, alphaLower, betaLower, (1 - alphaWeight) * (1 - betaWeight));
        AddBin(histogram, alphaLower, betaUpper, (1 - alphaWeight) * betaWeight);
        AddBin(histogram, alphaUpper, betaLower, alphaWeight * (1 - betaWeight));
        AddBin(histogram, alphaUpper, betaUpper, alphaWeight * betaWeight);
    }

    private static void AddBin(double[] histogram, int alphaBin, int betaBin, double weight)
        => histogram[(alphaBin * BinsPerDimension) + betaBin] += weight;

    private static double NormalizeAngle(double angle)
    {
        var normalized = angle % FullTurn;
        return normalized < 0 ? normalized + FullTurn : normalized;
    }

    private static double DistanceSquared(ImagePoint first, ImagePoint second)
        => ((first.X - second.X) * (first.X - second.X)) + ((first.Y - second.Y) * (first.Y - second.Y));

    private readonly record struct Neighbor(int Index, LineSegment Segment, double DistanceSquared);
}
