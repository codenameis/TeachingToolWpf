// File: src/VisionAlgorithm/OpenSrc/PatternMatching/StegerShape/ShapeTrainer.cs
using System.Collections.Immutable;
using VisionKernels;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

public static class ShapeTrainer
{
    public const int MaximumPixels = 8_388_608; // [Math Why]: 8 Mpx 학습 작업 예산 상한 (2048×4096급 기준).
    private const int MinimumScatteredFeatures = 12;
    private const int PreviewCandidateMultiplier = 4;
    private const int MinimumPreviewCandidateBudget = 480;
    private const double MinimumCoarseDiameterPixels = 6.0;
    private const double CoarseSpreadSupportPixels = 4.0; // [Math Why]: 가장 거친 레벨 확산 응답 폭(±2px, 지름 4px) 안에 회전·배율 표본 간격이 들어오도록 제한한다.
    private const double HalfCircleDegrees = 180.0;

    public static ShapeModel Train(ShapeGrayImage image, TrainingRegion region, TrainingOptions options,
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
            var candidates = new List<ShapeEdgePoint>();
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
            var featureCandidates = new List<ShapeEdgePoint>(candidates.Count);
            foreach (var edge in candidates)
            {
                if (edge.Strength >= minimumStrength)
                {
                    featureCandidates.Add(edge);
                }
            }
            var selected = Scatter(featureCandidates, options.MaximumFeatures, level > 0);
            if (selected.Count < MinimumScatteredFeatures || (level > 0 && options.UseFineDetailValidation && options.BuildClutterModel && !HasSufficientCoarseSpan(featureCandidates)))
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
                    SymmetrySignature signature = AssignSelfSymmetryFlags(
                        gradients,
                        featureCandidates,
                        orderedSelection,
                        region.Mask.Span,
                        image.Width,
                        image.Height,
                        region.ReferencePoint.X,
                        region.ReferencePoint.Y,
                        cos,
                        sin);
                    featureAsymmetryFlags = signature.Flags;
                    symmetryCenter = signature.Center;
                    symmetryCoreDiameter = signature.CoreDiameter;
                    contextSectorRadii = signature.SectorRadii;
                    occupiedContextSectorMask = signature.OccupiedMask;
                    emptyContextSectorMask = signature.EmptyMask;
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
        var angleStep = Math.Min(options.AngleStepDegrees, CoarseSpreadSupportPixels / (radius * options.MaximumScale) * HalfCircleDegrees / Math.PI);
        var scaleStep = Math.Min(options.ScaleStep, CoarseSpreadSupportPixels / radius);
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
    public static ShapeModel TrainPreview(ShapeGrayImage image, TrainingRegion region, TrainingOptions options,
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
        var candidates = new List<ShapeEdgePoint>();
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
        var validCandidates = new List<ShapeEdgePoint>(candidates.Count);
        foreach (var edge in candidates)
        {
            if (edge.Strength >= minimumStrength)
            {
                validCandidates.Add(edge);
            }
        }
        var maxPreviewCandidates = Math.Max(options.MaximumFeatures * PreviewCandidateMultiplier, MinimumPreviewCandidateBudget);
        List<ShapeEdgePoint> featureCandidates;
        if (validCandidates.Count <= maxPreviewCandidates)
        {
            featureCandidates = validCandidates;
        }
        else
        {
            featureCandidates = new List<ShapeEdgePoint>(maxPreviewCandidates);
            var step = (double)(validCandidates.Count - 1) / (maxPreviewCandidates - 1);
            for (var i = 0; i < maxPreviewCandidates; i++)
            {
                featureCandidates.Add(validCandidates[(int)Math.Round(i * step)]);
            }
        }
        var selected = Scatter(featureCandidates, options.MaximumFeatures, false);
        if (selected.Count < MinimumScatteredFeatures)
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
            SymmetrySignature signature = AssignSelfSymmetryFlags(
                gradients,
                featureCandidates,
                orderedSelection,
                region.Mask.Span,
                image.Width,
                image.Height,
                region.ReferencePoint.X,
                region.ReferencePoint.Y,
                cos,
                sin);
            featureAsymmetryFlags = signature.Flags;
            symmetryCenter = signature.Center;
            symmetryCoreDiameter = signature.CoreDiameter;
            contextSectorRadii = signature.SectorRadii;
            occupiedContextSectorMask = signature.OccupiedMask;
            emptyContextSectorMask = signature.EmptyMask;
        }
        levels.Add(new(1, level0Features));

        var maxRadiusLevel0 = Math.Max(1.0, level0Features.Max(feature => Math.Sqrt((feature.X * feature.X) + (feature.Y * feature.Y))));
        for (var level = 1; level < options.PyramidLevels; level++)
        {
            var factor = 1 << level;
            if ((maxRadiusLevel0 * 2.0 / factor) < MinimumCoarseDiameterPixels)
            {
                break;
            }
            levels.Add(new(factor, []));
        }

        var topFactor = levels[^1].Factor;
        var angleObservable = IsAngleObservable(level0Features);
        var radius = Math.Max(1.0, maxRadiusLevel0 / topFactor);
        var angleStep = Math.Min(options.AngleStepDegrees, CoarseSpreadSupportPixels / (radius * options.MaximumScale) * HalfCircleDegrees / Math.PI);
        var scaleStep = Math.Min(options.ScaleStep, CoarseSpreadSupportPixels / radius);
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
    internal static SymmetrySignature AssignSelfSymmetryFlags(
        GradientImage gradients,
        List<ShapeEdgePoint> denseCandidates,
        List<ShapeEdgePoint> orderedSelection,
        ReadOnlySpan<byte> mask,
        int imageWidth,
        int imageHeight,
        double referenceX,
        double referenceY,
        double cos,
        double sin,
        SymmetryHeuristicLimits? limits = null)
    {
        _ = mask;
        var effectiveLimits = limits ?? SymmetryHeuristicLimits.Default;
        if (orderedSelection.Count < effectiveLimits.MinimumCandidateCount ||
            denseCandidates.Count < effectiveLimits.MinimumCandidateCount)
        {
            return SymmetrySignature.Empty;
        }

        var (denseModel, components) = EdgeComponentClusterer.ClusterAndSummarize(
            denseCandidates,
            referenceX,
            referenceY,
            cos,
            sin,
            effectiveLimits);

        var core = SymmetryCoreDetector.DetectCore(
            gradients,
            denseModel,
            components,
            imageWidth,
            imageHeight,
            referenceX,
            referenceY,
            cos,
            sin,
            effectiveLimits);
        if (!core.IsValid)
        {
            return SymmetrySignature.Empty;
        }

        return SymmetryCoreDetector.BuildAsymmetryAndContextSignature(
            gradients,
            denseModel,
            orderedSelection,
            in core,
            imageWidth,
            imageHeight,
            referenceX,
            referenceY,
            cos,
            sin,
            effectiveLimits);
    }

    /// <summary>
    /// 원형 반지름 가정 없이 8-이웃 공간 연결 요소(Connected Contour Graph)와 경계 상자 포함 관계로
    /// 외곽 윤곽(0)과 내부 구멍 윤곽(1..K-1)을 보편적으로 분리한다.
    /// </summary>
    internal static ImmutableArray<byte> AssignContourChains(List<ShapeEdgePoint> denseCandidates, List<ShapeEdgePoint> orderedSelection)
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

    private static bool HasSufficientCoarseSpan(List<ShapeEdgePoint> candidates)
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

    private static List<ShapeEdgePoint> OrderByQuadrantLeverage(List<ShapeEdgePoint> selected, double referenceX, double referenceY)
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
        var quadrants = new List<ShapeEdgePoint>[4]
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
        var ordered = new List<ShapeEdgePoint>(selected.Count);
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

    private static ImmutableArray<ImagePoint> CreateBounds(ShapeGrayImage image, TrainingRegion region)
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

    private static List<ShapeEdgePoint> Scatter(List<ShapeEdgePoint> candidates, int count, bool coarseLevel = false)
    {
        var selected = new List<ShapeEdgePoint>(count);
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
        => RangeSampler.EnumerateSamples(minimum, maximum, step);
}
