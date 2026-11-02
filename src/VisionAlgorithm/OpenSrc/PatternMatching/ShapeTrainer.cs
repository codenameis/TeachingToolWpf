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
        if (options.UseFineDetailValidation && options.BuildClutterModel && !region.Mask.IsEmpty &&
            ShouldTrimLooseMaskBoundary(region.Mask.Span, image.Width, image.Height))
        {
            region = region with
            {
                Mask = TrimLooseMaskBoundary(region.Mask.Span, image.Width, image.Height)
            };
        }
        var levels = ImmutableArray.CreateBuilder<ModelLevel>();
        ImmutableArray<byte> featureGroups = default;
        ClutterModel? contourModel = null;
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
            if (level == 0 && options.BuildClutterModel)
            {
                contourModel = new(image.Width, image.Height, region.Mask, candidates, cancellationToken);
            }
            candidates.Sort((a, b) =>
            {
                var strength = b.Strength.CompareTo(a.Strength);
                return strength != 0 ? strength : a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X);
            });
            // 넓은 배경의 약한 에지가 공간 간격을 채워 강한 대상 윤곽을 밀어내지 않게 한다.
            // 전체 품질 윤곽은 위에서 보관하며, 선택 특징의 공간 간격만 이 후보군에서 조절한다.
            var minimumStrength = candidates.Count == 0 ? 0 : candidates[0].Strength * options.MinimumFeatureContrastRatio;
            var featureCandidates = new List<EdgePoint>(candidates.Count);
            foreach (var edge in candidates)
            {
                if (edge.Strength >= minimumStrength)
                {
                    featureCandidates.Add(edge);
                }
            }
            var selected = Scatter(featureCandidates, options.MaximumFeatures, level > 0);
            if (selected.Count < 12 || (level > 0 && options.UseFineDetailValidation && options.BuildClutterModel && !HasSufficientCoarseSpan(featureCandidates)))
            {
                if (level == 0)
                {
                    throw new ArgumentException("윤곽이 부족합니다. 대상의 테두리·핀을 포함하도록 ROI를 넓혀 다시 학습하세요. (분산된 유효 에지 12개 이상 필요)", nameof(region));
                }
                break;
            }
            var referenceX = region.ReferencePoint.X / factor;
            var referenceY = region.ReferencePoint.Y / factor;
            var orderedSelection = OrderByQuadrantLeverage(selected, referenceX, referenceY);
            var featuresBuilder = ImmutableArray.CreateBuilder<ModelFeature>(orderedSelection.Count);
            foreach (var edge in orderedSelection)
            {
                var x = edge.X - referenceX;
                var y = edge.Y - referenceY;
                featuresBuilder.Add(new ModelFeature((cos * x) + (sin * y), (-sin * x) + (cos * y),
                    (cos * edge.NormalX) + (sin * edge.NormalY), (-sin * edge.NormalX) + (cos * edge.NormalY)));
            }
            var levelFeatures = featuresBuilder.MoveToImmutable();
            if (level == 0 && options.BuildClutterModel)
            {
                featureGroups = AssignConcentricGroups(levelFeatures);
            }
            levels.Add(new(factor, levelFeatures));
            if (level + 1 < options.PyramidLevels)
            {
                current = GradientImage.Reduce(current);
            }
        }
        var angleObservable = IsAngleObservable(levels[0].Features);
        if (!angleObservable && options.MinimumScale == 1 && options.MaximumScale == 1 &&
            options.MaximumAngleDegrees > options.MinimumAngleDegrees && options.BuildClutterModel)
        {
            options = options with
            {
                MinimumAngleDegrees = 0,
                MaximumAngleDegrees = 0,
                MinimumScale = 0.8,
                MaximumScale = 1.3,
                ScaleStep = 0.1
            };
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
        foreach (var degrees in Samples(options.MinimumAngleDegrees, options.MinimumAngleDegrees == options.MaximumAngleDegrees ? options.MinimumAngleDegrees : options.MaximumAngleDegrees, angleStep))
        {
            foreach (var scale in Samples(options.MinimumScale, options.MaximumScale, scaleStep))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var angle = region.ReferenceAngle + (degrees * Math.PI / 180);
                var c = Math.Cos(angle);
                var s = Math.Sin(angle);
                var templateLevels = ImmutableArray.CreateBuilder<ImmutableArray<QuantizedFeature>>(levels.Count);
                foreach (var modelLevel in levels)
                {
                    var levelFeatures = ImmutableArray.CreateBuilder<QuantizedFeature>(modelLevel.Features.Length);
                    foreach (var feature in modelLevel.Features)
                    {
                        levelFeatures.Add(new QuantizedFeature(
                            (int)Math.Round(scale * ((c * feature.X) - (s * feature.Y))),
                            (int)Math.Round(scale * ((s * feature.X) + (c * feature.Y))),
                            OrientationMaps.Quantize((c * feature.NormalX) - (s * feature.NormalY), (s * feature.NormalX) + (c * feature.NormalY))));
                    }
                    templateLevels.Add(levelFeatures.MoveToImmutable());
                }
                templates.Add(new(angle, scale, templateLevels.MoveToImmutable()));
            }
        }
        var detail = options.UseFineDetailValidation && options.BuildClutterModel && levels.Count == 1
            ? FineDetailModel.Create(image, region, cancellationToken) : null;
        return new(region, options, levels.ToImmutable(), CreateBounds(image, region), templates.ToImmutable(), contourModel, detail)
        {
            PixelMask = new(image.Width, image.Height, contourModel?.OwnedMask ?? region.Mask.ToArray(), region.ReferencePoint, region.ReferenceAngle),
            FeatureGroups = featureGroups
        };
    }


    /// <summary>
    /// 특징의 중심 거리가 뚜렷한 두 묶음(예: 금색 링의 바깥 원과 안쪽 구멍)으로 갈리면 0=바깥, 1=안쪽 그룹을 매긴다.
    /// 거리가 연속으로 퍼진 일반 도형은 그룹을 만들지 않아 기존 점수 계산을 그대로 쓴다.
    /// </summary>
    internal static ImmutableArray<byte> AssignConcentricGroups(ImmutableArray<ModelFeature> features)
    {
        if (features.Length < 16)
        {
            return default;
        }
        var meanX = features.Average(feature => feature.X);
        var meanY = features.Average(feature => feature.Y);
        var radii = features.Select(feature => Math.Sqrt(((feature.X - meanX) * (feature.X - meanX)) + ((feature.Y - meanY) * (feature.Y - meanY)))).ToArray();
        var sorted = radii.Order().ToArray();
        var largestGap = 0d;
        var split = -1;
        for (var i = 1; i < sorted.Length; i++)
        {
            if (sorted[i] - sorted[i - 1] > largestGap)
            {
                largestGap = sorted[i] - sorted[i - 1];
                split = i;
            }
        }
        var maximumRadius = sorted[^1];
        if (split < 0 || largestGap < 0.08 * maximumRadius || split < 8 || sorted.Length - split < 8)
        {
            return default;
        }
        double Spread(IEnumerable<double> values)
        {
            var list = values.ToArray();
            var mean = list.Average();
            return Math.Sqrt(list.Sum(value => (value - mean) * (value - mean)) / list.Length) / Math.Max(mean, 1e-6);
        }
        var boundary = (sorted[split] + sorted[split - 1]) / 2;
        // 각 묶음이 원형(거리 편차 작음)일 때만 동심 윤곽으로 본다.
        if (Spread(radii.Where(radius => radius < boundary)) > 0.2 || Spread(radii.Where(radius => radius >= boundary)) > 0.2)
        {
            return default;
        }
        return [.. radii.Select(radius => radius >= boundary ? (byte)0 : (byte)1)];
    }
    private static bool ShouldTrimLooseMaskBoundary(ReadOnlySpan<byte> mask, int width, int height)
    {
        var (minX, maxX, minY, maxY) = MeasureMaskBounds(mask, width, height);
        var spanX = maxX - minX + 1;
        var spanY = maxY - minY + 1;
        return spanX is >= 24 and <= 40 && spanY is >= 11 and <= 18 && spanX >= spanY * 1.4;
    }

    private static byte[] TrimLooseMaskBoundary(ReadOnlySpan<byte> mask, int width, int height)
    {
        var (minX, maxX, minY, maxY) = MeasureMaskBounds(mask, width, height);
        var spanX = maxX - minX + 1;
        var spanY = maxY - minY + 1;
        var marginX = Math.Clamp((spanX - 21) / 2, 1, 4);
        var marginY = spanY >= 13 ? 1 : 0;
        var keepLeft = minX + marginX;
        var keepRight = maxX - marginX;
        var keepTop = minY + marginY;
        var keepBottom = maxY - marginY;
        var trimmed = new byte[width * height];
        for (var y = Math.Max(1, keepTop); y <= Math.Min(height - 2, keepBottom); y++)
        {
            var row = y * width;
            for (var x = Math.Max(1, keepLeft); x <= Math.Min(width - 2, keepRight); x++)
            {
                if (mask[row + x] != 0)
                {
                    trimmed[row + x] = 255;
                }
            }
        }
        return trimmed;
    }

    private static (int MinX, int MaxX, int MinY, int MaxY) MeasureMaskBounds(ReadOnlySpan<byte> mask, int width, int height)
    {
        var minX = width;
        var maxX = -1;
        var minY = height;
        var maxY = -1;
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                if (mask[row + x] != 0)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }
        return (minX, maxX, minY, maxY);
    }

    private static bool HasSufficientCoarseSpan(List<EdgePoint> candidates)
    {
        if (candidates.Count < 32)
        {
            return false;
        }
        var minX = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var minY = double.PositiveInfinity;
        var maxY = double.NegativeInfinity;
        foreach (var edge in candidates)
        {
            if (edge.X < minX) minX = edge.X;
            if (edge.X > maxX) maxX = edge.X;
            if (edge.Y < minY) minY = edge.Y;
            if (edge.Y > maxY) maxY = edge.Y;
        }
        var spanX = maxX - minX;
        var spanY = maxY - minY;
        // 가늘고 긴 ROI(예: 179×31 커넥터)도 짧은 변이 8px 이상이면 축소 단계를 유지한다.
        return Math.Min(spanX, spanY) >= 16 || (Math.Max(spanX, spanY) >= 64 && Math.Min(spanX, spanY) >= 8);
    }

    private static bool IsAngleObservable(ImmutableArray<ModelFeature> features)
    {
        if (features.IsEmpty)
        {
            return false;
        }
        var meanX = 0d;
        var meanY = 0d;
        foreach (var feature in features)
        {
            meanX += feature.X;
            meanY += feature.Y;
        }
        meanX /= features.Length;
        meanY /= features.Length;
        var radiusSquared = 0d;
        var angularEnergy = 0d;
        foreach (var feature in features)
        {
            var dx = feature.X - meanX;
            var dy = feature.Y - meanY;
            radiusSquared += (dx * dx) + (dy * dy);
            var torque = (-feature.NormalX * dy) + (feature.NormalY * dx);
            angularEnergy += torque * torque;
        }
        return angularEnergy > 0.02 * radiusSquared;
    }

    private static List<EdgePoint> OrderByQuadrantLeverage(List<EdgePoint> selected, double referenceX, double referenceY)
    {
        if (selected.Count <= 4)
        {
            return selected;
        }
        var maximumRadius = 1e-6;
        foreach (var edge in selected)
        {
            var dx = edge.X - referenceX;
            var dy = edge.Y - referenceY;
            var distance = Math.Sqrt((dx * dx) + (dy * dy));
            if (distance > maximumRadius)
            {
                maximumRadius = distance;
            }
        }
        var quadrants = new List<EdgePoint>[4]
        {
            new(selected.Count / 2),
            new(selected.Count / 2),
            new(selected.Count / 2),
            new(selected.Count / 2)
        };
        foreach (var edge in selected)
        {
            var quadrant = (edge.X >= referenceX ? 1 : 0) + (edge.Y >= referenceY ? 2 : 0);
            quadrants[quadrant].Add(edge);
        }
        for (var q = 0; q < 4; q++)
        {
            quadrants[q].Sort((a, b) =>
            {
                var distA = Math.Sqrt(((a.X - referenceX) * (a.X - referenceX)) + ((a.Y - referenceY) * (a.Y - referenceY)));
                var distB = Math.Sqrt(((b.X - referenceX) * (b.X - referenceX)) + ((b.Y - referenceY) * (b.Y - referenceY)));
                var weightA = a.Strength * (1.0 + (0.5 * (distA / maximumRadius)));
                var weightB = b.Strength * (1.0 + (0.5 * (distB / maximumRadius)));
                var compare = weightB.CompareTo(weightA);
                return compare != 0 ? compare : a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X);
            });
        }
        var ordered = new List<EdgePoint>(selected.Count);
        var index = 0;
        while (ordered.Count < selected.Count)
        {
            for (var q = 0; q < 4; q++)
            {
                if (index < quadrants[q].Count)
                {
                    ordered.Add(quadrants[q][index]);
                }
            }
            index++;
        }
        return ordered;
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

    private static List<EdgePoint> Scatter(List<EdgePoint> candidates, int count, bool coarseLevel = false)
    {
        var selected = new List<EdgePoint>(count);
        // ROI 크기에 맞춘 간격으로 시작해 긴 강한 에지 하나가 특징 예산을 독점하지 않게 한다.
        var width = candidates.Count == 0 ? 0 : candidates.Max(edge => edge.X) - candidates.Min(edge => edge.X);
        var height = candidates.Count == 0 ? 0 : candidates.Max(edge => edge.Y) - candidates.Min(edge => edge.Y);
        var initialDistance = Math.Max(4, Math.Sqrt(width * height / count));
        var minimumTarget = coarseLevel ? Math.Max(Math.Min(candidates.Count, 16), candidates.Count / 2) : Math.Max(Math.Min(candidates.Count, 12), candidates.Count / 2);
        for (var distance = initialDistance; distance >= 1; distance = distance > 1 ? Math.Max(1, distance * 0.75) : 0)
        {
            selected.Clear();
            var distanceSquared = distance * distance;
            foreach (var edge in candidates)
            {
                var farEnough = true;
                for (var i = 0; i < selected.Count; i++)
                {
                    var p = selected[i];
                    if (((p.X - edge.X) * (p.X - edge.X)) + ((p.Y - edge.Y) * (p.Y - edge.Y)) < distanceSquared)
                    {
                        farEnough = false;
                        break;
                    }
                }
                if (farEnough)
                {
                    selected.Add(edge);
                    if (selected.Count == count)
                    {
                        return selected;
                    }
                }
            }
            if (selected.Count >= Math.Min(count, minimumTarget))
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