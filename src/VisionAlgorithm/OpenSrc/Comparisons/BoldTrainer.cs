using System.Collections.Immutable;
using VisionAlgorithm.OpenSrc.PatternMatching;

namespace VisionAlgorithm.OpenSrc.Comparisons;

public sealed record BoldTrainingOptions
{
    public float MinimumGradient { get; init; } = 8;
    public double MinimumSegmentLength { get; init; } = 5;
    public int MaximumSegments { get; init; } = 256;

    public void Validate()
    {
        if (!float.IsFinite(MinimumGradient) || MinimumGradient <= 0 ||
            !double.IsFinite(MinimumSegmentLength) || MinimumSegmentLength < 2 ||
            MaximumSegments is < 3 or > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(BoldTrainingOptions));
        }
    }
}

public static class BoldTrainer
{
    private const int MinimumRequiredSegments = 3;

    public static BoldModel Train(GrayImage image, TrainingRegion region, BoldTrainingOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        BoldInputValidation.ValidateRegion(image, region);
        BoldInputValidation.ValidateInputSize(image, BoldMatcher.MaximumInputPixels);
        cancellationToken.ThrowIfCancellationRequested();

        var gradients = GradientImage.Create(image, options.MinimumGradient, cancellationToken);
        var lines = LineSegments.Extract(gradients, cancellationToken)
            .Where(line => line.Length >= options.MinimumSegmentLength)
            .Where(line => BoldLineGeometry.IsInsideTrainingMask(line, image, region))
            .Select(line => BoldLineGeometry.TryCanonicalize(line, gradients, out var canonical)
                ? canonical
                : (LineSegment?)null)
            .Where(line => line.HasValue)
            .Select(line => line!.Value)
            .OrderByDescending(line => line.Length)
            .Take(options.MaximumSegments)
            .ToArray();
        if (lines.Length < MinimumRequiredSegments)
        {
            throw new ArgumentException("BOLD 등록에는 극성과 방향을 확인할 수 있는 유효 선분이 3개 이상 필요합니다.", nameof(region));
        }

        var cos = Math.Cos(region.ReferenceAngle);
        var sin = Math.Sin(region.ReferenceAngle);
        var features = ImmutableArray.CreateBuilder<BoldFeature>(lines.Length);
        for (var index = 0; index < lines.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var descriptor = BoldDescriptor.Compute(lines, index);
            if (descriptor.IsEmpty)
            {
                continue;
            }

            var local = lines[index];
            ImagePoint ToModelCoordinates(ImagePoint point)
            {
                var x = point.X - region.ReferencePoint.X;
                var y = point.Y - region.ReferencePoint.Y;
                return new((cos * x) + (sin * y), (-sin * x) + (cos * y));
            }

            features.Add(new(new(ToModelCoordinates(local.Start), ToModelCoordinates(local.End)), descriptor));
        }

        if (features.Count < MinimumRequiredSegments)
        {
            throw new ArgumentException("BOLD 선분의 이웃 descriptor를 만들 수 없습니다.", nameof(region));
        }

        return new(region.ReferencePoint, region.ReferenceAngle, options.MinimumGradient,
            BoldLineGeometry.CreateBounds(image, region, cancellationToken), features.ToImmutable());
    }
}
