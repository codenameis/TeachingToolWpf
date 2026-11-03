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
        ImmutableArray<byte> featureChains = default;
        ImmutableArray<byte> featurePartitions = default;
        ImmutableArray<byte> partitionAdjacency = default;
        ImmutableArray<byte> featureAsymmetryFlags = default;
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
                    featureChains = AssignContourChains(candidates, orderedSelection);
                }
                if (options.BuildClutterModel && options.UseFineDetailValidation)
                {
                    featureAsymmetryFlags = AssignSelfSymmetryFlags(candidates, orderedSelection, region.ReferencePoint.X, region.ReferencePoint.Y, cos, sin);
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
            FeatureChains = featureChains,
            FeaturePartitions = featurePartitions,
            PartitionAdjacency = partitionAdjacency,
            FeatureAsymmetryFlags = featureAsymmetryFlags
        };
    }

    /// <summary>
    /// 주 대칭 중심(가장 큰 닫힌 주 윤곽 체인 중심 또는 전체 특징 중심)을 기준으로
    /// 각 특징점의 X-반전(좌우 플립) 및 Y-반전(상하 플립) 대칭 짝이 반경 2.0px 내에 존재하는지 검사한다.
    /// 비트 1 = IsAsymX(좌우 플립 구분 키), 비트 2 = IsAsymY(상하 플립 구분 키).
    /// </summary>
    internal static ImmutableArray<byte> AssignSelfSymmetryFlags(List<EdgePoint> denseCandidates, List<EdgePoint> orderedSelection,
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
                if (dot < 0.35)
                {
                    continue;
                }
                if (d2 > 2.6)
                {
                    var tx = -a.NormalY;
                    var ty = a.NormalX;
                    var cross = Math.Abs(((b.X - a.X) * a.NormalX) + ((b.Y - a.Y) * a.NormalY));
                    var along = Math.Abs(((b.X - a.X) * tx) + ((b.Y - a.Y) * ty));
                    if (cross > 1.15 || along < cross)
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
        }
        var symCx = 0d;
        var symCy = 0d;
        var bestClosedSize = 0;
        var bestClosedSpan = 0d;
        var totalMinX = denseModel.Min(m => m.X);
        var totalMaxX = denseModel.Max(m => m.X);
        var totalMinY = denseModel.Min(m => m.Y);
        var totalMaxY = denseModel.Max(m => m.Y);
        var totalSpan = Math.Max(1.0, Math.Max(totalMaxX - totalMinX, totalMaxY - totalMinY));
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
                // 닫힌 고리 위상 조건: 4분면 모두 존재하고 무게중심이 경계 상자 중심과 일치하며 종횡비가 균형 잡힌 주 윤곽 체인
                if (Math.Abs(mx - cx) <= spanX * 0.18 && Math.Abs(my - cy) <= spanY * 0.18 &&
                    spanX <= spanY * 1.45 && spanY <= spanX * 1.45 && sz > bestClosedSize)
                {
                    bestClosedSize = sz;
                    bestClosedSpan = Math.Max(spanX, spanY);
                    symCx = cx;
                    symCy = cy;
                }
            }
        }
        // 닫힌 대칭 코어의 직경이 전체 모델 스팬의 32% 미만이면(예: 큰 비대칭 프레임 내부의 작은 장식 원) 대칭 코어 지배 모델이 아니다.
        if (bestClosedSize < 16 || bestClosedSpan < totalSpan * 0.32)
        {
            return default;
        }
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
                    if (d2x <= 4.0 && Math.Abs((dm.NormalX * mirrorXnx) + (dm.NormalY * mirrorXny)) >= 0.8)
                    {
                        hasSymX = true;
                    }
                }
                if (!hasSymY)
                {
                    var d2y = ((dm.X - mirrorYx) * (dm.X - mirrorYx)) + ((dm.Y - mirrorYy) * (dm.Y - mirrorYy));
                    if (d2y <= 4.0 && Math.Abs((dm.NormalX * mirrorYnx) + (dm.NormalY * mirrorYny)) >= 0.8)
                    {
                        hasSymY = true;
                    }
                }
                if (!hasRot90)
                {
                    var d2r90 = ((dm.X - rot90Px) * (dm.X - rot90Px)) + ((dm.Y - rot90Py) * (dm.Y - rot90Py));
                    if (d2r90 <= 4.0 && Math.Abs((dm.NormalX * rot90Nx) + (dm.NormalY * rot90Ny)) >= 0.8)
                    {
                        hasRot90 = true;
                    }
                }
                if (!hasRot270)
                {
                    var d2r270 = ((dm.X - rot270Px) * (dm.X - rot270Px)) + ((dm.Y - rot270Py) * (dm.Y - rot270Py));
                    if (d2r270 <= 4.0 && Math.Abs((dm.NormalX * rot270Nx) + (dm.NormalY * rot270Ny)) >= 0.8)
                    {
                        hasRot270 = true;
                    }
                }
                if (hasSymX && hasSymY && hasRot90 && hasRot270)
                {
                    break;
                }
            }
            byte flag = 0;
            if (!hasSymX)
            {
                flag |= 1;
                asymXCount++;
            }
            if (!hasSymY)
            {
                flag |= 2;
                asymYCount++;
            }
            if (!hasRot90)
            {
                flag |= 4;
                asymR90Count++;
            }
            if (!hasRot270)
            {
                flag |= 8;
                asymR270Count++;
            }
            flags[s] = flag;
        }
        var minKeyCount = Math.Max(6, (int)Math.Ceiling(orderedSelection.Count * 0.05));
        var maxKeyCount = (int)Math.Floor(orderedSelection.Count * 0.45);
        var validX = asymXCount >= minKeyCount && asymXCount <= maxKeyCount;
        var validY = asymYCount >= minKeyCount && asymYCount <= maxKeyCount;
        var validR90 = asymR90Count >= minKeyCount && asymR90Count <= maxKeyCount;
        var validR270 = asymR270Count >= minKeyCount && asymR270Count <= maxKeyCount;
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
        return [.. flags];
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