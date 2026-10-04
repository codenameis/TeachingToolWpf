using System.Collections.Immutable;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

public static class ShapeTrainer
{
    public static ShapeModel Train(GrayImage image, TrainingRegion region, TrainingOptions options,
        CancellationToken cancellationToken = default)
    {
        options.Validate();
        PreparedSearchImage.ValidateSize(image);
        if ((!region.Mask.IsEmpty && region.Mask.Length != checked(image.Width * image.Height)) ||
            !double.IsFinite(region.ReferencePoint.X) || !double.IsFinite(region.ReferencePoint.Y) ||
            !double.IsFinite(region.ReferenceAngle) || region.ReferencePoint.X < 0 ||
            region.ReferencePoint.Y < 0 || region.ReferencePoint.X > image.Width || region.ReferencePoint.Y > image.Height)
        {
            throw new ArgumentException("학습 마스크 또는 기준점이 잘못되었습니다.", nameof(region));
        }
        var levels = ImmutableArray.CreateBuilder<ModelLevel>();
        var current = image;
        var cos = Math.Cos(region.ReferenceAngle);
        var sin = Math.Sin(region.ReferenceAngle);
        for (var level = 0; level < options.PyramidLevels; level++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var factor = 1 << level;
            var gradients = GradientImage.Create(current, options.MinimumGradient, cancellationToken);
            var candidates = new List<EdgePoint>();
            foreach (var edge in gradients.EdgePoints)
            {
                var sourceX = Math.Clamp((int)(edge.X * factor), 0, image.Width - 1);
                var sourceY = Math.Clamp((int)(edge.Y * factor), 0, image.Height - 1);
                if (region.Mask.IsEmpty || region.Mask.Span[(sourceY * image.Width) + sourceX] != 0)
                {
                    candidates.Add(edge);
                }
            }
            candidates.Sort((a, b) =>
            {
                var strength = b.Strength.CompareTo(a.Strength);
                return strength != 0 ? strength : a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X);
            });
            var selected = Scatter(candidates, options.MaximumFeatures);
            if (selected.Count < 12)
            {
                if (level == 0)
                {
                    throw new ArgumentException("학습 ROI에 분산된 유효 에지가 12개 이상 필요합니다.", nameof(region));
                }
                break;
            }
            var features = selected.Select(e =>
            {
                var x = e.X - (region.ReferencePoint.X / factor);
                var y = e.Y - (region.ReferencePoint.Y / factor);
                return new ModelFeature((cos * x) + (sin * y), (-sin * x) + (cos * y),
                    (cos * e.NormalX) + (sin * e.NormalY), (-sin * e.NormalX) + (cos * e.NormalY));
            }).ToImmutableArray();
            levels.Add(new(factor, features));
            current = GradientImage.Reduce(current);
        }
        var templates = ImmutableArray.CreateBuilder<PoseTemplate>();
        foreach (var degrees in Samples(options.MinimumAngleDegrees, options.MaximumAngleDegrees, options.AngleStepDegrees))
        {
            foreach (var scale in Samples(options.MinimumScale, options.MaximumScale, options.ScaleStep))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var angle = region.ReferenceAngle + (degrees * Math.PI / 180);
                var c = Math.Cos(angle);
                var s = Math.Sin(angle);
                templates.Add(new(angle, scale, levels.Select(l => l.Features.Select(f => new QuantizedFeature(
                    (int)Math.Round(scale * ((c * f.X) - (s * f.Y))),
                    (int)Math.Round(scale * ((s * f.X) + (c * f.Y))),
                    OrientationMaps.Quantize((c * f.NormalX) - (s * f.NormalY), (s * f.NormalX) + (c * f.NormalY))
                )).ToImmutableArray()).ToImmutableArray()));
            }
        }
        return new(region, options, levels.ToImmutable(), CreateBounds(image, region), templates.ToImmutable());
    }

    private static ImmutableArray<ImagePoint> CreateBounds(GrayImage image, TrainingRegion region)
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
                throw new ArgumentException("학습 마스크에 유효한 픽셀이 없습니다.", nameof(region));
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

    private static List<EdgePoint> Scatter(List<EdgePoint> candidates, int count)
    {
        var selected = new List<EdgePoint>(count);
        // 강도 순으로 고르되 공간을 분산한다. 부족하면 거리를 줄이고 같은 순서로 재선택한다.
        for (var distance = 4d; distance >= 1; distance -= 0.5)
        {
            selected.Clear();
            foreach (var edge in candidates)
            {
                if (selected.All(p => ((p.X - edge.X) * (p.X - edge.X)) + ((p.Y - edge.Y) * (p.Y - edge.Y)) >= distance * distance))
                {
                    selected.Add(edge);
                    if (selected.Count == count)
                    {
                        return selected;
                    }
                }
            }
            if (selected.Count >= Math.Min(count, candidates.Count / 2))
            {
                return selected;
            }
        }
        return selected;
    }

    private static IEnumerable<double> Samples(double minimum, double maximum, double step)
    {
        var count = (int)Math.Ceiling((maximum - minimum) / step);
        for (var i = 0; i <= count; i++)
        {
            yield return Math.Min(maximum, minimum + (i * step));
        }
    }
}
