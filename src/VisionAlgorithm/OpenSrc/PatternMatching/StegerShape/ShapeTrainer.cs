// File: src/VisionAlgorithm/OpenSrc/PatternMatching/StegerShape/ShapeTrainer.cs
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
        ImmutableArray<byte> featureChains = default;
        ImmutableArray<byte> featurePartitions = default;
        ImmutableArray<byte> partitionAdjacency = default;
        ImmutableArray<byte> featureAsymmetryFlags = default;
        ImagePoint symmetryCenter = default;
        var symmetryCoreDiameter = 0d;
        ImagePoint contextSectorRadii = default;
        byte occupiedContextSectorMask = 0;
        byte emptyContextSectorMask = 0;
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
            if (level == 0)
            {
                if (options.BuildClutterModel)
                {
                    featureChains = AssignContourChains(featureCandidates, orderedSelection);
                }
                if (options.BuildClutterModel && options.UseFineDetailValidation)
                {
                    (featureAsymmetryFlags, symmetryCenter, symmetryCoreDiameter, contextSectorRadii, occupiedContextSectorMask, emptyContextSectorMask) =
                        AssignSelfSymmetryFlags(gradients, featureCandidates, orderedSelection, region.Mask.Span, image.Width, image.Height,
                            region.ReferencePoint.X, region.ReferencePoint.Y, cos, sin);
                }
                (featurePartitions, partitionAdjacency) = AssignSpatialPartitions(levelFeatures);
            }
            levels.Add(new(factor, levelFeatures));
            if (level + 1 < options.PyramidLevels)
            {
                current = GradientImage.Reduce(current);
            }
        }
        var angleObservable = IsAngleObservable(levels[0].Features);
        var templates = ImmutableArray.CreateBuilder<PoseTemplate>();
        // 가장 거친 단계에서도 인접 자세 사이 특징 이동이 확산 지지 폭을 넘지 않게 한다.
        // 단, 완전 회전 대칭(!angleObservable) 모델에서 사용자가 고정 배율(MinimumScale == MaximumScale)을 요청한 경우에는
        // 불필요한 각도 템플릿을 생성하지 않고 기준 각도(0도) 1개만 생성하되, 사용자가 지정한 model.Options의 각도/배율 계약은 그대로 보존한다.
        var radius = levels[^1].Features.Max(feature => Math.Sqrt((feature.X * feature.X) + (feature.Y * feature.Y)));
        var angleStep = Math.Min(options.AngleStepDegrees, 4 / (radius * options.MaximumScale) * 180 / Math.PI);
        var scaleStep = Math.Min(options.ScaleStep, 4 / radius);
        var effectiveMinAngle = (!angleObservable && options.BuildClutterModel) ? 0.0 : options.MinimumAngleDegrees;
        var effectiveMaxAngle = (!angleObservable && options.BuildClutterModel) ? 0.0 : options.MaximumAngleDegrees;
        var templateCount = (Math.Ceiling((effectiveMaxAngle - effectiveMinAngle) / angleStep) + 1) *
            (Math.Ceiling((options.MaximumScale - options.MinimumScale) / scaleStep) + 1);
        if (templateCount > options.MaximumTemplates)
        {
            throw new ArgumentException("특징 반경에 필요한 회전·배율 샘플이 템플릿 예산을 초과했습니다. 검색 범위를 줄이십시오.");
        }
        foreach (var degrees in Samples(effectiveMinAngle, effectiveMinAngle == effectiveMaxAngle ? effectiveMinAngle : effectiveMaxAngle, angleStep))
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
        var hasAsymmetricContext = !featureAsymmetryFlags.IsDefault && occupiedContextSectorMask != 0;
        var detail = options.UseFineDetailValidation && options.BuildClutterModel && levels.Count == 1
            ? FineDetailModel.Create(image, region, hasAsymmetricContext, !angleObservable, cancellationToken) : null;
        return new(region, options, levels.ToImmutable(), CreateBounds(image, region), templates.ToImmutable(), contourModel, detail)
        {
            PixelMask = new(image.Width, image.Height, contourModel?.OwnedMask ?? region.Mask.ToArray(), region.ReferencePoint, region.ReferenceAngle),
            FeatureChains = featureChains,
            FeaturePartitions = featurePartitions,
            PartitionAdjacency = partitionAdjacency,
            FeatureAsymmetryFlags = featureAsymmetryFlags,
            SymmetryCenter = symmetryCenter,
            SymmetryCoreDiameter = symmetryCoreDiameter,
            ContextSectorRadii = contextSectorRadii,
            OccupiedContextSectorMask = occupiedContextSectorMask,
            EmptyContextSectorMask = emptyContextSectorMask
        };
    }

    /// <summary>
    /// 학습·검사 실행 전 파라미터 변경 시 ROI의 레벨 0 특징점, 비대칭 문맥 키, 피라미드 단계 및 예상 템플릿 수를
    /// 다중 자세 양자화 비용 없이 즉시 추출하는 경량 미리보기 전용 경로.
    /// </summary>
    public static ShapeModel TrainPreview(GrayImage image, TrainingRegion region, TrainingOptions options,
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
        ImmutableArray<byte> featureChains = default;
        ImmutableArray<byte> featurePartitions = default;
        ImmutableArray<byte> partitionAdjacency = default;
        ImmutableArray<byte> featureAsymmetryFlags = default;
        ImagePoint symmetryCenter = default;
        var symmetryCoreDiameter = 0d;
        ImagePoint contextSectorRadii = default;
        byte occupiedContextSectorMask = 0;
        byte emptyContextSectorMask = 0;
        var current = image;
        var cos = Math.Cos(region.ReferenceAngle);
        var sin = Math.Sin(region.ReferenceAngle);

        // 미리보기는 레벨 0(원본 해상도)의 특징점·비대칭 문맥 키만 시각화하므로
        // 상위 피라미드(level 1~5) 반복 생성 및 양자화 비용을 생략하고, 예상 피라미드 단계 수와 템플릿 수만 해석적으로 산출한다.
        cancellationToken.ThrowIfCancellationRequested();
        var gradients = GradientImage.Create(current, options.MinimumGradient, cancellationToken);
        var candidates = new List<EdgePoint>();
        foreach (var edge in gradients.EdgePoints)
        {
            var sourceX = Math.Clamp((int)edge.X, 0, image.Width - 1);
            var sourceY = Math.Clamp((int)edge.Y, 0, image.Height - 1);
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
        var minimumStrength = candidates.Count == 0 ? 0 : candidates[0].Strength * options.MinimumFeatureContrastRatio;
        var validCandidates = new List<EdgePoint>(candidates.Count);
        foreach (var edge in candidates)
        {
            if (edge.Strength >= minimumStrength)
            {
                validCandidates.Add(edge);
            }
        }
        var maxPreviewCandidates = Math.Max(options.MaximumFeatures * 4, 480);
        List<EdgePoint> featureCandidates;
        if (validCandidates.Count <= maxPreviewCandidates)
        {
            featureCandidates = validCandidates;
        }
        else
        {
            featureCandidates = new List<EdgePoint>(maxPreviewCandidates);
            var step = (double)(validCandidates.Count - 1) / (maxPreviewCandidates - 1);
            for (var i = 0; i < maxPreviewCandidates; i++)
            {
                featureCandidates.Add(validCandidates[(int)Math.Round(i * step)]);
            }
        }
        var selected = Scatter(featureCandidates, options.MaximumFeatures, false);
        if (selected.Count < 12)
        {
            throw new ArgumentException("윤곽이 부족합니다. 대상의 테두리·핀을 포함하도록 ROI를 넓혀 다시 학습하세요. (분산된 유효 에지 12개 이상 필요)", nameof(region));
        }
        var referenceX = region.ReferencePoint.X;
        var referenceY = region.ReferencePoint.Y;
        var orderedSelection = OrderByQuadrantLeverage(selected, referenceX, referenceY);
        var featuresBuilder = ImmutableArray.CreateBuilder<ModelFeature>(orderedSelection.Count);
        foreach (var edge in orderedSelection)
        {
            var x = edge.X - referenceX;
            var y = edge.Y - referenceY;
            featuresBuilder.Add(new ModelFeature((cos * x) + (sin * y), (-sin * x) + (cos * y),
                (cos * edge.NormalX) + (sin * edge.NormalY), (-sin * edge.NormalX) + (cos * edge.NormalY)));
        }
        var level0Features = featuresBuilder.MoveToImmutable();
        if (options.BuildClutterModel && options.UseFineDetailValidation)
        {
            (featureAsymmetryFlags, symmetryCenter, symmetryCoreDiameter, contextSectorRadii, occupiedContextSectorMask, emptyContextSectorMask) =
                AssignSelfSymmetryFlags(gradients, featureCandidates, orderedSelection, region.Mask.Span, image.Width, image.Height,
                    region.ReferencePoint.X, region.ReferencePoint.Y, cos, sin);
        }
        levels.Add(new(1, level0Features));

        var maxRadiusLevel0 = Math.Max(1.0, level0Features.Max(feature => Math.Sqrt((feature.X * feature.X) + (feature.Y * feature.Y))));
        for (var level = 1; level < options.PyramidLevels; level++)
        {
            var factor = 1 << level;
            if ((maxRadiusLevel0 * 2.0 / factor) < 6.0)
            {
                break;
            }
            levels.Add(new(factor, []));
        }

        var topFactor = levels[^1].Factor;
        var angleObservable = IsAngleObservable(level0Features);
        var radius = Math.Max(1.0, maxRadiusLevel0 / topFactor);
        var angleStep = Math.Min(options.AngleStepDegrees, 4 / (radius * options.MaximumScale) * 180 / Math.PI);
        var scaleStep = Math.Min(options.ScaleStep, 4 / radius);
        var effectiveMinAngle = (!angleObservable && options.BuildClutterModel) ? 0.0 : options.MinimumAngleDegrees;
        var effectiveMaxAngle = (!angleObservable && options.BuildClutterModel) ? 0.0 : options.MaximumAngleDegrees;
        var templateCount = (int)((Math.Ceiling((effectiveMaxAngle - effectiveMinAngle) / angleStep) + 1) *
            (Math.Ceiling((options.MaximumScale - options.MinimumScale) / scaleStep) + 1));

        return new(region, options, levels.ToImmutable(), CreateBounds(image, region), [])
        {
            TemplateCount = templateCount,
            FeatureChains = featureChains,
            FeaturePartitions = featurePartitions,
            PartitionAdjacency = partitionAdjacency,
            FeatureAsymmetryFlags = featureAsymmetryFlags,
            SymmetryCenter = symmetryCenter,
            SymmetryCoreDiameter = symmetryCoreDiameter,
            ContextSectorRadii = contextSectorRadii,
            OccupiedContextSectorMask = occupiedContextSectorMask,
            EmptyContextSectorMask = emptyContextSectorMask
        };
    }

    /// <summary>
    /// 주 대칭 중심(가장 큰 닫힌 주 윤곽 체인 중심)을 기준으로
    /// 각 특징점의 X-반전(좌우 플립) 및 Y-반전(상하 플립) 대칭 짝이 존재하는지 검사하고,
    /// 모델 좌표계 4방위(-X, +X, -Y, +Y) 문맥 구역의 점유/빈 공간(Free-Space) 서명을 기록한다.
    /// 비트 1 = IsAsymX(좌우 플립 구분 키), 비트 2 = IsAsymY(상하 플립 구분 키), 비트 4/8 = 90도/270도 회전 구분 키.
    /// </summary>
    internal static (ImmutableArray<byte> Flags, ImagePoint Center, double CoreDiameter, ImagePoint SectorRadii, byte OccupiedMask, byte EmptyMask) AssignSelfSymmetryFlags(
        GradientImage gradients, List<EdgePoint> denseCandidates, List<EdgePoint> orderedSelection, ReadOnlySpan<byte> mask, int imageWidth, int imageHeight,
        double referenceX, double referenceY, double cos, double sin)
    {
        if (orderedSelection.Count < 16 || denseCandidates.Count < 16)
        {
            return default;
        }
        var count = denseCandidates.Count;
        var parent = new int[count];
        for (var i = 0; i < count; i++)
        {
            parent[i] = i;
        }
        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }
        for (var i = 0; i < count; i++)
        {
            var a = denseCandidates[i];
            for (var j = i + 1; j < count; j++)
            {
                var b = denseCandidates[j];
                var dx = a.X - b.X;
                var dy = a.Y - b.Y;
                var d2 = (dx * dx) + (dy * dy);
                if (d2 > 6.5)
                {
                    continue;
                }
                var dot = (a.NormalX * b.NormalX) + (a.NormalY * b.NormalY);
                if (dot < 0.72)
                {
                    continue;
                }
                if (d2 > 1.8)
                {
                    var tx = -a.NormalY;
                    var ty = a.NormalX;
                    var cross = Math.Abs(((b.X - a.X) * a.NormalX) + ((b.Y - a.Y) * a.NormalY));
                    var along = Math.Abs(((b.X - a.X) * tx) + ((b.Y - a.Y) * ty));
                    if (cross > 0.75 || along < cross)
                    {
                        continue;
                    }
                }
                var ra = Find(i);
                var rb = Find(j);
                if (ra != rb)
                {
                    parent[rb] = ra;
                }
            }
        }
        var compSizes = new Dictionary<int, int>();
        var compMinX = new Dictionary<int, double>();
        var compMaxX = new Dictionary<int, double>();
        var compMinY = new Dictionary<int, double>();
        var compMaxY = new Dictionary<int, double>();
        var compQMask = new Dictionary<int, int>();
        var compRadialSum = new Dictionary<int, double>();
        var denseModel = new ModelFeature[count];
        for (var i = 0; i < count; i++)
        {
            var e = denseCandidates[i];
            var rx = e.X - referenceX;
            var ry = e.Y - referenceY;
            var mx = (cos * rx) + (sin * ry);
            var my = (-sin * rx) + (cos * ry);
            var mnx = (cos * e.NormalX) + (sin * e.NormalY);
            var mny = (-sin * e.NormalX) + (cos * e.NormalY);
            denseModel[i] = new(mx, my, mnx, mny);
            var r = Find(i);
            compSizes[r] = compSizes.GetValueOrDefault(r) + 1;
            compMinX[r] = compMinX.TryGetValue(r, out var mnx0) ? Math.Min(mnx0, mx) : mx;
            compMaxX[r] = compMaxX.TryGetValue(r, out var mxx0) ? Math.Max(mxx0, mx) : mx;
            compMinY[r] = compMinY.TryGetValue(r, out var mny0) ? Math.Min(mny0, my) : my;
            compMaxY[r] = compMaxY.TryGetValue(r, out var mxy0) ? Math.Max(mxy0, my) : my;
        }
        var compSumX = new Dictionary<int, double>();
        var compSumY = new Dictionary<int, double>();
        for (var i = 0; i < count; i++)
        {
            var r = Find(i);
            var m = denseModel[i];
            compSumX[r] = compSumX.GetValueOrDefault(r) + m.X;
            compSumY[r] = compSumY.GetValueOrDefault(r) + m.Y;
            var cx = 0.5 * (compMinX[r] + compMaxX[r]);
            var cy = 0.5 * (compMinY[r] + compMaxY[r]);
            var q = (m.X >= cx ? 1 : 0) + (m.Y >= cy ? 2 : 0);
            compQMask[r] = compQMask.GetValueOrDefault(r) | (1 << q);
            var cdx = m.X - cx;
            var cdy = m.Y - cy;
            var cdr = Math.Sqrt((cdx * cdx) + (cdy * cdy));
            if (cdr > 1e-4)
            {
                compRadialSum[r] = compRadialSum.GetValueOrDefault(r) + Math.Abs(((cdx * m.NormalX) + (cdy * m.NormalY)) / cdr);
            }
        }
        var symCx = 0d;
        var symCy = 0d;
        var bestClosedRoot = -1;
        var bestClosedSize = 0;
        var bestClosedSpan = 0d;
        var totalMinX = denseModel.Min(m => m.X);
        var totalMaxX = denseModel.Max(m => m.X);
        var totalMinY = denseModel.Min(m => m.Y);
        var totalMaxY = denseModel.Max(m => m.Y);
        var totalSpanX = Math.Max(1.0, totalMaxX - totalMinX);
        var totalSpanY = Math.Max(1.0, totalMaxY - totalMinY);
        var totalSpan = Math.Max(totalSpanX, totalSpanY);
        var minBoxSpan = Math.Min(totalSpanX, totalSpanY);
        foreach (var (r, sz) in compSizes)
        {
            if (sz >= 12 && compQMask[r] == 15)
            {
                var cx = 0.5 * (compMinX[r] + compMaxX[r]);
                var cy = 0.5 * (compMinY[r] + compMaxY[r]);
                var mx = compSumX[r] / sz;
                var my = compSumY[r] / sz;
                var spanX = Math.Max(1.0, compMaxX[r] - compMinX[r]);
                var spanY = Math.Max(1.0, compMaxY[r] - compMinY[r]);
                // 닫힌 대칭 고리 위상 조건:
                // 4분면 모두 존재하고 무게중심이 경계 상자 중심과 일치하며 종횡비가 균형 잡힌 가장 큰 주 윤곽 체인을 선택한다.
                if (Math.Abs(mx - cx) <= spanX * 0.18 && Math.Abs(my - cy) <= spanY * 0.18 &&
                    spanX <= spanY * 1.45 && spanY <= spanX * 1.45 && sz > bestClosedSize)
                {
                    bestClosedRoot = r;
                    bestClosedSize = sz;
                    bestClosedSpan = Math.Max(spanX, spanY);
                    symCx = cx;
                    symCy = cy;
                }
            }
        }
        // 외륜이 주변 기판선과 병합되어 과도하게 큰 컴포넌트로 잡혔거나, 내륜만 잡혔거나, 중심이 치우친 경우에도
        // 방사형 법선(|r·n|/|r| >= 0.94)을 가지는 동심 고리 점들의 8방위 섹터 반경 중앙값으로부터
        // 코어 중심(symCx, symCy)과 코어 외경(bestClosedSpan)을 ROI 박스 크기에 무관하게 추정한다.
        // 한쪽 문맥 테두리만 ROI에 포함되어 minBoxSpan이 코어 직경 쪽으로 수축된 경우에도 외륜 반경을 놓치지 않도록
        // totalSpan 기준 상한(0.34 * totalSpan)을 함께 허용하되, dr > minBoxSpan * 0.355인 축 정렬 직선 테두리 위의 점은
        // 직선 클러스터 검사를 통해 동심 고리 반경 추정에서 제외한다.
        var rMin = minBoxSpan * 0.08;
        var rMax = Math.Max(minBoxSpan * 0.355, totalSpan * 0.34);
        var radialCount = 0;
        var sumRx = 0d;
        var sumRy = 0d;
        Span<double> octMaxR = stackalloc double[8];
        for (var i = 0; i < count; i++)
        {
            var dm = denseModel[i];
            var dr = Math.Sqrt((dm.X * dm.X) + (dm.Y * dm.Y));
            if (dr >= rMin && dr <= rMax)
            {
                var radialDot = Math.Abs(((dm.X * dm.NormalX) + (dm.Y * dm.NormalY)) / dr);
                if (radialDot >= 0.94)
                {
                    if (dr > minBoxSpan * 0.355 &&
                        ((Math.Abs(dm.X) >= minBoxSpan * 0.355 && Math.Abs(dm.NormalX) >= 0.95) ||
                         (Math.Abs(dm.Y) >= minBoxSpan * 0.355 && Math.Abs(dm.NormalY) >= 0.95)))
                    {
                        var collinearLines = 0;
                        for (var j = 0; j < count; j++)
                        {
                            if (i == j) continue;
                            var dm2 = denseModel[j];
                            if (Math.Abs(dm.NormalX) >= 0.95 && Math.Abs(dm2.NormalX) >= 0.95 &&
                                Math.Abs(dm2.X - dm.X) <= 0.75 && Math.Abs(dm2.Y - dm.Y) >= 2.0 && Math.Abs(dm2.Y - dm.Y) <= 6.0)
                            {
                                collinearLines++;
                            }
                            else if (Math.Abs(dm.NormalY) >= 0.95 && Math.Abs(dm2.NormalY) >= 0.95 &&
                                     Math.Abs(dm2.Y - dm.Y) <= 0.75 && Math.Abs(dm2.X - dm.X) >= 2.0 && Math.Abs(dm2.X - dm.X) <= 6.0)
                            {
                                collinearLines++;
                            }
                        }
                        if (collinearLines >= 3)
                        {
                            continue;
                        }
                    }
                    radialCount++;
                    sumRx += dm.X;
                    sumRy += dm.Y;
                    var oct = ((int)Math.Floor(((Math.Atan2(dm.Y, dm.X) + Math.PI) / (2 * Math.PI)) * 8.0)) & 7;
                    if (dr > octMaxR[oct])
                    {
                        octMaxR[oct] = dr;
                    }
                }
            }
        }
        Span<double> validOcts = stackalloc double[8];
        var nonZeroOcts = 0;
        for (var o = 0; o < 8; o++)
        {
            if (octMaxR[o] > 0)
            {
                validOcts[nonZeroOcts++] = octMaxR[o];
            }
        }
        if (radialCount >= 20 && nonZeroOcts >= 5)
        {
            // 대칭 중심은 직선 문맥 에지가 섞여 있을 수 있는 전체 점들의 단순 평균(sumRx/radialCount) 대신,
            // 마주 보는 8방위 대칭 쌍(o와 o+4)의 반경 균형으로부터 추정하여 한쪽 직선 테두리에 의해 중심이 끌리지 않게 한다.
            var pairSumX = 0d;
            var pairSumY = 0d;
            var pairCount = 0;
            for (var o = 0; o < 4; o++)
            {
                if (octMaxR[o] > 0 && octMaxR[o + 4] > 0 && Math.Abs(octMaxR[o] - octMaxR[o + 4]) <= Math.Min(octMaxR[o], octMaxR[o + 4]) * 0.15)
                {
                    var ang = ((o + 0.5) * Math.PI / 4.0) - Math.PI;
                    var diff = 0.5 * (octMaxR[o] - octMaxR[o + 4]);
                    pairSumX += diff * Math.Cos(ang);
                    pairSumY += diff * Math.Sin(ang);
                    pairCount++;
                }
            }
            validOcts[..nonZeroOcts].Sort();
            var mid = nonZeroOcts / 2;
            var radialCoreDiameter = nonZeroOcts % 2 == 0
                ? validOcts[mid - 1] + validOcts[mid]
                : 2.0 * validOcts[mid];
            var radialCx = pairCount >= 2 && Math.Abs(pairSumX / pairCount) <= radialCoreDiameter * 0.03 ? pairSumX / pairCount : 0;
            var radialCy = pairCount >= 2 && Math.Abs(pairSumY / pairCount) <= radialCoreDiameter * 0.03 ? pairSumY / pairCount : 0;
            if (bestClosedSize < 12 ||
                bestClosedSpan < radialCoreDiameter * 0.85 ||
                bestClosedSpan > radialCoreDiameter * 1.15 ||
                Math.Abs(symCx - radialCx) > radialCoreDiameter * 0.01 ||
                Math.Abs(symCy - radialCy) > radialCoreDiameter * 0.01)
            {
                bestClosedSize = Math.Max(bestClosedSize, radialCount);
                bestClosedSpan = radialCoreDiameter;
                symCx = radialCx;
                symCy = radialCy;
            }
        }
        // 만약 닫힌 대칭 컴포넌트나 1차 방사형 추정이 동심 도넛의 내륜(inner ring)만 잡은 경우,
        // 그 바깥 동심 고리 대역(r ∈ [0.53 * D_inner, 0.66 * D_inner])에서 주축 4방위(0°, 90°, 180°, 270°)의
        // 학습 영상 방사형 그래디언트(|r·g|/|r||g| >= 0.90)가 연속 지지되면 실제 외륜 직경으로 승격시킨다.
        if (bestClosedSize >= 12 && bestClosedSpan > 0 && bestClosedSpan * 1.24 <= totalSpan * 0.68)
        {
            Span<double> outerRingR = stackalloc double[4];
            var outerRingMinR = bestClosedSpan * 0.53;
            var outerRingMaxR = Math.Min(totalSpan * 0.31, bestClosedSpan * 0.66);
            for (var d = 0; d < 4; d++)
            {
                var ang = (d * Math.PI / 2.0) - Math.PI;
                var dirX = Math.Cos(ang);
                var dirY = Math.Sin(ang);
                var expectedNx = (cos * dirX) - (sin * dirY);
                var expectedNy = (sin * dirX) + (cos * dirY);
                for (var r = outerRingMinR; r <= outerRingMaxR; r += 0.40)
                {
                    var mx = symCx + (r * dirX);
                    var my = symCy + (r * dirY);
                    var sx = referenceX + (cos * mx) - (sin * my) - 0.5;
                    var sy = referenceY + (sin * mx) + (cos * my) - 0.5;
                    if (sx >= 1 && sx < imageWidth - 2 && sy >= 1 && sy < imageHeight - 2)
                    {
                        gradients.SampleDirection(sx, sy, out var gx, out var gy);
                        if (Math.Abs((expectedNx * gx) + (expectedNy * gy)) >= 0.90)
                        {
                            outerRingR[d] = r;
                        }
                    }
                }
            }
            Span<double> validOuter = stackalloc double[4];
            var outerOctCount = 0;
            for (var d = 0; d < 4; d++)
            {
                if (outerRingR[d] > 0)
                {
                    validOuter[outerOctCount++] = outerRingR[d];
                }
            }
            if (outerOctCount >= 3)
            {
                validOuter[..outerOctCount].Sort();
                var oMid = outerOctCount / 2;
                var outerDiam = outerOctCount % 2 == 0
                    ? validOuter[oMid - 1] + validOuter[oMid]
                    : 2.0 * validOuter[oMid];
                if (outerDiam >= bestClosedSpan * 1.15)
                {
                    bestClosedSpan = outerDiam;
                    symCx = 0;
                    symCy = 0;
                }
            }
        }
        // 순수 동심 고리/원형 마크 단독 ROI(주변에 닫힌 고리와 분리된 비방사형 직선 문맥 에지가 없는 경우)는
        // 대칭 코어 자체이므로 코어+문맥 비대칭 분해에서 제외한다.
        // 이를 보편적으로 판별하기 위해:
        // 1) 방사형 법선(|r·n|/|r| >= 0.88)을 가진 동심 고리의 실제 최대 외경(effectiveCoreSpan)을 구하고,
        // 2) 그 바깥(dr > 0.56 * effectiveCoreSpan)에 존재하는 비방사형(radialDot <= 0.88) 문맥 에지들의 비율을 확인한다.
        // 순수 동심 코어 단독 ROI와 문맥 포함 ROI의 보편적 구분:
        // 코어 외륜 바깥(dr > 0.56 * D_core)에 있는 점들 중 비방사형(radialDot <= 0.88) 직선 문맥 에지의 수를 카운트한다.
        // 순수 도넛 단독 ROI(예: 24×24, 26×27)는 내륜(D_inner ≈ 14px) 바깥의 외륜(r ≈ 9.5px) 점들도 모두 방사형(radialDot >= 0.90)이므로
        // 비방사형 문맥 에지(nonRadialExteriorEdges)가 거의 없으며, 문맥 포함 ROI(예: 29×32..38×44)는 직선 기판 테두리/패드 위에
        // 비방사형 문맥 에지가 다수 존재한다.
        var exteriorLinearEdges = 0;
        var exteriorLinearQMask = 0;
        var nonCoreCount = 0;
        var nonRadialExteriorEdges = 0;
        for (var i = 0; i < count; i++)
        {
            var dm = denseModel[i];
            var dx = dm.X - symCx;
            var dy = dm.Y - symCy;
            var dr = Math.Sqrt((dx * dx) + (dy * dy));
            if (dr > bestClosedSpan * 0.56)
            {
                nonCoreCount++;
                var radialDot = Math.Abs(((dx * dm.NormalX) + (dy * dm.NormalY)) / Math.Max(1e-6, dr));
                if (radialDot <= 0.88)
                {
                    nonRadialExteriorEdges++;
                }
                var isAxisAlignedBorder =
                    (Math.Abs(dx) >= bestClosedSpan * 0.56 && Math.Abs(dm.NormalX) >= 0.85) ||
                    (Math.Abs(dy) >= bestClosedSpan * 0.56 && Math.Abs(dm.NormalY) >= 0.85);
                if (radialDot <= 0.88 || isAxisAlignedBorder)
                {
                    exteriorLinearEdges++;
                    var q = (dx >= 0 ? 1 : 0) | (dy >= 0 ? 2 : 0);
                    exteriorLinearQMask |= 1 << q;
                }
            }
        }
        var exteriorQCount = ((exteriorLinearQMask & 1) != 0 ? 1 : 0) +
                             ((exteriorLinearQMask & 2) != 0 ? 1 : 0) +
                             ((exteriorLinearQMask & 4) != 0 ? 1 : 0) +
                             ((exteriorLinearQMask & 8) != 0 ? 1 : 0);
        if (bestClosedSize < 12 || bestClosedSpan < minBoxSpan * 0.28 || totalSpan < bestClosedSpan * 1.25 ||
            exteriorLinearEdges < 4 || nonCoreCount < count * 0.18 || (nonRadialExteriorEdges < 8 && totalSpan <= minBoxSpan * 1.06))
        {
            return default;
        }
        var coreExcludeRadius = bestClosedSpan * 0.55;
        var minAxisOffset = bestClosedSpan * 0.48;
        var pairTolSq = Math.Max(4.0, (bestClosedSpan * 0.11) * (bestClosedSpan * 0.11));
        var rotTolSq = Math.Max(1.0, (bestClosedSpan * 0.055) * (bestClosedSpan * 0.055));
        var flags = new byte[orderedSelection.Count];
        var asymXCount = 0;
        var asymYCount = 0;
        var asymR90Count = 0;
        var asymR270Count = 0;
        for (var s = 0; s < orderedSelection.Count; s++)
        {
            var e = orderedSelection[s];
            var rx = e.X - referenceX;
            var ry = e.Y - referenceY;
            var px = (cos * rx) + (sin * ry);
            var py = (-sin * rx) + (cos * ry);
            var nx = (cos * e.NormalX) + (sin * e.NormalY);
            var ny = (-sin * e.NormalX) + (cos * e.NormalY);
            var distX = Math.Abs(px - symCx);
            var distY = Math.Abs(py - symCy);
            var distR = Math.Sqrt((distX * distX) + (distY * distY));

            // 주 대칭 코어 반경 내부에 있는 점은 대칭 코어(도넛 내/외륜) 자체이므로
            // 미세 샘플링 노이즈로 비대칭 플래그(AsymX, AsymY, AsymR90, AsymR270)를 받지 않도록 제외한다.
            if (distR < coreExcludeRadius)
            {
                continue;
            }

            var mirrorXx = (2 * symCx) - px;
            var mirrorXy = py;
            var mirrorXnx = -nx;
            var mirrorXny = ny;
            var hasSymX = false;

            var mirrorYx = px;
            var mirrorYy = (2 * symCy) - py;
            var mirrorYnx = nx;
            var mirrorYny = -ny;
            var hasSymY = false;

            var rot90Px = symCx - (py - symCy);
            var rot90Py = symCy + (px - symCx);
            var rot90Nx = -ny;
            var rot90Ny = nx;
            var hasRot90 = false;

            var rot270Px = symCx + (py - symCy);
            var rot270Py = symCy - (px - symCx);
            var rot270Nx = ny;
            var rot270Ny = -nx;
            var hasRot270 = false;

            for (var i = 0; i < count; i++)
            {
                var dm = denseModel[i];
                if (!hasSymX)
                {
                    var d2x = ((dm.X - mirrorXx) * (dm.X - mirrorXx)) + ((dm.Y - mirrorXy) * (dm.Y - mirrorXy));
                    if (d2x <= pairTolSq && Math.Abs((dm.NormalX * mirrorXnx) + (dm.NormalY * mirrorXny)) >= 0.8)
                    {
                        hasSymX = true;
                    }
                }
                if (!hasSymY)
                {
                    var d2y = ((dm.X - mirrorYx) * (dm.X - mirrorYx)) + ((dm.Y - mirrorYy) * (dm.Y - mirrorYy));
                    if (d2y <= pairTolSq && Math.Abs((dm.NormalX * mirrorYnx) + (dm.NormalY * mirrorYny)) >= 0.8)
                    {
                        hasSymY = true;
                    }
                }
                if (!hasRot90)
                {
                    var d2r90 = ((dm.X - rot90Px) * (dm.X - rot90Px)) + ((dm.Y - rot90Py) * (dm.Y - rot90Py));
                    if (d2r90 <= rotTolSq && Math.Abs((dm.NormalX * rot90Nx) + (dm.NormalY * rot90Ny)) >= 0.8)
                    {
                        hasRot90 = true;
                    }
                }
                if (!hasRot270)
                {
                    var d2r270 = ((dm.X - rot270Px) * (dm.X - rot270Px)) + ((dm.Y - rot270Py) * (dm.Y - rot270Py));
                    if (d2r270 <= rotTolSq && Math.Abs((dm.NormalX * rot270Nx) + (dm.NormalY * rot270Ny)) >= 0.8)
                    {
                        hasRot270 = true;
                    }
                }
                if (hasSymX && hasSymY && hasRot90 && hasRot270)
                {
                    break;
                }
            }
            var cornerMargin = bestClosedSpan * 0.08;
            byte flag = 0;
            // AsymX(좌우 플립 구분 키)는 좌/우 문맥 구역에 배치되어 수평 방향 법선(|nx| >= 0.55)을 가진 세로 문맥 에지에 대칭적으로 부여한다.
            if (!hasSymX && distX >= minAxisOffset && Math.Abs(nx) >= 0.55 &&
                (distY < minAxisOffset - cornerMargin || distX >= distY - cornerMargin))
            {
                flag |= 1;
                asymXCount++;
            }
            // AsymY(상하 플립 구분 키)는 상/하 문맥 구역에 배치되어 수직 방향 법선(|ny| >= 0.55)을 가진 가로 문맥 에지에 대칭적으로 부여한다.
            if (!hasSymY && distY >= minAxisOffset && Math.Abs(ny) >= 0.55 &&
                (distX < minAxisOffset - cornerMargin || distY >= distX - cornerMargin))
            {
                flag |= 2;
                asymYCount++;
            }
            // AsymR90 / AsymR270(90도/270도 회전 구분 키) 역시 대칭 코어 외곽의 축 방향 비대칭 문맥 에지 중
            // 더 먼 주 문맥 반경(dist >= bestClosedSpan * 0.72)에 위치한 에지에 우선 부여해,
            // 90도 회전 시 짧은 반경(r ≈ 0.64 * D_core) 테두리가 긴 반경(r ≈ 0.90 * D_core) 문맥 키를 지지하지 못하게 한다.
            if (!hasRot90 && (flag & 3) != 0 && Math.Max(distX, distY) >= bestClosedSpan * 0.72)
            {
                flag |= 4;
                asymR90Count++;
            }
            if (!hasRot270 && (flag & 3) != 0 && Math.Max(distX, distY) >= bestClosedSpan * 0.72)
            {
                flag |= 8;
                asymR270Count++;
            }
            flags[s] = flag;
        }
        var minAxisKeyCount = 1;
        var minRotKeyCount = 2;
        var maxKeyCount = (int)Math.Floor(orderedSelection.Count * 0.45);
        var validX = asymXCount >= minAxisKeyCount && asymXCount <= maxKeyCount;
        var validY = asymYCount >= minAxisKeyCount && asymYCount <= maxKeyCount;
        var validR90 = asymR90Count >= minRotKeyCount && asymR90Count <= maxKeyCount;
        var validR270 = asymR270Count >= minRotKeyCount && asymR270Count <= maxKeyCount;
        if (validX ^ validY)
        {
            validR90 = false;
            validR270 = false;
        }
        if (!validX && !validY && !validR90 && !validR270)
        {
            return default;
        }
        if (!validX)
        {
            for (var s = 0; s < flags.Length; s++)
            {
                flags[s] &= unchecked((byte)~1);
            }
        }
        if (!validY)
        {
            for (var s = 0; s < flags.Length; s++)
            {
                flags[s] &= unchecked((byte)~2);
            }
        }
        if (!validR90)
        {
            for (var s = 0; s < flags.Length; s++)
            {
                flags[s] &= unchecked((byte)~4);
            }
        }
        if (!validR270)
        {
            for (var s = 0; s < flags.Length; s++)
            {
                flags[s] &= unchecked((byte)~8);
            }
        }

        // 비대칭 특징점(AsymX, AsymY)과 전체 밀집 윤곽(denseModel) 및 학습 영상 그래디언트 프로브로부터
        // 모델 좌표계 기준 4방위(비트 0: -X 좌, 비트 1: +X 우, 비트 2: -Y 상, 비트 3: +Y 하)의
        // 점유 방위(OccupiedMask), 비점유 대칭 방위(EmptyMask), 그리고 축별 대표 문맥 반경(ContextSectorRadii)을 대칭적으로 산출한다.
        // 중요한 기하 원리:
        // 이미 앞 단계에서 자기 대칭성 검사를 통해 반대편 거울 대칭 위치에 짝이 없는 비방사형 문맥 에지만
        // AsymX(flag & 1 != 0), AsymY(flag & 2 != 0)로 마킹되었다.
        // 따라서 대칭 코어 외륜의 잔여 샘플은 AsymX/AsymY에 포함되지 않으므로,
        // 먼저 선택된 비대칭 특징점(orderedSelection) 중 중심선 근처에 놓인 점들로부터 각 방위의 점유와 반경을 구하고,
        // 직교 테두리가 ROI 가장자리 바로 안쪽에 걸쳐 비대칭 특징점이 코너 쪽에만 잡힌 방위에 대해서만
        // 해당 비대칭 특징점의 축 오프셋(|dx| 또는 |dy|)에서 그래디언트 프로브로 지지 여부를 보완한다.
        // 각 4방위(0: -X, 1: +X, 2: -Y, 3: +Y)에 대해,
        // 비대칭 문맥 특징점(AsymX, AsymY)과 밀집 윤곽(denseModel)의 중심선 근처 직선 테두리 점들로부터
        // 각 방위의 점유 마스크(OccupiedMask), 비점유 마스크(EmptyMask), 그리고 첫 번째 직선 테두리 반경(verifiedRadius)을 산출한다.
        // 중심선(|t| <= 2.2 * tangentStep)에 실제 비대칭 직선 테두리 점이 2개 이상 존재하면 그 최소 반경(firstLineR)이
        // 곧바로 해당 방위의 정확한 테두리 반경(예: -X 방위의 11.5px)이 되며,
        // ROI 경계 바로 안쪽에 걸쳐 중심선 특징점이 부족한 방위(예: 29×32, 31×34의 +Y 방위 16.5px)에 대해서만
        // 비대칭 특징점 반경 대역(asymDirMinR..asymDirMaxR)에서 학습 영상 그래디언트 프로브로 지지 여부를 확인한다.
        // 4방위(0: -X, 1: +X, 2: -Y, 3: +Y) 각각의 중심선(|t| <= 2 * tangentStep)을 가로지르는
        // 첫 번째 직교 직선 테두리 반경(verifiedRadius)과 점유/빈 공간 마스크(occupiedMask, emptyMask)를
        // 학습 영상의 그래디언트 프로브로부터 직접 대칭적으로 산출한다.
        // 이를 통해:
        // 1) 대칭 코어(예: 도넛)의 외륜과 내측 테두리가 닿아 한 컴포넌트로 병합된 방위(예: -X 방위의 11.5px 내측 테두리)도
        //    외곽 테두리(16.9px)가 아닌 실제 첫 번째 중심선 테두리 반경(11.5px)으로 정확히 측정되고,
        // 2) 코너의 2차 보조 구조물(예: (15, -14) SMD 핀 코너)이 중심선 테두리(occupiedMask)를 오탐/취소시키는 현상을 원천 방지한다.
        var tangentStep = Math.Max(1.0, bestClosedSpan * 0.11);
        var radialStep = Math.Max(0.8, bestClosedSpan * 0.075);
        var minTangentRadius = bestClosedSpan * 0.57;
        // 4방위(0: -X, 1: +X, 2: -Y, 3: +Y) 각각의 중심선(|t| <= 2 * tangentStep)을 가로지르는
        // 첫 번째 직교 직선 테두리 반경(verifiedRadius)과 점유/빈 공간 마스크(occupiedMask, emptyMask)를
        // 학습 영상의 그래디언트 프로브로부터 반경 대역(0.57 * D_core .. ROI 경계)을 스캔하며 거울 대칭 방위와 직접 비교하여 산출한다.
        // 중요한 기하 원리:
        // 대칭 코어(도넛 외륜)의 잔여 그래디언트는 양쪽 대칭 반경(+r과 -r)에서 동일하게 나타나는 반면,
        // 비대칭 문맥 테두리(예: -X 방위의 11.5px 세로선, +Y 방위의 16.5px 가로선)는 한쪽 방위에서는 중심선 전체(bandHits >= 3)에 걸쳐
        // 직교 그래디언트가 연속 지지되고 반대쪽 거울 방위(-r)에서는 비어 있다(oppHits <= 1).
        // 이를 통해:
        // 1) 선택 특징점(orderedSelection) 샘플링 위치나 ROI 경계 걸침 여부와 무관하게 실제 직선 테두리 유무와 반경을 정확히 찾고,
        // 2) 코너의 2차 보조 구조물(예: (15, -14) SMD 핀 코너)이 중심선 테두리(occupiedMask)를 오점유/취소시키는 현상을 원천 방지한다.
        var halfRoiX = Math.Max(minTangentRadius + radialStep, 0.5 * (totalMaxX - totalMinX) + 1.5);
        var halfRoiY = Math.Max(minTangentRadius + radialStep, 0.5 * (totalMaxY - totalMinY) + 1.5);
        var avgRx = bestClosedSpan * 0.62;
        var avgRy = avgRx;
        byte occupiedMask = 0;
        byte emptyMask = 0;
        for (var axis = 0; axis < 2; axis++)
        {
            var dirNeg = axis * 2;
            var dirPos = dirNeg + 1;
            var isXDir = axis == 0;
            var expectedNx = isXDir ? cos : -sin;
            var expectedNy = isXDir ? sin : cos;
            var maxScanR = isXDir ? halfRoiX : halfRoiY;
            var negAsymCount = 0;
            var posAsymCount = 0;
            var negFirstLineR = double.PositiveInfinity;
            var posFirstLineR = double.PositiveInfinity;
            for (var s = 0; s < orderedSelection.Count; s++)
            {
                if ((flags[s] & (isXDir ? 1 : 2)) == 0)
                {
                    continue;
                }
                var e = orderedSelection[s];
                var rx = e.X - referenceX;
                var ry = e.Y - referenceY;
                var coord = isXDir ? ((cos * rx) + (sin * ry) - symCx) : ((-sin * rx) + (cos * ry) - symCy);
                var ortho = isXDir ? ((-sin * rx) + (cos * ry) - symCy) : ((cos * rx) + (sin * ry) - symCx);
                var nx = (cos * e.NormalX) + (sin * e.NormalY);
                var ny = (-sin * e.NormalX) + (cos * e.NormalY);
                var isOrthoLine = isXDir ? Math.Abs(nx) >= 0.85 : Math.Abs(ny) >= 0.85;
                if (Math.Abs(ortho) <= bestClosedSpan * 0.48 && isOrthoLine)
                {
                    if (coord < -minTangentRadius * 0.82)
                    {
                        negAsymCount++;
                        if (-coord < negFirstLineR) negFirstLineR = -coord;
                    }
                    else if (coord > minTangentRadius * 0.82)
                    {
                        posAsymCount++;
                        if (coord < posFirstLineR) posFirstLineR = coord;
                    }
                }
            }
            // 선택 특징점(orderedSelection)뿐만 아니라 밀집 후보(denseModel)의 직교 직선 테두리 점들로부터
            // 동심 코어 외륜(minTangentRadius = 0.57 * D_core) 바깥에 존재하는 직선 테두리의 접선 방향 길이(orthoSpan)와
            // 최소 반경(denseMinR)을 보편적으로 추정한다.
            // 진짜 기판 경계 테두리(예: -X 세로선, +Y 가로선)는 접선 방향 길이(orthoSpan)가 코어 직경의 0.45배 이상으로 길게 뻗어 있는 반면,
            // 코너의 국소 클러터(예: SMD 패드 단편)는 접선 방향 길이가 짧으므로(orthoSpan < 0.45 * D_core) 자연 배제된다!
            var denseNegCount = 0;
            var densePosCount = 0;
            var denseNegMinR = double.PositiveInfinity;
            var densePosMinR = double.PositiveInfinity;
            var denseNegMinOrtho = double.PositiveInfinity;
            var denseNegMaxOrtho = double.NegativeInfinity;
            var densePosMinOrtho = double.PositiveInfinity;
            var densePosMaxOrtho = double.NegativeInfinity;
            for (var i = 0; i < count; i++)
            {
                var dm = denseModel[i];
                var coord = isXDir ? (dm.X - symCx) : (dm.Y - symCy);
                var ortho = isXDir ? (dm.Y - symCy) : (dm.X - symCx);
                var isOrthoLine = isXDir ? Math.Abs(dm.NormalX) >= 0.85 : Math.Abs(dm.NormalY) >= 0.85;
                if (Math.Abs(ortho) <= bestClosedSpan * 0.55 && isOrthoLine)
                {
                    if (coord < -minTangentRadius)
                    {
                        denseNegCount++;
                        if (-coord < denseNegMinR) denseNegMinR = -coord;
                        if (ortho < denseNegMinOrtho) denseNegMinOrtho = ortho;
                        if (ortho > denseNegMaxOrtho) denseNegMaxOrtho = ortho;
                    }
                    else if (coord > minTangentRadius)
                    {
                        densePosCount++;
                        if (coord < densePosMinR) densePosMinR = coord;
                        if (ortho < densePosMinOrtho) densePosMinOrtho = ortho;
                        if (ortho > densePosMaxOrtho) densePosMaxOrtho = ortho;
                    }
                }
            }
            var negOrthoSpan = denseNegCount >= 2 ? denseNegMaxOrtho - denseNegMinOrtho : 0.0;
            var posOrthoSpan = densePosCount >= 2 ? densePosMaxOrtho - densePosMinOrtho : 0.0;
            var hasLongNegBorder = denseNegCount >= 3 && negOrthoSpan >= bestClosedSpan * 0.45 && negOrthoSpan > posOrthoSpan * 1.35;
            var hasLongPosBorder = densePosCount >= 3 && posOrthoSpan >= bestClosedSpan * 0.45 && posOrthoSpan > negOrthoSpan * 1.35;
            if (hasLongNegBorder || (denseNegCount >= 3 && (densePosCount <= 1 || denseNegMinR + (0.45 * radialStep) < densePosMinR)))
            {
                negAsymCount = Math.Max(negAsymCount, denseNegCount);
                negFirstLineR = Math.Min(negFirstLineR, denseNegMinR);
                if (hasLongNegBorder) posAsymCount = 0;
            }
            else if (hasLongPosBorder || (densePosCount >= 3 && (denseNegCount <= 1 || densePosMinR + (0.45 * radialStep) < denseNegMinR)))
            {
                posAsymCount = Math.Max(posAsymCount, densePosCount);
                posFirstLineR = Math.Min(posFirstLineR, densePosMinR);
                if (hasLongPosBorder) negAsymCount = 0;
            }
            var negDominant = negAsymCount >= 2 && (negAsymCount > posAsymCount || negFirstLineR + (0.65 * radialStep) < posFirstLineR);
            var posDominant = posAsymCount >= 2 && (posAsymCount > negAsymCount || posFirstLineR + (0.65 * radialStep) < negFirstLineR);
            var foundNegR = negDominant ? negFirstLineR : double.PositiveInfinity;
            var foundPosR = posDominant ? posFirstLineR : double.PositiveInfinity;
            var anyNegHits = 0;
            var anyPosHits = 0;
            for (var r = minTangentRadius; r <= maxScanR; r += 0.5 * radialStep)
            {
                var negHits = 0;
                var posHits = 0;
                for (var tStep = -2; tStep <= 2; tStep++)
                {
                    var tangentOffset = tStep * tangentStep;
                    var hitNegT = false;
                    var hitPosT = false;
                    for (var drIdx = -1; drIdx <= 1; drIdx++)
                    {
                        var rSample = r + (drIdx * 0.40);
                        if (!hitNegT)
                        {
                            var mxNeg = isXDir ? symCx - rSample : symCx + tangentOffset;
                            var myNeg = isXDir ? symCy + tangentOffset : symCy - rSample;
                            var sxNeg = referenceX + (cos * mxNeg) - (sin * myNeg) - 0.5;
                            var syNeg = referenceY + (sin * mxNeg) + (cos * myNeg) - 0.5;
                            if (sxNeg >= 1 && sxNeg < imageWidth - 2 && syNeg >= 1 && syNeg < imageHeight - 2)
                            {
                                gradients.SampleDirection(sxNeg, syNeg, out var gxNeg, out var gyNeg);
                                if (Math.Abs((expectedNx * gxNeg) + (expectedNy * gyNeg)) >= 0.80)
                                {
                                    hitNegT = true;
                                }
                            }
                        }
                        if (!hitPosT)
                        {
                            var mxPos = isXDir ? symCx + rSample : symCx + tangentOffset;
                            var myPos = isXDir ? symCy + tangentOffset : symCy + rSample;
                            var sxPos = referenceX + (cos * mxPos) - (sin * myPos) - 0.5;
                            var syPos = referenceY + (sin * mxPos) + (cos * myPos) - 0.5;
                            if (sxPos >= 1 && sxPos < imageWidth - 2 && syPos >= 1 && syPos < imageHeight - 2)
                            {
                                gradients.SampleDirection(sxPos, syPos, out var gxPos, out var gyPos);
                                if (Math.Abs((expectedNx * gxPos) + (expectedNy * gyPos)) >= 0.80)
                                {
                                    hitPosT = true;
                                }
                            }
                        }
                    }
                    if (hitNegT) negHits++;
                    if (hitPosT) posHits++;
                }
                if (negHits > anyNegHits) anyNegHits = negHits;
                if (posHits > anyPosHits) anyPosHits = posHits;
                var minReqNegHits = negAsymCount > posAsymCount ? 3 : 4;
                var minReqPosHits = posAsymCount > negAsymCount ? 3 : 4;
                if (!double.IsFinite(foundNegR) && negHits >= minReqNegHits && posHits <= 1 &&
                    (negAsymCount > posAsymCount || (negAsymCount == 0 && posAsymCount == 0) ||
                     (negAsymCount == posAsymCount && negFirstLineR + (0.65 * radialStep) < posFirstLineR)))
                {
                    foundNegR = r;
                }
                if (!double.IsFinite(foundPosR) && posHits >= minReqPosHits && negHits <= 1 &&
                    (posAsymCount > negAsymCount || (negAsymCount == 0 && posAsymCount == 0) ||
                     (posAsymCount == negAsymCount && posFirstLineR + (0.65 * radialStep) < negFirstLineR)))
                {
                    foundPosR = r;
                }
            }
            // 양쪽 방위(foundNegR, foundPosR)가 같은 반경(0.8 * radialStep 이내)으로 잡혔으나 한쪽은 긴 직선 테두리(hasLongNegBorder/hasLongPosBorder)로
            // 지지되고 반대쪽은 코너 클러터 프로브로만 잡힌 경우, 직선 테두리가 실제로 길게 존재하는 방위만 채택한다.
            if (double.IsFinite(foundNegR) && double.IsFinite(foundPosR) && Math.Abs(foundNegR - foundPosR) <= 0.8 * radialStep)
            {
                if (hasLongNegBorder || (denseNegCount >= 3 && (densePosCount <= 1 || denseNegMinR + (0.45 * radialStep) < densePosMinR)))
                {
                    foundNegR = denseNegMinR;
                    foundPosR = double.PositiveInfinity;
                }
                else if (hasLongPosBorder || (densePosCount >= 3 && (denseNegCount <= 1 || densePosMinR + (0.45 * radialStep) < denseNegMinR)))
                {
                    foundPosR = densePosMinR;
                    foundNegR = double.PositiveInfinity;
                }
            }
            // 순수 회전 대칭 코어 단독 ROI(비대칭 직선 테두리가 한쪽 방위에서도 연속 지지되지 않은 경우)에서
            // ROI 중심이 0.5~1.0px 어긋나 가장자리 클러터 2점이 우연히 잡힌 경우에는 점유 마스크를 세우지 않는다.
            if (double.IsFinite(foundNegR) && (!double.IsFinite(foundPosR) || foundNegR + (0.8 * radialStep) < foundPosR) &&
                (hasLongNegBorder || negAsymCount >= 3 || anyNegHits >= 4))
            {
                occupiedMask |= (byte)(1 << dirNeg);
                emptyMask |= (byte)(1 << dirPos);
                if (isXDir) avgRx = foundNegR;
                else avgRy = foundNegR;
            }
            else if (double.IsFinite(foundPosR) && (!double.IsFinite(foundNegR) || foundPosR + (0.8 * radialStep) < foundNegR) &&
                     (hasLongPosBorder || posAsymCount >= 3 || anyPosHits >= 4))
            {
                occupiedMask |= (byte)(1 << dirPos);
                emptyMask |= (byte)(1 << dirNeg);
                if (isXDir) avgRx = foundPosR;
                else avgRy = foundPosR;
            }
            else if (!double.IsFinite(foundNegR) && !double.IsFinite(foundPosR))
            {
                if (anyNegHits <= 1)
                {
                    emptyMask |= (byte)(1 << dirNeg);
                }
                if (anyPosHits <= 1)
                {
                    emptyMask |= (byte)(1 << dirPos);
                }
            }
        }
        // 한쪽 축(예: X축의 -X 세로 테두리)은 중심선 밀집 테두리 점들로 명확히 지지되었으나,
        // 다른 축(예: Y축의 +Y 가로 테두리)이 소형 ROI 경계 바로 안쪽/바깥쪽에 걸쳐 코어 외륜 접선 잔여 그래디언트(r ≈ 0.59 * D_core)나
        // SMD 패드 경계(r ≈ 0.72 * D_core)만 잡힌 경우, 바깥 반경 대역(0.75 * D_core .. 0.96 * D_core)에서 비대칭 직교 테두리 유무를 확인한다.
        for (var checkAxis = 0; checkAxis < 2; checkAxis++)
        {
            var isXDir = checkAxis == 0;
            var curR = isXDir ? avgRx : avgRy;
            var hasAxisDenseBorder = false;
            var axisNegCount = 0;
            var axisPosCount = 0;
            var axisNegMinOrtho = double.PositiveInfinity;
            var axisNegMaxOrtho = double.NegativeInfinity;
            var axisPosMinOrtho = double.PositiveInfinity;
            var axisPosMaxOrtho = double.NegativeInfinity;
            for (var i = 0; i < count; i++)
            {
                var dm = denseModel[i];
                var coord = isXDir ? (dm.X - symCx) : (dm.Y - symCy);
                var ortho = isXDir ? (dm.Y - symCy) : (dm.X - symCx);
                var isOrthoLine = isXDir ? Math.Abs(dm.NormalX) >= 0.85 : Math.Abs(dm.NormalY) >= 0.85;
                if (Math.Abs(ortho) <= bestClosedSpan * 0.55 && isOrthoLine)
                {
                    if (coord < -minTangentRadius)
                    {
                        axisNegCount++;
                        if (ortho < axisNegMinOrtho) axisNegMinOrtho = ortho;
                        if (ortho > axisNegMaxOrtho) axisNegMaxOrtho = ortho;
                    }
                    else if (coord > minTangentRadius)
                    {
                        axisPosCount++;
                        if (ortho < axisPosMinOrtho) axisPosMinOrtho = ortho;
                        if (ortho > axisPosMaxOrtho) axisPosMaxOrtho = ortho;
                    }
                }
            }
            if ((axisNegCount >= 3 && (axisNegMaxOrtho - axisNegMinOrtho) >= bestClosedSpan * 0.45) ||
                (axisPosCount >= 3 && (axisPosMaxOrtho - axisPosMinOrtho) >= bestClosedSpan * 0.45))
            {
                hasAxisDenseBorder = true;
            }
            var isOccupied = (occupiedMask & (isXDir ? 3 : 12)) != 0;
            if (!isOccupied || (!hasAxisDenseBorder && curR < bestClosedSpan * 0.76))
            {
                var dirNeg = checkAxis * 2;
                var dirPos = dirNeg + 1;
                var expectedNx = isXDir ? cos : -sin;
                var expectedNy = isXDir ? sin : cos;
                var extMinR = bestClosedSpan * 0.76;
                var extMaxR = Math.Min(bestClosedSpan * 0.96, (isXDir ? halfRoiX : halfRoiY) + (1.2 * radialStep));
                for (var r = extMinR; r <= extMaxR; r += 0.5 * radialStep)
                {
                    var negHits = 0;
                    var posHits = 0;
                    for (var tStep = -2; tStep <= 2; tStep++)
                    {
                        var tangentOffset = tStep * tangentStep;
                        var hitNegT = false;
                        var hitPosT = false;
                        for (var drIdx = -1; drIdx <= 1; drIdx++)
                        {
                            var rSample = r + (drIdx * 0.40);
                            if (!hitNegT)
                            {
                                var mxNeg = isXDir ? symCx - rSample : symCx + tangentOffset;
                                var myNeg = isXDir ? symCy + tangentOffset : symCy - rSample;
                                var sxNeg = referenceX + (cos * mxNeg) - (sin * myNeg) - 0.5;
                                var syNeg = referenceY + (sin * mxNeg) + (cos * myNeg) - 0.5;
                                if (sxNeg >= 1 && sxNeg < imageWidth - 2 && syNeg >= 1 && syNeg < imageHeight - 2)
                                {
                                    gradients.SampleDirection(sxNeg, syNeg, out var gxNeg, out var gyNeg);
                                    if (Math.Abs((expectedNx * gxNeg) + (expectedNy * gyNeg)) >= 0.80)
                                    {
                                        hitNegT = true;
                                    }
                                }
                            }
                            if (!hitPosT)
                            {
                                var mxPos = isXDir ? symCx + rSample : symCx + tangentOffset;
                                var myPos = isXDir ? symCy + tangentOffset : symCy + rSample;
                                var sxPos = referenceX + (cos * mxPos) - (sin * myPos) - 0.5;
                                var syPos = referenceY + (sin * mxPos) + (cos * myPos) - 0.5;
                                if (sxPos >= 1 && sxPos < imageWidth - 2 && syPos >= 1 && syPos < imageHeight - 2)
                                {
                                    gradients.SampleDirection(sxPos, syPos, out var gxPos, out var gyPos);
                                    if (Math.Abs((expectedNx * gxPos) + (expectedNy * gyPos)) >= 0.80)
                                    {
                                        hitPosT = true;
                                    }
                                }
                            }
                        }
                        if (hitNegT) negHits++;
                        if (hitPosT) posHits++;
                    }
                    if (negHits >= 4 && posHits <= 1)
                    {
                        occupiedMask &= (byte)~(1 << dirPos);
                        occupiedMask |= (byte)(1 << dirNeg);
                        emptyMask |= (byte)(1 << dirPos);
                        emptyMask &= (byte)~(1 << dirNeg);
                        if (isXDir) avgRx = r;
                        else avgRy = r;
                        break;
                    }
                    if (posHits >= 4 && negHits <= 1)
                    {
                        occupiedMask &= (byte)~(1 << dirNeg);
                        occupiedMask |= (byte)(1 << dirPos);
                        emptyMask |= (byte)(1 << dirNeg);
                        emptyMask &= (byte)~(1 << dirPos);
                        if (isXDir) avgRx = r;
                        else avgRy = r;
                        break;
                    }
                }
            }
        }
        if ((occupiedMask & 3) == 0 && (occupiedMask & 12) != 0)
        {
            avgRx = avgRy;
        }
        else if ((occupiedMask & 12) == 0 && (occupiedMask & 3) != 0)
        {
            avgRy = avgRx;
        }
        return ([.. flags], new(symCx, symCy), bestClosedSpan, new(avgRx, avgRy), occupiedMask, emptyMask);
    }

    /// <summary>
    /// 원형 반지름 가정 없이 8-이웃 공간 연결 요소(Connected Contour Graph)와 경계 상자 포함 관계로
    /// 외곽 윤곽(0)과 내부 구멍 윤곽(1..K-1)을 보편적으로 분리한다.
    /// </summary>
    internal static ImmutableArray<byte> AssignContourChains(List<EdgePoint> denseCandidates, List<EdgePoint> orderedSelection)
    {
        if (orderedSelection.Count < 16 || denseCandidates.Count < 16)
        {
            return default;
        }
        var count = denseCandidates.Count;
        var parent = new int[count];
        for (var i = 0; i < count; i++)
        {
            parent[i] = i;
        }
        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }
        for (var i = 0; i < count; i++)
        {
            var a = denseCandidates[i];
            for (var j = i + 1; j < count; j++)
            {
                var b = denseCandidates[j];
                var dx = a.X - b.X;
                var dy = a.Y - b.Y;
                var distSq = (dx * dx) + (dy * dy);
                if (distSq <= 5.2)
                {
                    var dotNormal = (a.NormalX * b.NormalX) + (a.NormalY * b.NormalY);
                    if (dotNormal < 0.35)
                    {
                        continue;
                    }
                    if (distSq > 0.25)
                    {
                        // 같은 윤곽을 따라 이어지는 인접 점은 두 점의 연결 벡터가 평균 법선과 수직(접선 방향)이다.
                        // 안쪽 링과 바깥쪽 링처럼 법선 방향으로 떨어진 두 윤곽 사이(normalProj > 1.15px)는 연결하지 않는다.
                        var meanNx = a.NormalX + b.NormalX;
                        var meanNy = a.NormalY + b.NormalY;
                        var meanLen = Math.Sqrt((meanNx * meanNx) + (meanNy * meanNy));
                        if (meanLen > 1e-6)
                        {
                            var normalProj = Math.Abs((dx * meanNx) + (dy * meanNy)) / meanLen;
                            if (normalProj > 1.15)
                            {
                                continue;
                            }
                        }
                    }
                    var ra = Find(i);
                    var rb = Find(j);
                    if (ra != rb)
                    {
                        parent[rb] = ra;
                    }
                }
            }
        }
        var rootMap = new Dictionary<int, int>();
        var rawComp = new int[orderedSelection.Count];
        var compCounts = new List<int>();
        var minX = new List<double>();
        var maxX = new List<double>();
        var minY = new List<double>();
        var maxY = new List<double>();
        for (var i = 0; i < count; i++)
        {
            var r = Find(i);
            if (!rootMap.TryGetValue(r, out var cid))
            {
                cid = compCounts.Count;
                rootMap[r] = cid;
                compCounts.Add(0);
                minX.Add(double.PositiveInfinity);
                maxX.Add(double.NegativeInfinity);
                minY.Add(double.PositiveInfinity);
                maxY.Add(double.NegativeInfinity);
            }
            var pt = denseCandidates[i];
            if (pt.X < minX[cid]) minX[cid] = pt.X;
            if (pt.X > maxX[cid]) maxX[cid] = pt.X;
            if (pt.Y < minY[cid]) minY[cid] = pt.Y;
            if (pt.Y > maxY[cid]) maxY[cid] = pt.Y;
        }
        for (var s = 0; s < orderedSelection.Count; s++)
        {
            var sel = orderedSelection[s];
            var bestCid = 0;
            var bestDist = double.PositiveInfinity;
            for (var i = 0; i < count; i++)
            {
                var pt = denseCandidates[i];
                var d = ((pt.X - sel.X) * (pt.X - sel.X)) + ((pt.Y - sel.Y) * (pt.Y - sel.Y));
                if (d < bestDist)
                {
                    bestDist = d;
                    bestCid = rootMap[Find(i)];
                    if (d == 0)
                    {
                        break;
                    }
                }
            }
            rawComp[s] = bestCid;
            compCounts[bestCid]++;
        }
        // 각 연결 윤곽이 모든 방위(4분면)를 둘러싸는 닫힌 고리(Closed Loop) 위상인지 확인한다.
        // 한쪽으로 열린 F/L자 외곽과 내부 작은 원(예: SyntheticScene)은 닫힌 중첩 링이 아니므로 제외한다.
        bool IsClosedLoop(int compId)
        {
            var cx = (minX[compId] + maxX[compId]) * 0.5;
            var cy = (minY[compId] + maxY[compId]) * 0.5;
            var qMask = 0;
            var sx = 0d;
            var sy = 0d;
            var n = 0;
            for (var s = 0; s < orderedSelection.Count; s++)
            {
                if (rawComp[s] != compId)
                {
                    continue;
                }
                var pt = orderedSelection[s];
                qMask |= 1 << ((pt.X >= cx ? 1 : 0) + (pt.Y >= cy ? 2 : 0));
                sx += pt.X;
                sy += pt.Y;
                n++;
            }
            if (qMask != 15 || n < 8)
            {
                return false;
            }
            var mx = sx / n;
            var my = sy / n;
            var spanX = Math.Max(1.0, maxX[compId] - minX[compId]);
            var spanY = Math.Max(1.0, maxY[compId] - minY[compId]);
            return Math.Abs(mx - cx) <= spanX * 0.18 && Math.Abs(my - cy) <= spanY * 0.18;
        }
        var major = new List<int>();
        for (var cid = 0; cid < compCounts.Count; cid++)
        {
            if (compCounts[cid] >= 8 && IsClosedLoop(cid))
            {
                major.Add(cid);
            }
        }
        if (major.Count < 2)
        {
            // ROI 가장자리가 외곽 윤곽 일부를 잘라내어 외곽 체인이 두세 조각으로 나뉜 경우:
            // 내부 닫힌 윤곽(innerLoop)의 전체 경계 상자가 모델 전체 폭/높이의 30%~88%를 차지하고,
            // 그 바깥을 둘러싸며 법선 부호가 반대(반지름 방향 내적이 정반대)인 외곽 윤곽 점들을 체인 0으로 묶는다.
            if (major.Count == 1)
            {
                var innerLoop = major[0];
                var inMinX = minX[innerLoop];
                var inMaxX = maxX[innerLoop];
                var inMinY = minY[innerLoop];
                var inMaxY = maxY[innerLoop];
                var totalMinX = orderedSelection.Min(p => p.X);
                var totalMaxX = orderedSelection.Max(p => p.X);
                var totalMinY = orderedSelection.Min(p => p.Y);
                var totalMaxY = orderedSelection.Max(p => p.Y);
                var totalSpanX = Math.Max(1.0, totalMaxX - totalMinX);
                var totalSpanY = Math.Max(1.0, totalMaxY - totalMinY);
                var inSpanX = inMaxX - inMinX;
                var inSpanY = inMaxY - inMinY;
                if (inSpanX < totalSpanX * 0.30 || inSpanY < totalSpanY * 0.30 ||
                    inSpanX > totalSpanX * 0.88 || inSpanY > totalSpanY * 0.88)
                {
                    return default;
                }
                var inCx = (inMinX + inMaxX) * 0.5;
                var inCy = (inMinY + inMaxY) * 0.5;
                var inRadius = 0.25 * (inSpanX + inSpanY);
                var innerRadialSum = 0d;
                var innerCount = 0;
                for (var s = 0; s < orderedSelection.Count; s++)
                {
                    if (rawComp[s] != innerLoop)
                    {
                        continue;
                    }
                    var pt = orderedSelection[s];
                    var dx = pt.X - inCx;
                    var dy = pt.Y - inCy;
                    var r = Math.Sqrt((dx * dx) + (dy * dy));
                    if (r > 1e-6)
                    {
                        innerRadialSum += ((dx * pt.NormalX) + (dy * pt.NormalY)) / r;
                        innerCount++;
                    }
                }
                var innerRadialSign = innerCount > 0 ? innerRadialSum / innerCount : 0d;
                var outerIndices = new List<int>();
                var outQMask = 0;
                var outerRadialSum = 0d;
                for (var s = 0; s < orderedSelection.Count; s++)
                {
                    if (rawComp[s] == innerLoop)
                    {
                        continue;
                    }
                    var pt = orderedSelection[s];
                    var dx = pt.X - inCx;
                    var dy = pt.Y - inCy;
                    var dist = Math.Sqrt((dx * dx) + (dy * dy));
                    if (dist > inRadius * 1.18 && dist <= inRadius * 2.5)
                    {
                        outerIndices.Add(s);
                        outQMask |= 1 << ((pt.X >= inCx ? 1 : 0) + (pt.Y >= inCy ? 2 : 0));
                        outerRadialSum += ((dx * pt.NormalX) + (dy * pt.NormalY)) / dist;
                    }
                }
                var outerRadialSign = outerIndices.Count > 0 ? outerRadialSum / outerIndices.Count : 0d;
                if (outerIndices.Count >= 8 && innerCount >= 8 &&
                    System.Numerics.BitOperations.PopCount((uint)outQMask) == 4 &&
                    innerRadialSign * outerRadialSign < -0.25)
                {
                    var fallbackChains = new byte[orderedSelection.Count];
                    for (var s = 0; s < orderedSelection.Count; s++)
                    {
                        fallbackChains[s] = outerIndices.Contains(s) ? (byte)0 : (byte)1;
                    }
                    return [.. fallbackChains];
                }
            }
            return default;
        }
        major.Sort((a, b) => ((maxX[b] - minX[b]) * (maxY[b] - minY[b])).CompareTo((maxX[a] - minX[a]) * (maxY[a] - minY[a])));
        var outer = major[0];
        var outerSpanX = maxX[outer] - minX[outer];
        var outerSpanY = maxY[outer] - minY[outer];
        if (outerSpanX < 6 || outerSpanY < 6)
        {
            return default;
        }
        // 최외곽 닫힌 윤곽(outer)이 내부 닫힌 윤곽(inner)의 경계 상자를 사방에서 감싸고 무게중심이 일치하는 중첩(Enclosure) 위상인지 확인한다.
        var outerCx = (minX[outer] + maxX[outer]) * 0.5;
        var outerCy = (minY[outer] + maxY[outer]) * 0.5;
        var enclosedMajor = new List<int> { outer };
        for (var m = 1; m < major.Count && enclosedMajor.Count < 8; m++)
        {
            var inner = major[m];
            var innerSpanX = maxX[inner] - minX[inner];
            var innerSpanY = maxY[inner] - minY[inner];
            var innerCx = (minX[inner] + maxX[inner]) * 0.5;
            var innerCy = (minY[inner] + maxY[inner]) * 0.5;
            if (minX[inner] > minX[outer] + 0.75 && maxX[inner] < maxX[outer] - 0.75 &&
                minY[inner] > minY[outer] + 0.75 && maxY[inner] < maxY[outer] - 0.75 &&
                innerSpanX >= outerSpanX * 0.28 && innerSpanY >= outerSpanY * 0.28 &&
                innerSpanX <= outerSpanX * 0.88 && innerSpanY <= outerSpanY * 0.88 &&
                Math.Abs(innerCx - outerCx) <= outerSpanX * 0.20 &&
                Math.Abs(innerCy - outerCy) <= outerSpanY * 0.20)
            {
                enclosedMajor.Add(inner);
            }
        }
        if (enclosedMajor.Count < 2)
        {
            return default;
        }
        // 중심에서 각 윤곽 체인의 최소/최대 거리가 실제로 분리되어 있는지(단순히 끊어진 같은 테두리 조각이 아닌지) 확인한다.
        var centerX = (minX[outer] + maxX[outer]) * 0.5;
        var centerY = (minY[outer] + maxY[outer]) * 0.5;
        var outerMeanDist = 0d;
        var outerN = 0;
        var innerMeanDist = 0d;
        var innerN = 0;
        for (var s = 0; s < orderedSelection.Count; s++)
        {
            var d = Math.Sqrt(((orderedSelection[s].X - centerX) * (orderedSelection[s].X - centerX)) +
                              ((orderedSelection[s].Y - centerY) * (orderedSelection[s].Y - centerY)));
            if (rawComp[s] == outer)
            {
                outerMeanDist += d;
                outerN++;
            }
            else if (enclosedMajor.Contains(rawComp[s]))
            {
                innerMeanDist += d;
                innerN++;
            }
        }
        if (outerN < 8 || innerN < 8 || (outerN + innerN) < orderedSelection.Count * 0.80 ||
            (innerMeanDist / innerN) >= (outerMeanDist / outerN) * 0.88)
        {
            return default;
        }
        var chains = new byte[orderedSelection.Count];
        for (var s = 0; s < orderedSelection.Count; s++)
        {
            var cid = rawComp[s];
            var mapped = enclosedMajor.IndexOf(cid);
            if (mapped >= 0)
            {
                chains[s] = (byte)mapped;
            }
            else
            {
                // 작은 파편은 가장 가까운 주요 체인에 귀속시킨다.
                var sel = orderedSelection[s];
                var bestChain = (byte)0;
                var bestD = double.PositiveInfinity;
                for (var t = 0; t < orderedSelection.Count; t++)
                {
                    var mIdx = enclosedMajor.IndexOf(rawComp[t]);
                    if (mIdx < 0)
                    {
                        continue;
                    }
                    var pt = orderedSelection[t];
                    var d = ((pt.X - sel.X) * (pt.X - sel.X)) + ((pt.Y - sel.Y) * (pt.Y - sel.Y));
                    if (d < bestD)
                    {
                        bestD = d;
                        bestChain = (byte)mIdx;
                    }
                }
                chains[s] = bestChain;
            }
        }
        return [.. chains];
    }

    /// <summary>
    /// 2D 주성분 축(PCA)을 기준으로 특징점들을 동일 가중치의 8개 공간 블록(0..7)으로 분할하고,
    /// 각 블록 무게중심 간의 2D 공간 인접 그래프(8비트 마스크)를 생성한다.
    /// </summary>
    internal static (ImmutableArray<byte> Partitions, ImmutableArray<byte> Adjacency) AssignSpatialPartitions(ImmutableArray<ModelFeature> features)
    {
        if (features.Length < 16)
        {
            return (default, default);
        }
        var count = features.Length;
        var meanX = 0d;
        var meanY = 0d;
        for (var i = 0; i < count; i++)
        {
            meanX += features[i].X;
            meanY += features[i].Y;
        }
        meanX /= count;
        meanY /= count;
        var cxx = 0d;
        var cyy = 0d;
        var cxy = 0d;
        for (var i = 0; i < count; i++)
        {
            var dx = features[i].X - meanX;
            var dy = features[i].Y - meanY;
            cxx += dx * dx;
            cyy += dy * dy;
            cxy += dx * dy;
        }
        var theta = 0.5 * Math.Atan2(2 * cxy, cxx - cyy);
        var ux = Math.Cos(theta);
        var uy = Math.Sin(theta);
        var vx = -uy;
        var vy = ux;
        var u = new double[count];
        var v = new double[count];
        var minU = double.PositiveInfinity;
        var maxU = double.NegativeInfinity;
        var minV = double.PositiveInfinity;
        var maxV = double.NegativeInfinity;
        for (var i = 0; i < count; i++)
        {
            var dx = features[i].X - meanX;
            var dy = features[i].Y - meanY;
            var ui = (dx * ux) + (dy * uy);
            var vi = (dx * vx) + (dy * vy);
            u[i] = ui;
            v[i] = vi;
            if (ui < minU) minU = ui;
            if (ui > maxU) maxU = ui;
            if (vi < minV) minV = vi;
            if (vi > maxV) maxV = vi;
        }
        var spanU = maxU - minU;
        var spanV = maxV - minV;
        var partitions = new byte[count];
        var order = Enumerable.Range(0, count).OrderBy(i => u[i]).ThenBy(i => v[i]).ToArray();
        if (spanU >= spanV * 1.8)
        {
            // 가늘고 긴 형상은 주축(u) 방향 8등분 구간으로 나눈다.
            for (var rank = 0; rank < count; rank++)
            {
                partitions[order[rank]] = (byte)Math.Min(7, (rank * 8) / count);
            }
        }
        else
        {
            // 일반 2D 형상은 주축(u) 4등분 × 부축(v) 2등분 격자(Gray-code 순서: 0,1,2,3 위쪽 / 7,6,5,4 아래쪽)로 나눈다.
            for (var col = 0; col < 4; col++)
            {
                var start = (col * count) / 4;
                var end = ((col + 1) * count) / 4;
                var slice = order[start..end].OrderBy(i => v[i]).ToArray();
                var half = slice.Length / 2;
                for (var r = 0; r < slice.Length; r++)
                {
                    var topHalf = r < half;
                    partitions[slice[r]] = (byte)(topHalf ? col : 7 - col);
                }
            }
        }
        Span<double> cx = stackalloc double[8];
        Span<double> cy = stackalloc double[8];
        Span<int> pCount = stackalloc int[8];
        for (var i = 0; i < count; i++)
        {
            var p = partitions[i];
            cx[p] += features[i].X;
            cy[p] += features[i].Y;
            pCount[p]++;
        }
        for (var p = 0; p < 8; p++)
        {
            if (pCount[p] > 0)
            {
                cx[p] /= pCount[p];
                cy[p] /= pCount[p];
            }
        }
        var adj = new byte[8];
        for (var p = 0; p < 8; p++)
        {
            if (pCount[p] == 0)
            {
                continue;
            }
            // 각 블록을 가장 가까운 2개의 이웃 블록과 양방향 연결해 2D 인접 그래프를 구성한다.
            var best1 = -1;
            var best2 = -1;
            var d1 = double.PositiveInfinity;
            var d2 = double.PositiveInfinity;
            for (var q = 0; q < 8; q++)
            {
                if (q == p || pCount[q] == 0)
                {
                    continue;
                }
                var dist = ((cx[p] - cx[q]) * (cx[p] - cx[q])) + ((cy[p] - cy[q]) * (cy[p] - cy[q]));
                if (dist < d1)
                {
                    d2 = d1;
                    best2 = best1;
                    d1 = dist;
                    best1 = q;
                }
                else if (dist < d2)
                {
                    d2 = dist;
                    best2 = q;
                }
            }
            if (best1 >= 0)
            {
                adj[p] |= (byte)(1 << best1);
                adj[best1] |= (byte)(1 << p);
            }
            if (best2 >= 0)
            {
                adj[p] |= (byte)(1 << best2);
                adj[best2] |= (byte)(1 << p);
            }
        }
        return ([.. partitions], [.. adj]);
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
