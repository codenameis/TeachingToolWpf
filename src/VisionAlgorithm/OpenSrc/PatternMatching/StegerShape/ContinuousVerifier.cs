// File: src/VisionAlgorithm/OpenSrc/PatternMatching/StegerShape/ContinuousVerifier.cs
using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

internal static class ContinuousVerifier
{
    public static VerifiedScore Score(
        GradientImage image,
        ShapeModel model,
        MatchPose pose,
        int level = 0,
        bool allowElastic = true,
        double maxOcclusionRatio = 0,
        bool strictContext = true)
        => ScoreProposal(
            image,
            model,
            pose,
            level,
            double.NegativeInfinity,
            double.PositiveInfinity,
            0,
            allowElastic,
            maxOcclusionRatio,
            strictContext);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static VerifiedScore ScoreProposal(
        GradientImage image,
        ShapeModel model,
        MatchPose pose,
        int level,
        double minimumScore,
        double featureUpperBound,
        double greediness,
        bool allowElastic,
        double maxOcclusionRatio = 0,
        bool strictContext = true)
    {
        var levelData = model.Levels[level];
        var features = levelData.Features;
        var count = features.Length;
        var thresholds = VerificationThresholds.Create(
            model,
            level,
            count,
            minimumScore,
            featureUpperBound,
            greediness,
            allowElastic,
            maxOcclusionRatio,
            strictContext,
            UsesLocalTolerance(model));

        Span<double> featureScores = thresholds.ElasticFeatures &&
                                     (!thresholds.HasUniformGrid || !thresholds.HasOccupiedContext) &&
                                     count <= VerificationThresholds.MaxStackFeatureScoreCount
            ? stackalloc double[count]
            : default;

        Span<double> asymQuadSum = thresholds.HasAsymFlags ? stackalloc double[VerificationThresholds.SymmetryQuadrantCount] : default;
        Span<int> asymQuadCount = thresholds.HasAsymFlags ? stackalloc int[VerificationThresholds.SymmetryQuadrantCount] : default;
        Span<double> asymQuadXSum = thresholds.HasAsymFlags ? stackalloc double[VerificationThresholds.SymmetryQuadrantCount] : default;
        Span<int> asymQuadXCount = thresholds.HasAsymFlags ? stackalloc int[VerificationThresholds.SymmetryQuadrantCount] : default;
        Span<double> asymQuadYSum = thresholds.HasAsymFlags ? stackalloc double[VerificationThresholds.SymmetryQuadrantCount] : default;
        Span<int> asymQuadYCount = thresholds.HasAsymFlags ? stackalloc int[VerificationThresholds.SymmetryQuadrantCount] : default;

        Span<double> partitionRigidSum = thresholds.HasPartitions ? stackalloc double[VerificationThresholds.SpatialPartitionCount] : default;
        Span<double> partitionElasticSum = thresholds.HasPartitions ? stackalloc double[VerificationThresholds.SpatialPartitionCount] : default;
        Span<int> partitionCount = thresholds.HasPartitions ? stackalloc int[VerificationThresholds.SpatialPartitionCount] : default;
        Span<int> partitionSupported = thresholds.HasPartitions ? stackalloc int[VerificationThresholds.SpatialPartitionCount] : default;
        Span<double> partitionNormalShiftSum = thresholds.HasPartitions && thresholds.HasUniformGrid
            ? stackalloc double[VerificationThresholds.SpatialPartitionCount]
            : default;
        Span<double> partitionNormalShiftSq = thresholds.HasPartitions && thresholds.HasUniformGrid
            ? stackalloc double[VerificationThresholds.SpatialPartitionCount]
            : default;
        Span<int> partitionNormalShifted = thresholds.HasPartitions && thresholds.HasUniformGrid
            ? stackalloc int[VerificationThresholds.SpatialPartitionCount]
            : default;

        var accumulators = new VerificationAccumulators
        {
            Image = image,
            Model = model,
            Pose = pose,
            LevelData = levelData,
            Features = features,
            Count = count,
            Cos = Math.Cos(pose.Angle),
            Sin = Math.Sin(pose.Angle),
            Chains = thresholds.HasChains ? model.FeatureChains : default,
            AsymFlags = thresholds.HasAsymFlags ? model.FeatureAsymmetryFlags : default,
            Partitions = thresholds.HasPartitions ? model.FeaturePartitions : default,
            SymCenter = model.SymmetryCenter,
            FeatureScores = featureScores,
            AsymQuadSum = asymQuadSum,
            AsymQuadCount = asymQuadCount,
            AsymQuadXSum = asymQuadXSum,
            AsymQuadXCount = asymQuadXCount,
            AsymQuadYSum = asymQuadYSum,
            AsymQuadYCount = asymQuadYCount,
            PartitionRigidSum = partitionRigidSum,
            PartitionElasticSum = partitionElasticSum,
            PartitionCount = partitionCount,
            PartitionSupported = partitionSupported,
            PartitionNormalShiftSum = partitionNormalShiftSum,
            PartitionNormalShiftSq = partitionNormalShiftSq,
            PartitionNormalShifted = partitionNormalShifted
        };

        // [Perf Why]: Dispatch struct stages via constrained generic RunStage<TStage> so JIT devirtualizes & inlines every stage with zero allocations.
        if (!RunStage<RigidSimdAccumulator>(ref accumulators, in thresholds) ||
            !RunStage<ElasticFeatureAccumulator>(ref accumulators, in thresholds))
        {
            return new VerifiedScore(double.NegativeInfinity, 0, 0);
        }

        AsymmetryKeyCheck.SummarizeKeyScores(ref accumulators, in thresholds);
        RunStage<PartitionDeadBlockPenalty>(ref accumulators, in thresholds);
        RunStage<TrimmedMeanBoost>(ref accumulators, in thresholds);
        RunStage<ContiguousOcclusionCompensation>(ref accumulators, in thresholds);
        RunStage<ChainConsistencyCheck>(ref accumulators, in thresholds);
        RunStage<AsymmetryKeyCheck>(ref accumulators, in thresholds);

        return new VerifiedScore(
            accumulators.MeanScore,
            Math.Min(1.0, accumulators.Supported / (double)count),
            BitOperations.PopCount((uint)accumulators.Quadrants));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool RunStage<TStage>(ref VerificationAccumulators accumulators, in VerificationThresholds thresholds)
        where TStage : struct, IVerificationStage
        => default(TStage).Evaluate(ref accumulators, in thresholds);

    /// <summary>비가림 영역 내 지지된 특징점들의 법선 구조 텐서 고유값 비율(λ_min / λ_max)을 검사해 단일 1D 직선 에지 오검출을 차단한다.</summary>
    internal static bool HasOrthogonalNormalDiversity(
        GradientImage image,
        ReadOnlySpan<ModelFeature> features,
        ReadOnlySpan<byte> partitions,
        byte occludedMask,
        MatchPose pose,
        ContrastPolarity polarity)
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

    /// <summary>가려진 공간 블록 집합(mask)이 2D 인접 그래프(adjacency) 위에서 하나의 연결 요소(Connected Component)를 이루는지 판정한다.</summary>
    internal static bool IsContiguousPartitionMask(byte mask, ImmutableArray<byte> adjacency)
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
    public static (MatchPose Pose, VerifiedScore Score) Optimize(
        GradientImage image,
        ShapeModel model,
        MatchPose initial,
        ShapeSearchOptions options,
        CancellationToken token,
        int level = 0)
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

                        var score = ScoreProposal(
                            image,
                            model,
                            proposal,
                            level,
                            best.Score + 1e-10,
                            featureUpperBound,
                            options.Greediness,
                            allowElastic: false,
                            options.MaxOcclusionRatio,
                            options.StrictContext);
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

            if (model.FineDetail is not null &&
                level == 0 &&
                resolution == 1 &&
                options.Verify &&
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

    private static bool UsesLocalTolerance(ShapeModel model)
        => model.FineDetail is null && model.Options.BuildClutterModel && model.Options.UseFineDetailValidation;

    internal static (ImmutableArray<ImagePoint> Unmatched, ImmutableArray<ImagePoint> KeyMatched) EvaluateFeatureSupport(
        GradientImage image,
        ShapeModel model,
        MatchPose pose)
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
        var unmatched = ImmutableArray.CreateBuilder<ImagePoint>();
        var keyMatched = ImmutableArray.CreateBuilder<ImagePoint>();
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

    internal static bool Allowed(MatchPose pose, ShapeModel model, ShapeSearchOptions options)
        => pose.Scale >= model.Options.MinimumScale &&
           pose.Scale <= model.Options.MaximumScale &&
           pose.Angle - model.TrainingReferenceAngle >= (model.Options.MinimumAngleDegrees * Math.PI / 180) - 1e-12 &&
           pose.Angle - model.TrainingReferenceAngle <= (model.Options.MaximumAngleDegrees * Math.PI / 180) + 1e-12;
}
