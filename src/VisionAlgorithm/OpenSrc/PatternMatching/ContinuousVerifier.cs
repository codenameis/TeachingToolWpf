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
        var elasticFeatures = allowElastic && level == 0 && (model.FineDetail?.HasElasticPatches == true || UsesLocalTolerance(model));
        // 큰 모델은 ±1px, 작은 세부 모델은 기존 ±1.5px 범위에서 특징별 국소 이동을 허용한다.
        var elasticSpan = model.FineDetail?.HasElasticPatches == true ? 3 : 2;
        Span<double> featureScores = elasticFeatures && count <= 512 ? stackalloc double[count] : default;
        // 동심 윤곽(바깥 0 / 안쪽 1)이 있는 모델은 그룹별 평균을 따로 모아 안쪽 윤곽 결손을 검사한다. ClutterWeight와 무관하다.
        var groups = level == 0 && !model.FeatureGroups.IsDefault && model.FeatureGroups.Length == count ? model.FeatureGroups : default;
        var hasGroups = !groups.IsDefault;
        var outerSum = 0d;
        var innerSum = 0d;
        var outerCount = 0;
        var innerCount = 0;
        var outerSigned = 0d;
        var innerSigned = 0d;
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
            if (hasGroups && model.Options.Polarity == ContrastPolarity.Preserve && dot < 0)
            {
                // 극성 역전(밝은 링에 어두운 구멍)은 0점이 아니라 음수로 반영한다.
                score = Math.Max(-0.5, 0.5 * dot);
            }
            if (elasticFeatures && score < 0.92)
            {
                for (var iy = -3; iy <= 3; iy++)
                {
                    var dy = iy * 0.5;
                    for (var ix = -3; ix <= 3; ix++)
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
            if (hasGroups)
            {
                if (groups[index] == 0)
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
                var effectiveMin = elasticFeatures ? minimumScore * 0.80 : minimumScore;
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
        var meanScore = sum / count;
        if (!featureScores.IsEmpty && count >= 12)
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
                meanScore = (0.80 * keptMean) + (0.20 * meanScore);
            }
        }
        if (hasGroups && outerCount > 0 && innerCount > 0 && (outerSum / outerCount) >= 0.85 && (innerSum / innerCount) <= 0.35)
        {
            // 바깥 윤곽은 선명한데 안쪽 윤곽이 전 방향에서 사라진 민짜 구멍: 가림이 아니라 필수 형상 결손이다.
            meanScore *= 0.4;
        }
        else if (hasGroups && outerCount > 0 && innerCount > 0)
        {
            // 단면 구조 검사: 도넛은 바깥·안쪽 윤곽이 같은 부호(밝은 링)로 맞는다. 한쪽만 뒤집히면(회색 판 위의 검은 구멍 등)
            // 극성 무시 모드에서도 전체 반전과 구별해 구조 불일치로 감점한다.
            var outerMean = outerSigned / outerCount;
            var innerMean = innerSigned / innerCount;
            var weaker = Math.Min(Math.Abs(outerMean), Math.Abs(innerMean));
            var stronger = Math.Max(Math.Abs(outerMean), Math.Abs(innerMean));
            // 부호가 엇갈리거나, 한 윤곽의 부호 정합이 다른 윤곽의 절반에도 못 미치면 같은 단면 구조가 아니다.
            if ((outerMean * innerMean < 0 && weaker >= 0.15) || weaker < 0.5)
            {
                meanScore *= 0.4;
            }
        }
        return new(meanScore, supported / (double)count, System.Numerics.BitOperations.PopCount((uint)quadrants));
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static (MatchPose Pose, VerifiedScore Score) Optimize(GradientImage image, ShapeModel model,
        MatchPose initial, SearchOptions options, CancellationToken token, int level = 0)
    {
        var pose = initial;
        var hasElasticPatches = level == 0 && model.FineDetail?.HasElasticPatches == true;
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
        if (level == 0 && !hasElasticPatches && UsesLocalTolerance(model) && best.Score >= options.MinimumScore - 0.30)
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
