using System.Collections.Immutable;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

public static class ShapeTrainer
{
    public const int MaximumPixels = 8_388_608;

    public static ShapeModel Train(GrayImage image, TrainingRegion region, TrainingOptions options,
        CancellationToken cancellationToken = default)
    {
        options.Validate();
        if ((long)image.Width * image.Height > MaximumPixels)
        {
            throw new ArgumentException("학습 영상이 8 Mpx 작업 예산을 초과했습니다.", nameof(image));
        }
        if ((!region.Mask.IsEmpty && region.Mask.Length != checked(image.Width * image.Height)) ||
            !double.IsFinite(region.ReferencePoint.X) || !double.IsFinite(region.ReferencePoint.Y) ||
            !double.IsFinite(region.ReferenceAngle) || region.ReferencePoint.X < 0 ||
            region.ReferencePoint.Y < 0 || region.ReferencePoint.X > image.Width || region.ReferencePoint.Y > image.Height)
        {
            throw new ArgumentException("학습 마스크 또는 기준점이 잘못되었습니다.", nameof(region));
        }
        var levels = ImmutableArray.CreateBuilder<ModelLevel>();
        GeometricQualityModel? geometryModel = null;
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
            if (level == 0 && (options.BuildGeometricModel || options.BuildClutterModel))
            {
                geometryModel = new(image.Width, image.Height, region.Mask, candidates, cancellationToken);
            }
            candidates.Sort((a, b) =>
            {
                var strength = b.Strength.CompareTo(a.Strength);
                return strength != 0 ? strength : a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X);
            });
            // 넓은 배경의 약한 에지가 공간 간격을 채워 강한 대상 윤곽을 밀어내지 않게 한다.
            // 전체 품질 윤곽은 위에서 보관하며, 선택 특징의 공간 간격만 이 후보군에서 조절한다.
            var minimumStrength = candidates.Count == 0 ? 0 : candidates[0].Strength * options.MinimumFeatureContrastRatio;
            var featureCandidates = candidates.Where(edge => edge.Strength >= minimumStrength).ToList();
            var selected = Scatter(featureCandidates, options.MaximumFeatures);
            if (selected.Count < 12)
            {
                if (level == 0)
                {
                    throw new ArgumentException("윤곽이 부족합니다. 대상의 테두리·핀을 포함하도록 ROI를 넓혀 다시 학습하세요. (분산된 유효 에지 12개 이상 필요)", nameof(region));
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
            if (level + 1 < options.PyramidLevels)
            {
                current = GradientImage.Reduce(current);
            }
        }
        var templates = ImmutableArray.CreateBuilder<PoseTemplate>();
        // 가장 거친 단계에서도 인접 자세 사이 특징 이동이 확산 지지 폭을 넘지 않게 한다.
        var radius = levels[^1].Features.Max(feature => Math.Sqrt((feature.X * feature.X) + (feature.Y * feature.Y)));
        var angleStep = Math.Min(options.AngleStepDegrees, 4 / (radius * options.MaximumScale) * 180 / Math.PI);
        var scaleStep = Math.Min(options.ScaleStep, 4 / radius);
        var templateCount = (Math.Ceiling((options.MaximumAngleDegrees - options.MinimumAngleDegrees) / angleStep) + 1) *
            (Math.Ceiling((options.MaximumScale - options.MinimumScale) / scaleStep) + 1);
        if (templateCount > options.MaximumTemplates)
        {
            throw new ArgumentException("특징 반경에 필요한 회전·배율 샘플이 템플릿 예산을 초과했습니다. 검색 범위를 줄이십시오.");
        }
        foreach (var degrees in Samples(options.MinimumAngleDegrees, options.MaximumAngleDegrees, angleStep))
        {
            foreach (var scale in Samples(options.MinimumScale, options.MaximumScale, scaleStep))
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
        var detail = options.UseFineDetailValidation && options.BuildClutterModel && !options.BuildGeometricModel && levels.Count == 1
            ? FineDetailModel.Create(image, region, cancellationToken) : null;
        return new(region, options, levels.ToImmutable(), CreateBounds(image, region), templates.ToImmutable(), geometryModel, detail)
        {
            PixelMask = new(image.Width, image.Height, geometryModel?.OwnedMask ?? region.Mask.ToArray(), region.ReferencePoint, region.ReferenceAngle)
        };
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
        // ROI 크기에 맞춘 간격으로 시작해 긴 강한 에지 하나가 특징 예산을 독점하지 않게 한다.
        var width = candidates.Count == 0 ? 0 : candidates.Max(edge => edge.X) - candidates.Min(edge => edge.X);
        var height = candidates.Count == 0 ? 0 : candidates.Max(edge => edge.Y) - candidates.Min(edge => edge.Y);
        var initialDistance = Math.Max(4, Math.Sqrt(width * height / count));
        for (var distance = initialDistance; distance >= 1; distance = distance > 1 ? Math.Max(1, distance * 0.75) : 0)
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
