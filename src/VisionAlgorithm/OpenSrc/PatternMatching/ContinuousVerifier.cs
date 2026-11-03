using System.Numerics;
using System.Runtime.CompilerServices;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

internal readonly record struct VerifiedScore(double Score, double Support, int Quadrants);

internal static class ContinuousVerifier
{
    public static VerifiedScore Score(GradientImage image, ShapeModel model, MatchPose pose, int level = 0, bool allowElastic = true)
        => ScoreProposal(image, model, pose, level, double.NegativeInfinity, double.PositiveInfinity, 0, allowElastic);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static VerifiedScore ScoreProposal(GradientImage image, ShapeModel model, MatchPose pose, int level,
        double minimumScore, double featureUpperBound, double greediness, bool allowElastic)
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
        var elasticFeatures = allowElastic && level == 0 && (hasElasticPatches || hasUniformGrid || UsesLocalTolerance(model));
        var elasticSpan = hasUniformGrid ? 2 : 3;
        Span<double> featureScores = elasticFeatures && !hasUniformGrid && count <= 512 ? stackalloc double[count] : default;
        // 연결 윤곽 체인(외곽 0 / 내부 1..K-1)이 있는 모델은 체인별 평균과 부호 정합을 검사한다. ClutterWeight와 무관하다.
        var chains = level == 0 && !model.FeatureChains.IsDefault && model.FeatureChains.Length == count ? model.FeatureChains : default;
        var hasChains = !chains.IsDefault;
        var asymFlags = level == 0 && !model.FeatureAsymmetryFlags.IsDefault && model.FeatureAsymmetryFlags.Length == count ? model.FeatureAsymmetryFlags : default;
        var hasAsymFlags = !asymFlags.IsDefault;
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
        for (var index = 0; index < count; index++)
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
            if (hasChains)
            {
                if (chains[index] == 0)
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
                var af = asymFlags[index];
                if ((af & 1) != 0)
                {
                    asymXSum += score;
                    asymXCount++;
                }
                if ((af & 2) != 0)
                {
                    asymYSum += score;
                    asymYCount++;
                }
                if ((af & 4) != 0)
                {
                    asymR90Sum += score;
                    asymR90Count++;
                }
                if ((af & 8) != 0)
                {
                    asymR270Sum += score;
                    asymR270Count++;
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
                var effectiveMin = (elasticFeatures || (hasPartitions && !hasUniformGrid && !hasElasticPatches && model.Options.UseFineDetailValidation))
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
        if (hasAsymFlags)
        {
            if (asymXCount > 0)
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
            if (minAsymKeyScore < 0.72)
            {
                asymKeyMissing = true;
            }
        }
        if (!asymKeyMissing && !featureScores.IsEmpty && count >= 12)
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
        // 8개 공간 블록 중 1~3개의 서로 인접한 블록만 가려지고(평균 < 0.48), 나머지 5~7개 비가림 블록(>=60%)의 평균 정합도가 >= 0.84일 때 작동한다.
        // 단, 비대칭 구분 키(AsymX/AsymY)가 결손된 후보는 가림이 아니라 플립된 잘못된 모서리이므로 가림 보정을 절대 적용하지 않는다.
        if (!asymKeyMissing && hasPartitions && !hasUniformGrid && !hasElasticPatches && model.Options.BuildClutterModel && model.Options.UseFineDetailValidation)
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
                if (pMean < 0.48)
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
            if (occludedBlocks is >= 1 and <= 3 && inlierBlocks >= 5 && inlierCount >= count * 0.60 &&
                (inlierSum / inlierCount) >= (allowElastic ? 0.90 : 0.84) &&
                IsContiguousPartitionMask(occludedMask, model.PartitionAdjacency))
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
        if (hasAsymFlags && minAsymKeyScore < 0.78)
        {
            // 비대칭 키 결손 페널티(Flip Rejection): 대칭 코어 점수가 높아도 AsymX/AsymY/AsymRot 키 정합도가 낮으면 달성률을 직접 곱한다.
            var keyAchievement = Math.Clamp(minAsymKeyScore / 0.78, 0.0, 1.0);
            meanScore *= keyAchievement * keyAchievement;
            if (minAsymKeyScore < 0.68)
            {
                meanScore = Math.Min(meanScore, 0.54);
            }
        }
        return new(meanScore, Math.Min(1.0, supported / (double)count), BitOperations.PopCount((uint)quadrants));
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
        var best = Score(image, model, pose, level, allowElastic: false);
        var features = model.Levels[level].Features;
        var radius = 0d;
        var maximumNormalLength = 0d;
        for (var index = 0; index < features.Length; index++)
        {
            var feature = features[index];
            var featureRadius = Math.Sqrt((feature.X * feature.X) + (feature.Y * feature.Y));
            if (featureRadius > radius)
            {
                radius = featureRadius;
            }
            var normalLength = Math.Sqrt((feature.NormalX * feature.NormalX) + (feature.NormalY * feature.NormalY));
            if (normalLength > maximumNormalLength)
            {
                maximumNormalLength = normalLength;
            }
        }
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
                        var score = ScoreProposal(image, model, proposal, level, best.Score + 1e-10, featureUpperBound, options.Greediness, allowElastic: false);
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
        if (level == 0 && !hasElasticPatches && (hasUniformGrid || UsesLocalTolerance(model)) &&
            best.Score >= (hasUniformGrid ? 0.95 : options.MinimumScore - 0.30))
        {
            best = Score(image, model, pose, level, allowElastic: true);
        }
        if (hasElasticPatches && best.Score >= Math.Min(0.42, options.MinimumScore - 0.30))
        {
            best = Score(image, model, pose, level, allowElastic: true);
            var initialElastic = Score(image, model, initial, level, allowElastic: true);
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

    internal static bool Allowed(MatchPose pose, ShapeModel model, SearchOptions options)
        => pose.Scale >= model.Options.MinimumScale && pose.Scale <= model.Options.MaximumScale &&
        pose.Angle - model.TrainingReferenceAngle >= (model.Options.MinimumAngleDegrees * Math.PI / 180) - 1e-12 &&
        pose.Angle - model.TrainingReferenceAngle <= (model.Options.MaximumAngleDegrees * Math.PI / 180) + 1e-12;
}
