// File: src/VisionAlgorithm/OpenSrc/PatternMatching/StegerShape/ContinuousVerifier.cs
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

internal readonly record struct VerifiedScore(double Score, double Support, int Quadrants);

internal static class ContinuousVerifier
{
    public static VerifiedScore Score(GradientImage image, ShapeModel model, MatchPose pose, int level = 0, bool allowElastic = true,
        double maxOcclusionRatio = 0, bool strictContext = true)
        => ScoreProposal(image, model, pose, level, double.NegativeInfinity, double.PositiveInfinity, 0, allowElastic, maxOcclusionRatio, strictContext);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static VerifiedScore ScoreProposal(GradientImage image, ShapeModel model, MatchPose pose, int level,
        double minimumScore, double featureUpperBound, double greediness, bool allowElastic,
        double maxOcclusionRatio = 0, bool strictContext = true)
    {
        var sum = 0d;
        var supported = 0;
        var quadrants = 0;
        var cos = Math.Cos(pose.Angle);
        var sin = Math.Sin(pose.Angle);
        var features = model.Levels[level].Features;
        var count = features.Length;
        var hasElasticPatches = model.FineDetail?.HasElasticPatches == true;
        var hasUniformGrid = model.FineDetail?.HasUniformGridElasticity == true;
        // 비대칭 문맥 및 4방위 공간 서명(occupiedMask, emptyMask)이 존재하는 모델은
        // ±1.0px 법선 슬라이딩(elasticFeatures) 시 서로 다른 거리의 직교 테두리(예: -90도 회전 오검출)가
        // 코어와 반대 방향으로 찢어져 가짜 정합되는 것을 막기 위해 레벨 0 방향 검증을 순수 강체(Rigid)로 수행한다.
        var hasAsymFlags = level == 0 && !model.FeatureAsymmetryFlags.IsDefault && model.FeatureAsymmetryFlags.Length == count;
        var hasOccupiedContext = hasAsymFlags && model.OccupiedContextSectorMask != 0;
        var elasticFeatures = allowElastic && level == 0 && (hasElasticPatches || hasUniformGrid || UsesLocalTolerance(model));
        var elasticSpan = hasUniformGrid ? ((hasOccupiedContext && strictContext && maxOcclusionRatio <= 0.2) ? 1 : 2) : 3;
        var allowOcclusion = maxOcclusionRatio > 0;
        // 하위 20% 외곽점 절사(Trimmed Mean) 부스팅은 국소 허용 이동(elasticFeatures)이 활성화된 모델에서
        // 유지된 상위 80% 특징 평균이 0.85 이상일 때만 작동하되, 비대칭 구분 키(AsymX, AsymY)가 결손된 후보는 제외한다.
        Span<double> featureScores = elasticFeatures && (!hasUniformGrid || !hasOccupiedContext) && count <= 512
            ? stackalloc double[count]
            : default;
        // 연결 윤곽 체인(외곽 0 / 내부 1..K-1)이 있는 모델은 체인별 평균과 부호 정합을 검사한다. ClutterWeight와 무관하다.
        var chains = level == 0 && !model.FeatureChains.IsDefault && model.FeatureChains.Length == count ? model.FeatureChains : default;
        var hasChains = !chains.IsDefault;
        var asymFlags = hasAsymFlags ? model.FeatureAsymmetryFlags : default;
        var symCenter = model.SymmetryCenter;
        var unmatchedAsymCount = 0;
        var unmatchedTotalCount = 0;
        Span<double> asymQuadSum = hasAsymFlags ? stackalloc double[4] : default;
        Span<int> asymQuadCount = hasAsymFlags ? stackalloc int[4] : default;
        Span<double> asymQuadXSum = hasAsymFlags ? stackalloc double[4] : default;
        Span<int> asymQuadXCount = hasAsymFlags ? stackalloc int[4] : default;
        Span<double> asymQuadYSum = hasAsymFlags ? stackalloc double[4] : default;
        Span<int> asymQuadYCount = hasAsymFlags ? stackalloc int[4] : default;
        var asymXSum = 0d;
        var asymYSum = 0d;
        var asymR90Sum = 0d;
        var asymR270Sum = 0d;
        var asymXCount = 0;
        var asymYCount = 0;
        var asymR90Count = 0;
        var asymR270Count = 0;
        var partitions = level == 0 && !model.FeaturePartitions.IsDefault && model.FeaturePartitions.Length == count ? model.FeaturePartitions : default;
        var hasPartitions = !partitions.IsDefault;
        Span<double> partitionRigidSum = hasPartitions ? stackalloc double[8] : default;
        Span<double> partitionElasticSum = hasPartitions ? stackalloc double[8] : default;
        Span<int> partitionCount = hasPartitions ? stackalloc int[8] : default;
        Span<int> partitionSupported = hasPartitions ? stackalloc int[8] : default;
        Span<double> partitionNormalShiftSum = hasPartitions && hasUniformGrid ? stackalloc double[8] : default;
        Span<double> partitionNormalShiftSq = hasPartitions && hasUniformGrid ? stackalloc double[8] : default;
        Span<int> partitionNormalShifted = hasPartitions && hasUniformGrid ? stackalloc int[8] : default;
        var outerSum = 0d;
        var innerSum = 0d;
        var outerCount = 0;
        var innerCount = 0;
        var outerSigned = 0d;
        var innerSigned = 0d;
        var rigidSum = 0d;
        var rigidSupported = 0;
        var rigidQuadrants = 0;
        var levelData = model.Levels[level];
        var index = 0;
        if (!elasticFeatures && Vector256.IsHardwareAccelerated && count >= 4)
        {
            var vCos = Vector256.Create(cos);
            var vSin = Vector256.Create(sin);
            var vScale = Vector256.Create(pose.Scale);
            var vPoseX = Vector256.Create(pose.X);
            var vPoseY = Vector256.Create(pose.Y);
            var vHalf = Vector256.Create(0.5);
            ref var fxRef = ref MemoryMarshal.GetArrayDataReference(levelData.FeatureX);
            ref var fyRef = ref MemoryMarshal.GetArrayDataReference(levelData.FeatureY);
            ref var nxRef = ref MemoryMarshal.GetArrayDataReference(levelData.NormalX);
            ref var nyRef = ref MemoryMarshal.GetArrayDataReference(levelData.NormalY);
            var qMask = levelData.QuadrantMask;
            Span<double> sxBuf = stackalloc double[4];
            Span<double> syBuf = stackalloc double[4];
            Span<double> snxBuf = stackalloc double[4];
            Span<double> snyBuf = stackalloc double[4];
            var effectiveMin = (allowOcclusion && hasPartitions && !hasUniformGrid && !hasElasticPatches && model.Options.UseFineDetailValidation)
                ? minimumScore * 0.72
                : minimumScore;
            for (; index <= count - 4; index += 4)
            {
                var vfx = Vector256.LoadUnsafe(ref fxRef, (nuint)index);
                var vfy = Vector256.LoadUnsafe(ref fyRef, (nuint)index);
                var vfnx = Vector256.LoadUnsafe(ref nxRef, (nuint)index);
                var vfny = Vector256.LoadUnsafe(ref nyRef, (nuint)index);
                var vx = (vPoseX + (vScale * ((vCos * vfx) - (vSin * vfy)))) - vHalf;
                var vy = (vPoseY + (vScale * ((vSin * vfx) + (vCos * vfy)))) - vHalf;
                var vnx = (vCos * vfnx) - (vSin * vfny);
                var vny = (vSin * vfnx) + (vCos * vfny);
                vx.CopyTo(sxBuf);
                vy.CopyTo(syBuf);
                vnx.CopyTo(snxBuf);
                vny.CopyTo(snyBuf);
                for (var lane = 0; lane < 4; lane++)
                {
                    var fi = index + lane;
                    image.SampleDirection(sxBuf[lane], syBuf[lane], out var gx, out var gy);
                    var dot = (snxBuf[lane] * gx) + (snyBuf[lane] * gy);
                    var score = model.Options.Polarity == ContrastPolarity.IgnoreLocal ? Math.Abs(dot) : Math.Max(0, dot);
                    if (hasChains && model.Options.Polarity == ContrastPolarity.Preserve && dot < 0)
                    {
                        score = Math.Max(-0.5, 0.5 * dot);
                    }
                    rigidSum += score;
                    if (score >= 0.8)
                    {
                        var qm = qMask[fi];
                        rigidSupported++;
                        rigidQuadrants |= qm;
                        supported++;
                        quadrants |= qm;
                    }
                    if (hasPartitions)
                    {
                        var p = partitions[fi];
                        partitionRigidSum[p] += score;
                        partitionElasticSum[p] += score;
                        partitionCount[p]++;
                        if (score >= 0.78)
                        {
                            partitionSupported[p]++;
                        }
                    }
                    AccumulateChainsAndAsym(
                        hasChains, chains, fi, dot, score, score, ref outerSigned, ref outerSum, ref outerCount, ref innerSigned, ref innerSum, ref innerCount,
                        hasAsymFlags, asymFlags, ref asymXSum, ref asymXCount, ref asymYSum, ref asymYCount, ref asymR90Sum, ref asymR90Count, ref asymR270Sum, ref asymR270Count);
                    if (hasAsymFlags && asymFlags[fi] != 0)
                    {
                        var f = features[fi];
                        var aq = (f.X >= symCenter.X ? 1 : 0) + (f.Y >= symCenter.Y ? 2 : 0);
                        var signedScore = model.Options.Polarity == ContrastPolarity.IgnoreLocal ? score : Math.Max(0, dot);
                        asymQuadSum[aq] += signedScore;
                        asymQuadCount[aq]++;
                        if ((asymFlags[fi] & 1) != 0)
                        {
                            asymQuadXSum[aq] += signedScore;
                            asymQuadXCount[aq]++;
                        }
                        if ((asymFlags[fi] & 2) != 0)
                        {
                            asymQuadYSum[aq] += signedScore;
                            asymQuadYCount[aq]++;
                        }
                    }
                    sum += score;
                }
                if (index + 4 < count && double.IsFinite(minimumScore))
                {
                    var safeUpperMean = (sum + ((count - index - 4) * featureUpperBound) + (count * 1e-12)) / count;
                    if (greediness <= 0)
                    {
                        if (safeUpperMean <= effectiveMin)
                        {
                            return new(double.NegativeInfinity, 0, 0);
                        }
                    }
                    else
                    {
                        var currentMean = sum / (index + 4);
                        if (((1.0 - greediness) * safeUpperMean) + (greediness * currentMean) <= effectiveMin)
                        {
                            return new(double.NegativeInfinity, 0, 0);
                        }
                    }
                }
            }
        }
        for (; index < count; index++)
        {
            var feature = features[index];
            var x = pose.X + (pose.Scale * ((cos * feature.X) - (sin * feature.Y))) - 0.5;
            var y = pose.Y + (pose.Scale * ((sin * feature.X) + (cos * feature.Y))) - 0.5;
            var nx = (cos * feature.NormalX) - (sin * feature.NormalY);
            var ny = (sin * feature.NormalX) + (cos * feature.NormalY);
            image.SampleDirection(x, y, out var gx, out var gy);
            // 벡터 보간 길이를 다시 1로 만들지 않는다. 약한/누락 에지와 경계 밖은 지지를 얻지 못한다.
            var dot = (nx * gx) + (ny * gy);
            var score = model.Options.Polarity == ContrastPolarity.IgnoreLocal ? Math.Abs(dot) : Math.Max(0, dot);
            if (hasChains && model.Options.Polarity == ContrastPolarity.Preserve && dot < 0)
            {
                // 극성 역전(밝은 링에 어두운 구멍)은 0점이 아니라 음수로 반영한다.
                score = Math.Max(-0.5, 0.5 * dot);
            }
            var rigidScore = score;
            rigidSum += rigidScore;
            if (rigidScore >= 0.8)
            {
                rigidSupported++;
                rigidQuadrants |= 1 << ((feature.X >= 0 ? 1 : 0) + (feature.Y >= 0 ? 2 : 0));
            }
            var bestNormalStep = 0d;
            if (elasticFeatures && score < 0.92)
            {
                if (hasUniformGrid)
                {
                    // 1D 법선 탐색(t in [-1.0, +1.0]px): 접선 슬라이딩 없이 법선 방향 5-tap 탐색과 에지 존재 게이트를 적용한다.
                    for (var step = -elasticSpan; step <= elasticSpan; step++)
                    {
                        if (step == 0)
                        {
                            continue;
                        }
                        var t = step * 0.5;
                        image.SampleDirection(x + (t * nx), y + (t * ny), out var sx, out var sy);
                        if ((sx * sx) + (sy * sy) < 0.64)
                        {
                            continue;
                        }
                        var shiftedDot = (nx * sx) + (ny * sy);
                        var shifted = (model.Options.Polarity == ContrastPolarity.IgnoreLocal ? Math.Abs(shiftedDot) : Math.Max(0, shiftedDot)) *
                            (1.0 - (0.02 * (t * t)));
                        if (shifted > score)
                        {
                            score = shifted;
                            bestNormalStep = t;
                        }
                    }
                }
                else
                {
                    for (var iy = -elasticSpan; iy <= elasticSpan; iy++)
                    {
                        var dy = iy * 0.5;
                        for (var ix = -elasticSpan; ix <= elasticSpan; ix++)
                        {
                            if (ix == 0 && iy == 0)
                            {
                                continue;
                            }
                            var dx = ix * 0.5;
                            image.SampleDirection(x + dx, y + dy, out var sx, out var sy);
                            var shiftedDot = (nx * sx) + (ny * sy);
                            var shifted = (model.Options.Polarity == ContrastPolarity.IgnoreLocal ? Math.Abs(shiftedDot) : Math.Max(0, shiftedDot)) *
                                (1.0 - (0.02 * Math.Sqrt((dx * dx) + (dy * dy))));
                            if (shifted > score)
                            {
                                score = shifted;
                            }
                        }
                    }
                }
            }
            if (hasPartitions)
            {
                var p = partitions[index];
                partitionRigidSum[p] += rigidScore;
                partitionElasticSum[p] += score;
                partitionCount[p]++;
                if (score >= 0.78)
                {
                    partitionSupported[p]++;
                }
                if (hasUniformGrid && score >= 0.75)
                {
                    partitionNormalShiftSum[p] += bestNormalStep;
                    partitionNormalShiftSq[p] += bestNormalStep * bestNormalStep;
                    partitionNormalShifted[p]++;
                }
            }
            AccumulateChainsAndAsym(
                hasChains, chains, index, dot, score, rigidScore, ref outerSigned, ref outerSum, ref outerCount, ref innerSigned, ref innerSum, ref innerCount,
                hasAsymFlags, asymFlags, ref asymXSum, ref asymXCount, ref asymYSum, ref asymYCount, ref asymR90Sum, ref asymR90Count, ref asymR270Sum, ref asymR270Count);
            if (score < 0.65)
            {
                unmatchedTotalCount++;
                if (hasAsymFlags && asymFlags[index] != 0)
                {
                    unmatchedAsymCount++;
                }
            }
            if (hasAsymFlags && asymFlags[index] != 0)
            {
                var aq = (feature.X >= symCenter.X ? 1 : 0) + (feature.Y >= symCenter.Y ? 2 : 0);
                var signedRigid = model.Options.Polarity == ContrastPolarity.IgnoreLocal ? Math.Abs(dot) : Math.Max(0, dot);
                asymQuadSum[aq] += signedRigid;
                asymQuadCount[aq]++;
                if ((asymFlags[index] & 1) != 0)
                {
                    asymQuadXSum[aq] += signedRigid;
                    asymQuadXCount[aq]++;
                }
                if ((asymFlags[index] & 2) != 0)
                {
                    asymQuadYSum[aq] += signedRigid;
                    asymQuadYCount[aq]++;
                }
            }
            if (!featureScores.IsEmpty)
            {
                featureScores[index] = score;
            }
            sum += score;
            if (score >= 0.8)
            {
                supported++;
                quadrants |= 1 << ((feature.X >= 0 ? 1 : 0) + (feature.Y >= 0 ? 2 : 0));
            }
            if ((index & 3) == 3 && index + 1 < count && double.IsFinite(minimumScore))
            {
                var effectiveMin = (elasticFeatures || (allowOcclusion && hasPartitions && !hasUniformGrid && !hasElasticPatches && model.Options.UseFineDetailValidation))
                    ? minimumScore * 0.72
                    : minimumScore;
                var safeUpperMean = (sum + ((count - index - 1) * featureUpperBound) + (count * 1e-12)) / count;
                if (greediness <= 0)
                {
                    if (safeUpperMean <= effectiveMin)
                    {
                        return new(double.NegativeInfinity, 0, 0);
                    }
                }
                else
                {
                    var currentMean = sum / (index + 1);
                    if (((1.0 - greediness) * safeUpperMean) + (greediness * currentMean) <= effectiveMin)
                    {
                        return new(double.NegativeInfinity, 0, 0);
                    }
                }
            }
        }
        // 균일 격자 탄성 모델에서 활성 블록의 80% 이상이 실제로 지지되지 않거나 블록 내부 법선 이동 분산이 크면(노이즈 위 지그재그 점프) 강체 점수로 복원한다.
        if (hasUniformGrid && hasPartitions && elasticFeatures)
        {
            var activeBlocks = 0;
            var supportedBlocks = 0;
            var coherentBlocks = 0;
            for (var p = 0; p < 8; p++)
            {
                if (partitionCount[p] == 0)
                {
                    continue;
                }
                activeBlocks++;
                if (partitionSupported[p] >= Math.Max(1, (int)Math.Ceiling(partitionCount[p] * 0.65)))
                {
                    supportedBlocks++;
                }
                var nShift = partitionNormalShifted[p];
                if (nShift > 0)
                {
                    var meanT = partitionNormalShiftSum[p] / nShift;
                    var varT = (partitionNormalShiftSq[p] / nShift) - (meanT * meanT);
                    if (varT <= 0.35)
                    {
                        coherentBlocks++;
                    }
                }
            }
            if (activeBlocks > 0 && (supportedBlocks < Math.Ceiling(activeBlocks * 0.80) || coherentBlocks < Math.Ceiling(activeBlocks * 0.75)))
            {
                sum = rigidSum;
                supported = rigidSupported;
                quadrants = rigidQuadrants;
            }
        }
        var meanScore = sum / count;
        var asymKeyMissing = false;
        var minAsymKeyScore = 1.0;
        var minAsymQuadScore = 1.0;
        var asymRotSum = 0d;
        var asymRotCount = 0;
        if (hasAsymFlags)
        {
            if (asymXCount > 0 && asymXCount >= 1)
            {
                minAsymKeyScore = Math.Min(minAsymKeyScore, asymXSum / asymXCount);
            }
            if (asymYCount > 0)
            {
                minAsymKeyScore = Math.Min(minAsymKeyScore, asymYSum / asymYCount);
            }
            if (asymR90Count > 0)
            {
                minAsymKeyScore = Math.Min(minAsymKeyScore, asymR90Sum / asymR90Count);
            }
            if (asymR270Count > 0)
            {
                minAsymKeyScore = Math.Min(minAsymKeyScore, asymR270Sum / asymR270Count);
            }
            for (var aq = 0; aq < 4; aq++)
            {
                if (asymQuadCount[aq] >= 2)
                {
                    minAsymQuadScore = Math.Min(minAsymQuadScore, asymQuadSum[aq] / asymQuadCount[aq]);
                }
                if (asymQuadXCount[aq] >= 2)
                {
                    minAsymQuadScore = Math.Min(minAsymQuadScore, asymQuadXSum[aq] / asymQuadXCount[aq]);
                }
                if (asymQuadYCount[aq] >= 2)
                {
                    minAsymQuadScore = Math.Min(minAsymQuadScore, asymQuadYSum[aq] / asymQuadYCount[aq]);
                }
            }
            if (allowElastic && model.OccupiedContextSectorMask != 0)
            {
                minAsymKeyScore = Math.Min(minAsymKeyScore, minAsymQuadScore);
            }
            if (asymR90Count > 0 || asymR270Count > 0)
            {
                asymRotSum = asymR90Sum + asymR270Sum;
                asymRotCount = asymR90Count + asymR270Count;
            }
            if (model.OccupiedContextSectorMask != 0 && minAsymKeyScore < 0.72)
            {
                asymKeyMissing = true;
            }
        }
        if (hasPartitions && strictContext && maxOcclusionRatio <= 0.2 && !hasChains && !hasUniformGrid && !hasElasticPatches && model.Options.BuildClutterModel && model.Options.UseFineDetailValidation)
        {
            // 범용 공간 블록(8개 파티션) 결손 게이트:
            // 사각형 패드·핀·모서리 등 비원형 일반 형상(!hasChains)에서 기본 엄격 모드(MaxOcclusionRatio <= 0.2, StrictContext = true)일 때
            // 특정 공간 블록(최소 4개 이상 특징점 포함)의 평균 일치도가 0.22 이하로 완전히 증발했다면(예: 한쪽 끝 배선이 없는 중간 핀),
            // 단순 평균으로 0.75~0.85점을 받아 통과하지 못하도록 결손 블록 수에 비례해 감점한다.
            var deadBlocks = 0;
            var activeBlocks = 0;
            var minBlockMean = 1.0;
            for (var p = 0; p < 8; p++)
            {
                if (partitionCount[p] < 4)
                {
                    continue;
                }
                activeBlocks++;
                var pMean = (allowElastic ? partitionElasticSum[p] : partitionRigidSum[p]) / partitionCount[p];
                if (pMean < minBlockMean)
                {
                    minBlockMean = pMean;
                }
                if (pMean <= 0.22 && partitionSupported[p] == 0)
                {
                    deadBlocks++;
                }
            }
            if (activeBlocks >= 4 && deadBlocks >= 1)
            {
                asymKeyMissing = true;
                var blockPenalty = Math.Clamp(0.68 - (0.14 * (deadBlocks - 1)) - (0.20 * (0.22 - minBlockMean)), 0.42, 0.68);
                meanScore *= blockPenalty;
            }
        }
        if (!asymKeyMissing && !(hasOccupiedContext && strictContext && maxOcclusionRatio <= 0.2) && !featureScores.IsEmpty && count >= 12)
        {
            featureScores.Sort();
            var dropCount = count / 5;
            var keptSum = 0d;
            for (var i = dropCount; i < count; i++)
            {
                keptSum += featureScores[i];
            }
            var keptMean = keptSum / (count - dropCount);
            if (keptMean >= 0.85)
            {
                meanScore = (0.85 * keptMean) + (0.15 * meanScore);
            }
        }
        // 2D 공간 연속 부분 가림(Contiguous Partial Occlusion) 보정:
        // 사용자가 가림 허용 비율(MaxOcclusionRatio > 0)을 설정했을 때만 작동한다.
        // 기본 타이트 모드(MaxOcclusionRatio == 0.0)에서는 가림 보정을 완전히 꺼서 일부 윤곽이 없는 반대쪽 모서리 등이 부스팅되지 않게 한다.
        var maxOccludedBlocks = allowOcclusion ? Math.Clamp((int)Math.Floor(maxOcclusionRatio * 8), 1, 4) : 0;
        var minInlierRatio = Math.Clamp(1.0 - maxOcclusionRatio, 0.50, 1.0);
        if (allowOcclusion && !asymKeyMissing && hasPartitions && !hasUniformGrid && !hasElasticPatches && model.Options.BuildClutterModel && model.Options.UseFineDetailValidation)
        {
            byte occludedMask = 0;
            var occludedBlocks = 0;
            var inlierBlocks = 0;
            var inlierSum = 0d;
            var inlierCount = 0;
            var inlierSupported = 0;
            for (var p = 0; p < 8; p++)
            {
                if (partitionCount[p] == 0)
                {
                    continue;
                }
                var pMean = (allowElastic ? partitionElasticSum[p] : partitionRigidSum[p]) / partitionCount[p];
                var pSupportRatio = partitionSupported[p] / (double)partitionCount[p];
                if (pMean < 0.48 || (pMean < 0.58 && pSupportRatio < 0.42))
                {
                    occludedMask |= (byte)(1 << p);
                    occludedBlocks++;
                }
                else
                {
                    inlierBlocks++;
                    inlierSum += allowElastic ? partitionElasticSum[p] : partitionRigidSum[p];
                    inlierCount += partitionCount[p];
                    inlierSupported += partitionSupported[p];
                }
            }
            if (occludedBlocks >= 1 && occludedBlocks <= maxOccludedBlocks && inlierBlocks >= 8 - maxOccludedBlocks && inlierCount >= count * minInlierRatio &&
                (inlierSum / inlierCount) >= (allowElastic ? 0.90 : 0.84) &&
                IsContiguousPartitionMask(occludedMask, model.PartitionAdjacency) &&
                HasOrthogonalNormalDiversity(image, features.AsSpan(), partitions.AsSpan(), occludedMask, pose, model.Options.Polarity))
            {
                var occludedCount = count - inlierCount;
                var occMean = inlierSum / (inlierCount + (0.18 * occludedCount));
                if (occMean > meanScore)
                {
                    meanScore = occMean;
                    supported = Math.Max(supported, (int)Math.Round(inlierSupported * (count / (double)inlierCount)));
                }
                // 연속 부분 가림이 확인된 경우, 비가림 블록(Inlier Partitions)에 속한 특징점들만으로 체인 결손 및 부호 일치도를 재계산한다.
                if (hasChains)
                {
                    outerSum = 0;
                    innerSum = 0;
                    outerCount = 0;
                    innerCount = 0;
                    outerSigned = 0;
                    innerSigned = 0;
                    for (var i = 0; i < count; i++)
                    {
                        if ((occludedMask & (1 << partitions[i])) != 0)
                        {
                            continue;
                        }
                        var f = features[i];
                        var fx = pose.X + (pose.Scale * ((cos * f.X) - (sin * f.Y))) - 0.5;
                        var fy = pose.Y + (pose.Scale * ((sin * f.X) + (cos * f.Y))) - 0.5;
                        var fnx = (cos * f.NormalX) - (sin * f.NormalY);
                        var fny = (sin * f.NormalX) + (cos * f.NormalY);
                        image.SampleDirection(fx, fy, out var fgx, out var fgy);
                        var fdot = (fnx * fgx) + (fny * fgy);
                        var fsc = model.Options.Polarity == ContrastPolarity.IgnoreLocal ? Math.Abs(fdot) : Math.Max(0, fdot);
                        if (chains[i] == 0)
                        {
                            outerSigned += fdot;
                            outerSum += fsc;
                            outerCount++;
                        }
                        else
                        {
                            innerSigned += fdot;
                            innerSum += fsc;
                            innerCount++;
                        }
                    }
                }
            }
        }
        if (hasChains && outerCount > 0 && innerCount > 0 && (outerSum / outerCount) >= 0.85 && (innerSum / innerCount) <= 0.35)
        {
            // 최외곽 윤곽 체인은 선명한데 내부 윤곽 체인이 전 방향에서 사라진 민짜 구멍: 가림이 아니라 필수 위상 구조 결손이다.
            meanScore *= 0.4;
        }
        else if (hasChains && outerCount > 0 && innerCount > 0)
        {
            // 체인 간 단면 부호 일관성 검사: 한쪽 윤곽만 극성이 뒤집히면(회색 판 위의 검은 구멍 등) 극성 무시 모드에서도 감점한다.
            var outerMean = outerSigned / outerCount;
            var innerMean = innerSigned / innerCount;
            var weaker = Math.Min(Math.Abs(outerMean), Math.Abs(innerMean));
            if ((outerMean * innerMean < 0 && weaker >= 0.15) || weaker < 0.5)
            {
                meanScore *= 0.4;
            }
        }
        if (hasAsymFlags && model.OccupiedContextSectorMask != 0)
        {
            // 비대칭 문맥 및 반전 에지 엄격 검증(StrictContext)은 Optimize가 수렴한 뒤 최종 평가(allowElastic: true) 단계에서 적용해
            // Optimize 초기 탐색 도중 미세 위치 오차(1~1.5px)로 인해 진짜 정답 후보가 조기 가지치기되지 않게 보호한다.
            if (allowElastic && strictContext && maxOcclusionRatio <= 0.2 &&
                HasMirrorCounterpartIntrusion(image, model, pose, cos, sin, asymFlags, asymXSum, asymXCount, asymYSum, asymYCount))
            {
                // 주 대칭 중심 기준 반전 위치(Mirror Counterpart)에 역방향 테두리 에지가 침범했거나
                // 비대칭 특징(AsymX, AsymY)이 결손되면 플립된 반대쪽 모서리로 판정해 점수를 0.50 이하로 떨어뜨린다.
                meanScore = Math.Min(meanScore * 0.45, 0.50);
            }
            else if (allowElastic && strictContext && maxOcclusionRatio <= 0.2 &&
                ((asymR90Count > 0 && (asymR90Sum / asymR90Count) < 0.85) || (asymR270Count > 0 && (asymR270Sum / asymR270Count) < 0.85)))
            {
                // 90도/270도 회전 시 한쪽 축(예: 세로 기판선)은 가로선에 맞아떨어져도 다른 쪽 축이 빈 공간이면 회전 비대칭 달성률이 0.85 미만으로 떨어진다.
                meanScore = Math.Min(meanScore * 0.45, 0.50);
            }
            else if (allowElastic && strictContext && maxOcclusionRatio <= 0.2 &&
                ((asymXCount >= 1 && (asymXSum / asymXCount) < 0.78) || (asymYCount >= 1 && (asymYSum / asymYCount) < 0.78) ||
                 minAsymQuadScore < 0.65 || (rigidSum / count) < 0.88 || unmatchedAsymCount >= 2 || unmatchedTotalCount >= 3))
            {
                // 비대칭 문맥 키(AsymX, AsymY) 또는 비대칭 문맥 사분면(최소 2개 이상 특징 포함) 강체 평균 정합도가 기준 미만이거나
                // 엄격 모드(MaxOcclusionRatio == 0)에서 비대칭 문맥 특징점 2개 이상(또는 전체 특징점 3개 이상)이 0.65 미만으로 완전 이탈하면 차단한다.
                meanScore = Math.Min(meanScore * 0.45, 0.50);
            }
            if (meanScore > 0.50 && minAsymKeyScore < ((asymXCount == 1 && asymYCount == 0) || (asymYCount == 1 && asymXCount == 0) ? 0.55 : 0.78))
            {
                // 비대칭 키 결손 페널티(Flip Rejection): 대칭 코어 점수가 높아도 AsymX/AsymY/AsymRot 키 정합도가 낮으면 달성률을 직접 곱한다.
                var singleKey = (asymXCount == 1 && asymYCount == 0) || (asymYCount == 1 && asymXCount == 0);
                var keyThreshold = singleKey ? 0.55 : 0.78;
                var keyAchievement = Math.Clamp(minAsymKeyScore / keyThreshold, 0.0, 1.0);
                meanScore *= keyAchievement * keyAchievement;
                if (allowElastic && strictContext && maxOcclusionRatio <= 0.2 && minAsymKeyScore < (singleKey ? 0.45 : 0.72))
                {
                    meanScore = Math.Min(meanScore * 0.45, 0.50);
                }
                else if (minAsymKeyScore < (singleKey ? 0.40 : 0.68))
                {
                    meanScore = Math.Min(meanScore, 0.54);
                }
            }
        }
        return new(meanScore, Math.Min(1.0, supported / (double)count), BitOperations.PopCount((uint)quadrants));
    }

    /// <summary>
    /// 비대칭 특징(AsymX, AsymY)이 원래 위치에서는 결손되고 주 대칭 중심(SymmetryCenter) 기준 반대편 대칭 위치(Mirror Counterpart)에
    /// 강한 에지가 존재하거나, 티칭된 모델의 4방위(-X, +X, -Y, +Y) 문맥 서명(점유 구역 vs 빈 배경 Free-Space 구역)을
    /// 후보 자세(pose.Angle, pose.Scale)로 회전·배율 변환했을 때 빈 배경 구역에 에지가 침범하거나 점유 구역이 비어 있으면 오검출로 판정한다.
    /// 특정 모서리 위치(좌하단/좌상단 등)나 회전 각도에 종속되지 않는 위치·배율·회전 불변(SE(2)-Equivariant) 검증이다.
    /// </summary>
    private static bool HasMirrorCounterpartIntrusion(GradientImage image, ShapeModel model, MatchPose pose,
        double cos, double sin, System.Collections.Immutable.ImmutableArray<byte> asymFlags,
        double asymXSum, int asymXCount, double asymYSum, int asymYCount)
    {
        var features = model.Levels[0].Features;
        var symCenter = model.SymmetryCenter;
        if (asymYCount > 0 && (asymYSum / asymYCount) < (asymYCount == 1 ? 0.50 : 0.78))
        {
            var mirrorYSum = 0d;
            var flipHits = 0;
            for (var i = 0; i < features.Length; i++)
            {
                if ((asymFlags[i] & 2) == 0)
                {
                    continue;
                }
                var f = features[i];
                var mx = f.X;
                var my = (2 * symCenter.Y) - f.Y;
                var mnx = f.NormalX;
                var mny = -f.NormalY;
                var sx = pose.X + (pose.Scale * ((cos * mx) - (sin * my))) - 0.5;
                var sy = pose.Y + (pose.Scale * ((sin * mx) + (cos * my))) - 0.5;
                var snx = (cos * mnx) - (sin * mny);
                var sny = (sin * mnx) + (cos * mny);
                image.SampleDirection(sx, sy, out var gx, out var gy);
                var dot = (snx * gx) + (sny * gy);
                var mirrorScore = Math.Abs(dot);
                mirrorYSum += mirrorScore;
                if (mirrorScore >= 0.50)
                {
                    flipHits++;
                }
            }
            if ((mirrorYSum / asymYCount) > (asymYSum / asymYCount) + 0.05 || flipHits >= Math.Max(1, (asymYCount + 1) / 2))
            {
                return true;
            }
        }
        if (asymXCount > 0 && (asymXSum / asymXCount) < (asymXCount == 1 ? 0.50 : 0.78))
        {
            var mirrorXSum = 0d;
            var flipHits = 0;
            for (var i = 0; i < features.Length; i++)
            {
                if ((asymFlags[i] & 1) == 0)
                {
                    continue;
                }
                var f = features[i];
                var mx = (2 * symCenter.X) - f.X;
                var my = f.Y;
                var mnx = -f.NormalX;
                var mny = f.NormalY;
                var sx = pose.X + (pose.Scale * ((cos * mx) - (sin * my))) - 0.5;
                var sy = pose.Y + (pose.Scale * ((sin * mx) + (cos * my))) - 0.5;
                var snx = (cos * mnx) - (sin * mny);
                var sny = (sin * mnx) + (cos * mny);
                image.SampleDirection(sx, sy, out var gx, out var gy);
                var dot = (snx * gx) + (sny * gy);
                var mirrorScore = Math.Abs(dot);
                mirrorXSum += mirrorScore;
                if (mirrorScore >= 0.50)
                {
                    flipHits++;
                }
            }
            if ((mirrorXSum / asymXCount) > (asymXSum / asymXCount) + 0.05 || flipHits >= Math.Max(1, (asymXCount + 1) / 2))
            {
                return true;
            }
        }

        // 모델 좌표계 4방위(-X, +X, -Y, +Y) 문맥 점유/빈 공간(Free-Space) 회전 공변(SE(2)-Equivariant) 검증:
        // 티칭 당시 존재했던 방위(OccupiedContextSectorMask)와 비어 있던 방위(EmptyContextSectorMask)를
        // 후보 자세(pose.Angle, pose.Scale)로 그대로 회전·배율 변환하여 장면과 비교한다.
        var occupiedMask = model.OccupiedContextSectorMask;
        var emptyMask = model.EmptyContextSectorMask;
        if (occupiedMask == 0 && emptyMask == 0)
        {
            return false;
        }
        var coreDiameter = model.SymmetryCoreDiameter > 1.0 ? model.SymmetryCoreDiameter : 18.0;
        var rxBase = model.ContextSectorRadii.X > 1.0 ? model.ContextSectorRadii.X : coreDiameter * 0.64;
        var ryBase = model.ContextSectorRadii.Y > 1.0 ? model.ContextSectorRadii.Y : rxBase;
        var minBase = Math.Min(rxBase, ryBase);
        var tangentStep = Math.Max(1.0, coreDiameter * 0.11);
        var radialStep = Math.Max(0.8, coreDiameter * 0.075);

        // 4방위(0: -X, 1: +X, 2: -Y, 3: +Y) 각각의 장면 접선 에지 히트 수를 대칭적으로 측정한다.
        Span<int> hitsAtOwnRadius = stackalloc int[4];
        Span<int> hitsInBand = stackalloc int[4];
        Span<int> hitsInFarBand = stackalloc int[4];
        for (var dir = 0; dir < 4; dir++)
        {
            var isXDir = dir < 2;
            var sign = (dir & 1) == 0 ? -1.0 : 1.0;
            var rOwn = isXDir ? rxBase : ryBase;
            // 해당 방위의 접선 에지가 가지는 모델 법선 방향:
            // ±X 방위(세로 테두리)의 법선은 모델 X축 (cos, sin), ±Y 방위(가로 테두리)의 법선은 모델 Y축 (-sin, cos)
            var expectedNx = isXDir ? cos : -sin;
            var expectedNy = isXDir ? sin : cos;

            for (var tStep = -2; tStep <= 2; tStep++)
            {
                var tangentOffset = tStep * tangentStep;
                for (var rIdx = -1; rIdx <= 1; rIdx++)
                {
                    var r = rOwn + (rIdx * radialStep);
                    var mx = isXDir ? symCenter.X + (sign * r) : symCenter.X + tangentOffset;
                    var my = isXDir ? symCenter.Y + tangentOffset : symCenter.Y + (sign * r);
                    var sx = pose.X + (pose.Scale * ((cos * mx) - (sin * my))) - 0.5;
                    var sy = pose.Y + (pose.Scale * ((sin * mx) + (cos * my))) - 0.5;
                    image.SampleDirection(sx, sy, out var gx, out var gy);
                    if (Math.Abs((expectedNx * gx) + (expectedNy * gy)) >= 0.65)
                    {
                        hitsAtOwnRadius[dir]++;
                        break;
                    }
                }
                for (var rIdx = 0; rIdx <= 5; rIdx++)
                {
                    var r = minBase + (rIdx * radialStep);
                    var mx = isXDir ? symCenter.X + (sign * r) : symCenter.X + tangentOffset;
                    var my = isXDir ? symCenter.Y + tangentOffset : symCenter.Y + (sign * r);
                    var sx = pose.X + (pose.Scale * ((cos * mx) - (sin * my))) - 0.5;
                    var sy = pose.Y + (pose.Scale * ((sin * mx) + (cos * my))) - 0.5;
                    image.SampleDirection(sx, sy, out var gx, out var gy);
                    if (Math.Abs((expectedNx * gx) + (expectedNy * gy)) >= 0.65)
                    {
                        hitsInBand[dir]++;
                        if (rIdx >= 2)
                        {
                            hitsInFarBand[dir]++;
                        }
                    }
                }
            }
        }

        // 1) 티칭된 모델에서 분명히 존재했던 문맥 방위(occupiedMask)는 회전된 장면에서도 자신의 모델 반경(rOwn)에서 지지되어야 한다.
        for (var dir = 0; dir < 4; dir++)
        {
            if ((occupiedMask & (1 << dir)) != 0 && hitsAtOwnRadius[dir] < 3)
            {
                return true;
            }
        }

        // 2) 티칭된 모델에서 비어 있던 학습 ROI 내부 배경 방위(emptyMask)에 강한 테두리 에지가 침범하고
        //    반대편 대칭 방위(oppositeDir)에는 에지가 없으면 거울 반전된 반대편 모서리이거나 잘못된 90도/180도 회전 정합이므로 차단한다.
        for (var dir = 0; dir < 4; dir++)
        {
            if ((emptyMask & (1 << dir)) == 0)
            {
                continue;
            }
            var oppositeDir = dir ^ 1;
            var hasOppositeOccupied = (occupiedMask & (1 << oppositeDir)) != 0;
            if (!hasOppositeOccupied)
            {
                if (hitsInBand[dir] >= 3 && hitsInBand[oppositeDir] <= 1)
                {
                    return true;
                }
                if (hitsInFarBand[dir] >= 3 && hitsInFarBand[oppositeDir] <= 1)
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>비가림 영역 내 지지된 특징점들의 법선 구조 텐서 고유값 비율(λ_min / λ_max)을 검사해 단일 1D 직선 에지 오검출을 차단한다.</summary>
    internal static bool HasOrthogonalNormalDiversity(GradientImage image, ReadOnlySpan<ModelFeature> features,
        ReadOnlySpan<byte> partitions, byte occludedMask, MatchPose pose, ContrastPolarity polarity)
    {
        var cos = Math.Cos(pose.Angle);
        var sin = Math.Sin(pose.Angle);
        var txx = 0d;
        var tyy = 0d;
        var txy = 0d;
        var supportedCount = 0;
        for (var i = 0; i < features.Length; i++)
        {
            if (!partitions.IsEmpty && (occludedMask & (1 << partitions[i])) != 0)
            {
                continue;
            }
            var f = features[i];
            var fx = pose.X + (pose.Scale * ((cos * f.X) - (sin * f.Y))) - 0.5;
            var fy = pose.Y + (pose.Scale * ((sin * f.X) + (cos * f.Y))) - 0.5;
            var fnx = (cos * f.NormalX) - (sin * f.NormalY);
            var fny = (sin * f.NormalX) + (cos * f.NormalY);
            image.SampleDirection(fx, fy, out var fgx, out var fgy);
            var fdot = (fnx * fgx) + (fny * fgy);
            var fsc = polarity == ContrastPolarity.IgnoreLocal ? Math.Abs(fdot) : Math.Max(0, fdot);
            if (fsc >= 0.65)
            {
                txx += fsc * f.NormalX * f.NormalX;
                tyy += fsc * f.NormalY * f.NormalY;
                txy += fsc * f.NormalX * f.NormalY;
                supportedCount++;
            }
        }
        if (supportedCount < 6)
        {
            return false;
        }
        var trace = txx + tyy;
        var diff = txx - tyy;
        var disc = Math.Sqrt((diff * diff) + (4.0 * txy * txy));
        var lambdaMax = 0.5 * (trace + disc);
        var lambdaMin = Math.Max(0.0, 0.5 * (trace - disc));
        return lambdaMax > 1e-9 && (lambdaMin / lambdaMax) >= 0.035;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AccumulateChainsAndAsym(
        bool hasChains,
        System.Collections.Immutable.ImmutableArray<byte> chains,
        int fi,
        double dot,
        double score,
        double rigidScore,
        ref double outerSigned,
        ref double outerSum,
        ref int outerCount,
        ref double innerSigned,
        ref double innerSum,
        ref int innerCount,
        bool hasAsymFlags,
        System.Collections.Immutable.ImmutableArray<byte> asymFlags,
        ref double asymXSum,
        ref int asymXCount,
        ref double asymYSum,
        ref int asymYCount,
        ref double asymR90Sum,
        ref int asymR90Count,
        ref double asymR270Sum,
        ref int asymR270Count)
    {
        if (hasChains)
        {
            if (chains[fi] == 0)
            {
                outerSigned += dot;
                outerSum += score;
                outerCount++;
            }
            else
            {
                innerSigned += dot;
                innerSum += score;
                innerCount++;
            }
        }
        if (hasAsymFlags)
        {
            // 비대칭 문맥 키(AsymX, AsymY, AsymR90, AsymR270)는 ±1px 탄성 보정(score)이나 음수 감점(hasChains)으로 왜곡되지 않게 순수 강체 정합도(signedRigid)로 평가한다.
            var signedRigid = dot < 0 ? 0.0 : rigidScore;
            var af = asymFlags[fi];
            if ((af & 1) != 0)
            {
                asymXSum += signedRigid;
                asymXCount++;
            }
            if ((af & 2) != 0)
            {
                asymYSum += signedRigid;
                asymYCount++;
            }
            if ((af & 4) != 0)
            {
                asymR90Sum += signedRigid;
                asymR90Count++;
            }
            if ((af & 8) != 0)
            {
                asymR270Sum += signedRigid;
                asymR270Count++;
            }
        }
    }

    /// <summary>가려진 공간 블록 집합(mask)이 2D 인접 그래프(adjacency) 위에서 하나의 연결 요소(Connected Component)를 이루는지 판정한다.</summary>
    internal static bool IsContiguousPartitionMask(byte mask, System.Collections.Immutable.ImmutableArray<byte> adjacency)
    {
        var k = BitOperations.PopCount(mask);
        if (k <= 1)
        {
            return k == 1;
        }
        if (adjacency.IsDefault || adjacency.Length < 8)
        {
            return false;
        }
        var start = BitOperations.TrailingZeroCount(mask);
        byte visited = (byte)(1 << start);
        var queue = visited;
        while (queue != 0)
        {
            var current = BitOperations.TrailingZeroCount(queue);
            queue &= (byte)~(1 << current);
            var neighbors = (byte)(adjacency[current] & mask & ~visited);
            visited |= neighbors;
            queue |= neighbors;
        }
        return visited == mask;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static (MatchPose Pose, VerifiedScore Score) Optimize(GradientImage image, ShapeModel model,
        MatchPose initial, SearchOptions options, CancellationToken token, int level = 0)
    {
        var pose = initial;
        var hasElasticPatches = level == 0 && model.FineDetail?.HasElasticPatches == true;
        var hasUniformGrid = level == 0 && model.FineDetail?.HasUniformGridElasticity == true;
        var best = Score(image, model, pose, level, allowElastic: false, options.MaxOcclusionRatio, options.StrictContext);
        var levelData = model.Levels[level];
        var radius = levelData.MaxRadius;
        var maximumNormalLength = levelData.MaxNormalLength;
        var featureUpperBound = maximumNormalLength * image.DirectionLengthUpperBound * 1.000000001;
        var rotation = Math.Min(model.Options.AngleStepDegrees * Math.PI / 360, 2 / (radius * pose.Scale));
        var scale = model.Options.MaximumScale > model.Options.MinimumScale ? Math.Min(model.Options.ScaleStep / 2, 2 / radius) : 0;
        for (var resolution = 0; resolution < 5; resolution++)
        {
            var translation = (hasElasticPatches ? 1d : 2d) / (1 << resolution);
            for (var iteration = 0; iteration < 5; iteration++)
            {
                token.ThrowIfCancellationRequested();
                var changed = false;
                for (var dimension = 0; dimension < 4; dimension++)
                {
                    // 고정 배율의 두 제안은 현재 자세와 같으므로 점수를 다시 계산할 필요가 없다.
                    if (dimension == 3 && scale == 0)
                    {
                        continue;
                    }
                    var original = pose;
                    for (var sign = -1; sign <= 1; sign += 2)
                    {
                        var proposal = dimension switch
                        {
                            0 => original with { X = original.X + (sign * translation) },
                            1 => original with { Y = original.Y + (sign * translation) },
                            2 => original with { Angle = original.Angle + (sign * rotation) },
                            _ => original with { Scale = original.Scale + (sign * scale) }
                        };
                        if (hasElasticPatches &&
                            (Math.Abs(proposal.X - initial.X) > 1.25 ||
                             Math.Abs(proposal.Y - initial.Y) > 1.25 ||
                             Math.Abs(proposal.Angle - initial.Angle) > 0.045))
                        {
                            continue;
                        }
                        if (!Allowed(proposal, model, options))
                        {
                            continue;
                        }
                        var score = ScoreProposal(image, model, proposal, level, best.Score + 1e-10, featureUpperBound, options.Greediness, allowElastic: false, options.MaxOcclusionRatio, options.StrictContext);
                        if (score.Score > best.Score + 1e-10)
                        {
                            pose = proposal;
                            best = score;
                            changed = true;
                        }
                    }
                }
                if (!changed)
                {
                    break;
                }
            }
            if (model.FineDetail is not null && level == 0 && resolution == 1 && options.Verify &&
                best.Score < (hasElasticPatches ? options.MinimumScore - 0.30 : options.MinimumScore - 0.18))
            {
                break;
            }
            rotation /= 2;
            scale /= 2;
        }
        if (level == 0 && !hasElasticPatches && (hasUniformGrid || UsesLocalTolerance(model) || !model.FeatureAsymmetryFlags.IsDefault))
        {
            best = Score(image, model, pose, level, allowElastic: true, options.MaxOcclusionRatio, options.StrictContext);
        }
        if (hasElasticPatches && best.Score >= Math.Min(0.42, options.MinimumScore - 0.30))
        {
            best = Score(image, model, pose, level, allowElastic: true, options.MaxOcclusionRatio, options.StrictContext);
            var initialElastic = Score(image, model, initial, level, allowElastic: true, options.MaxOcclusionRatio, options.StrictContext);
            if (initialElastic.Score > best.Score + 1e-10)
            {
                pose = initial;
                best = initialElastic;
            }
        }
        return (pose, best);
    }

    /// <summary>복사된 배열에서도 서브픽셀 위상이 다른 큰 모델(미세 세부 검증 없음)의 최종 점수만 국소 허용 이동으로 평가한다.</summary>
    private static bool UsesLocalTolerance(ShapeModel model)
        => model.FineDetail is null && model.Options.BuildClutterModel && model.Options.UseFineDetailValidation;

    /// <summary>
    /// 최종 검출 자세에서 레벨 0 특징점 각각의 방향 일치 여부를 평가해
    /// 불일치(누락/반전) 특징점 좌표와 정상 일치한 비대칭 구분 키 좌표를 반환한다.
    /// </summary>
    internal static (System.Collections.Immutable.ImmutableArray<ImagePoint> Unmatched, System.Collections.Immutable.ImmutableArray<ImagePoint> KeyMatched) EvaluateFeatureSupport(
        GradientImage image, ShapeModel model, MatchPose pose)
    {
        var features = model.Levels[0].Features;
        if (features.IsDefaultOrEmpty)
        {
            return ([], []);
        }
        var cos = Math.Cos(pose.Angle);
        var sin = Math.Sin(pose.Angle);
        var asymFlags = !model.FeatureAsymmetryFlags.IsDefault && model.FeatureAsymmetryFlags.Length == features.Length
            ? model.FeatureAsymmetryFlags
            : default;
        var unmatched = System.Collections.Immutable.ImmutableArray.CreateBuilder<ImagePoint>();
        var keyMatched = System.Collections.Immutable.ImmutableArray.CreateBuilder<ImagePoint>();
        for (var i = 0; i < features.Length; i++)
        {
            var f = features[i];
            var wx = pose.X + (pose.Scale * ((cos * f.X) - (sin * f.Y)));
            var wy = pose.Y + (pose.Scale * ((sin * f.X) + (cos * f.Y)));
            var nx = (cos * f.NormalX) - (sin * f.NormalY);
            var ny = (sin * f.NormalX) + (cos * f.NormalY);
            image.SampleDirection(wx - 0.5, wy - 0.5, out var gx, out var gy);
            var dot = (nx * gx) + (ny * gy);
            var score = model.Options.Polarity == ContrastPolarity.IgnoreLocal ? Math.Abs(dot) : Math.Max(0, dot);
            if (score < 0.65)
            {
                // 0.5px 국소 탄성 보정 위치도 함께 확인한다.
                for (var step = -2; step <= 2; step++)
                {
                    if (step == 0)
                    {
                        continue;
                    }
                    var t = step * 0.5;
                    image.SampleDirection(wx - 0.5 + (t * nx), wy - 0.5 + (t * ny), out var sx, out var sy);
                    var sdot = (nx * sx) + (ny * sy);
                    var shifted = model.Options.Polarity == ContrastPolarity.IgnoreLocal ? Math.Abs(sdot) : Math.Max(0, sdot);
                    if (shifted > score)
                    {
                        score = shifted;
                    }
                }
            }
            if (score < 0.65)
            {
                unmatched.Add(new(wx, wy));
            }
            else if (!asymFlags.IsDefault && (asymFlags[i] & 3) != 0)
            {
                keyMatched.Add(new(wx, wy));
            }
        }
        return (
            unmatched.Count == 0 ? [] : unmatched.ToImmutable(),
            keyMatched.Count == 0 ? [] : keyMatched.ToImmutable()
        );
    }

    internal static bool Allowed(MatchPose pose, ShapeModel model, SearchOptions options)
        => pose.Scale >= model.Options.MinimumScale && pose.Scale <= model.Options.MaximumScale &&
        pose.Angle - model.TrainingReferenceAngle >= (model.Options.MinimumAngleDegrees * Math.PI / 180) - 1e-12 &&
        pose.Angle - model.TrainingReferenceAngle <= (model.Options.MaximumAngleDegrees * Math.PI / 180) + 1e-12;
}
