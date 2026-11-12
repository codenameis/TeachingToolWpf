using System.Runtime.CompilerServices;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

/// <summary>
/// Tracks and refines coarse pose candidates down the image pyramid from top level to level 0,
/// bridging quantized map search with continuous gradient verification and fine-detail intensity consistency.
/// </summary>
internal static class PyramidCandidateTracker
{
    private const int LocalWindowSpan = 81;
    private const int LocalWindowRadius = 4;
    private const int MaxQuantizedStackFeatures = 512;
    private const double PreMapBucketReprobeThreshold = 0.78;
    private const double PostMapBucketReprobeThreshold = 0.72;
    private const double BucketImprovementMargin = 0.01;
    private const double AxisDecompositionMinShiftSquared = 0.25;
    private const double FineDetailCoarseCandidateGate = 0.88;
    private const double FineDetailInitialProbeGate = 0.85;
    private const double FineDetailCoarseDiscount = 0.08;
    private const double StrictOcclusionPhaseGate = 0.2;
    private const double ElasticPatchDetailReprobeGate = 0.70;
    private const double AsymmetryDetailReprobeGate = 0.85;
    private const double MaxDetailRefineDriftSquared = 0.36;
    private const double TieEpsilon = 1e-9;
    private const double ProbeTieEpsilon = 1e-6;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static PoseCandidate Refine(
        PreparedSearchImage image,
        ShapeModel model,
        PoseCandidate candidate,
        ShapeSearchOptions options,
        CancellationToken token,
        bool optimizedLocalSearch = false)
    {
        Span<ushort> localScores = stackalloc ushort[LocalWindowSpan];
        Span<QuantizedFeature> quantizedBuffer = stackalloc QuantizedFeature[MaxQuantizedStackFeatures];
        var topLevel = model.Levels.Length - 1;

        for (var level = topLevel; level >= 0; level--)
        {
            token.ThrowIfCancellationRequested();
            var factor = model.Levels[level].Factor;
            var pose = candidate.Pose with
            {
                X = candidate.Pose.X / factor,
                Y = candidate.Pose.Y / factor
            };
            var cx = (int)Math.Round(pose.X - 0.5);
            var cy = (int)Math.Round(pose.Y - 0.5);

            if (options.Verify &&
                level == topLevel &&
                model.FineDetail is null &&
                !model.FeaturePartitions.IsDefault &&
                model.Options.UseFineDetailValidation &&
                model.Templates.Length > 1)
            {
                (pose, candidate) = ReprobeSameBucketBeforeMap(image.Gradients[level], model, candidate, pose, factor, level);
            }

            var cos = Math.Cos(pose.Angle);
            var sin = Math.Sin(pose.Angle);
            var modelFeatures = model.Levels[level].Features;
            scoped ReadOnlySpan<QuantizedFeature> features;
            if (optimizedLocalSearch &&
                level == topLevel &&
                Math.Abs(pose.Angle - model.Templates[candidate.Template].Angle) <= ProbeTieEpsilon)
            {
                features = model.Templates[candidate.Template].Levels[level].AsSpan();
            }
            else
            {
                for (var index = 0; index < modelFeatures.Length; index++)
                {
                    var feature = modelFeatures[index];
                    quantizedBuffer[index] = new QuantizedFeature(
                        (int)Math.Round(pose.Scale * ((cos * feature.X) - (sin * feature.Y))),
                        (int)Math.Round(pose.Scale * ((sin * feature.X) + (cos * feature.Y))),
                        OrientationMaps.Quantize(
                            (cos * feature.NormalX) - (sin * feature.NormalY),
                            (sin * feature.NormalX) + (cos * feature.NormalY)));
                }

                features = quantizedBuffer.Slice(0, modelFeatures.Length);
            }

            var bestSum = SearchLocalOrientationWindow(
                image.Maps[level],
                model,
                features,
                cx,
                cy,
                optimizedLocalSearch,
                options,
                localScores,
                out var plateauPose,
                out var hasPlateau);

            if (hasPlateau)
            {
                var mapPose = pose with { X = plateauPose.X, Y = plateauPose.Y };
                pose = ResolvePlateauShiftAlongPrincipalAxes(image.Gradients[level], model, pose, mapPose, level, options);
            }

            if (options.Verify)
            {
                if (level == topLevel &&
                    model.FineDetail is null &&
                    !model.FeaturePartitions.IsDefault &&
                    model.Options.UseFineDetailValidation &&
                    model.Templates.Length > 1)
                {
                    (pose, candidate) = ReprobeSameBucketAfterMap(image.Gradients[level], model, candidate, pose, level);
                }

                if (level == topLevel &&
                    model.Templates.Length > 1 &&
                    candidate.Score >= FineDetailCoarseCandidateGate &&
                    model.FineDetail is not null)
                {
                    (pose, candidate) = ReprobeFineDetailTemplates(image, model, candidate, pose, cx, cy, level, modelFeatures.Length);
                }

                var result = ContinuousVerifier.Optimize(image.Gradients[level], model, pose, options, token, level);
                if (level == 0)
                {
                    result = OccludedCandidateRecovery.RefineMajorAxisAndSubpixelCenter(image, model, result, options, token);
                }

                pose = result.Pose;
                if (level == 0)
                {
                    var (finalPose, verification, detailConsistency) = EvaluateLevelZeroContextAndFineDetail(
                        image,
                        model,
                        pose,
                        result.Score,
                        cx,
                        cy,
                        options,
                        token);
                    pose = finalPose;
                    candidate = candidate with
                    {
                        Verification = verification,
                        DetailConsistency = detailConsistency
                    };
                }
            }

            candidate = candidate with
            {
                Pose = pose with
                {
                    X = pose.X * factor,
                    Y = pose.Y * factor
                },
                Score = bestSum / (100d * features.Length)
            };
        }

        return candidate;
    }

    public static (VerifiedScore Score, MatchPose BestPose) ProbeTemplateScore(
        GradientImage gradient,
        ShapeModel model,
        MatchPose center,
        int level,
        bool fullSubCell = false)
    {
        var bestPose = center;
        var best = ContinuousVerifier.Score(gradient, model, center, level, allowElastic: false);
        var minScore = model.FineDetail is null ? 0.25 : 0.30;
        if (best.Score < minScore || best.Score >= FineDetailCoarseCandidateGate)
        {
            return (best, bestPose);
        }

        if (fullSubCell && model.FineDetail is null)
        {
            ReadOnlySpan<double> coarseGrid = [-1.5, 0.0, 1.5];
            foreach (var dy in coarseGrid)
            {
                foreach (var dx in coarseGrid)
                {
                    if (dx == 0 && dy == 0)
                    {
                        continue;
                    }

                    var proposal = center with { X = center.X + dx, Y = center.Y + dy };
                    var score = ContinuousVerifier.Score(gradient, model, proposal, level, allowElastic: false);
                    if (score.Score > best.Score + ProbeTieEpsilon)
                    {
                        best = score;
                        bestPose = proposal;
                    }
                }
            }

            if (best.Score < 0.35)
            {
                return (best, bestPose);
            }

            ReadOnlySpan<double> fullOffsets = [-1.5, -0.5, 0.0, 0.5, 1.5];
            foreach (var dy in fullOffsets)
            {
                foreach (var dx in fullOffsets)
                {
                    if ((dx == -1.5 || dx == 0.0 || dx == 1.5) && (dy == -1.5 || dy == 0.0 || dy == 1.5))
                    {
                        continue;
                    }

                    var proposal = center with { X = center.X + dx, Y = center.Y + dy };
                    var score = ContinuousVerifier.Score(gradient, model, proposal, level, allowElastic: false);
                    if (score.Score > best.Score + ProbeTieEpsilon)
                    {
                        best = score;
                        bestPose = proposal;
                    }
                }
            }

            if (best.Score < 0.34 || model.Templates.Length <= 1)
            {
                return (best, bestPose);
            }

            var halfStep = model.Options.AngleStepDegrees * Math.PI / 360.0;
            Span<double> angleDeltas = [-halfStep, halfStep];
            foreach (var da in angleDeltas)
            {
                var angle = center.Angle + da;
                if (angle - model.TrainingReferenceAngle < (model.Options.MinimumAngleDegrees * Math.PI / 180) - 1e-12 ||
                    angle - model.TrainingReferenceAngle > (model.Options.MaximumAngleDegrees * Math.PI / 180) + 1e-12)
                {
                    continue;
                }

                foreach (var dy in fullOffsets)
                {
                    foreach (var dx in fullOffsets)
                    {
                        var proposal = center with { X = center.X + dx, Y = center.Y + dy, Angle = angle };
                        var score = ContinuousVerifier.Score(gradient, model, proposal, level, allowElastic: false);
                        if (score.Score > best.Score + ProbeTieEpsilon)
                        {
                            best = score;
                            bestPose = proposal;
                        }
                    }
                }
            }

            return (best, bestPose);
        }

        ReadOnlySpan<double> coarseOffsets = [-1.5, 0.0, 1.5];
        foreach (var dy in coarseOffsets)
        {
            foreach (var dx in coarseOffsets)
            {
                if (dx == 0 && dy == 0)
                {
                    continue;
                }

                var proposal = center with { X = center.X + dx, Y = center.Y + dy };
                var score = ContinuousVerifier.Score(gradient, model, proposal, level, allowElastic: false);
                if (score.Score > best.Score + ProbeTieEpsilon)
                {
                    best = score;
                    bestPose = proposal;
                }
            }
        }

        if (model.FineDetail is not null)
        {
            return (best, bestPose);
        }

        ReadOnlySpan<double> fineOffsets = [-0.5, 0.0, 0.5];
        foreach (var dy in fineOffsets)
        {
            foreach (var dx in fineOffsets)
            {
                if (dx == 0 && dy == 0)
                {
                    continue;
                }

                var proposal = center with { X = center.X + dx, Y = center.Y + dy };
                var score = ContinuousVerifier.Score(gradient, model, proposal, level, allowElastic: false);
                if (score.Score > best.Score + ProbeTieEpsilon)
                {
                    best = score;
                    bestPose = proposal;
                }
            }
        }

        if (model.Templates.Length > 1)
        {
            var halfStep = model.Options.AngleStepDegrees * Math.PI / 360.0;
            var spatialBest = bestPose;
            Span<double> angleDeltas = [-halfStep, halfStep];
            foreach (var da in angleDeltas)
            {
                var angle = center.Angle + da;
                if (angle - model.TrainingReferenceAngle < (model.Options.MinimumAngleDegrees * Math.PI / 180) - 1e-12 ||
                    angle - model.TrainingReferenceAngle > (model.Options.MaximumAngleDegrees * Math.PI / 180) + 1e-12)
                {
                    continue;
                }

                Span<(double dx, double dy)> spatialOffsets =
                [
                    (spatialBest.X - center.X, spatialBest.Y - center.Y),
                    (0.0, 0.0),
                    (-0.5, -0.5),
                    (-0.5, 0.5),
                    (0.5, -0.5),
                    (0.5, 0.5)
                ];
                foreach (var (dx, dy) in spatialOffsets)
                {
                    var proposal = center with { X = center.X + dx, Y = center.Y + dy, Angle = angle };
                    var score = ContinuousVerifier.Score(gradient, model, proposal, level, allowElastic: false);
                    if (score.Score > best.Score + ProbeTieEpsilon)
                    {
                        best = score;
                        bestPose = proposal;
                    }
                }
            }
        }

        return (best, bestPose);
    }

    private static (MatchPose Pose, PoseCandidate Candidate) ReprobeSameBucketBeforeMap(
        GradientImage gradient,
        ShapeModel model,
        PoseCandidate candidate,
        MatchPose pose,
        int factor,
        int level)
    {
        var (currentInitial, bestBucketPose) = ProbeTemplateScore(gradient, model, pose, level, fullSubCell: true);
        pose = bestBucketPose;
        if (currentInitial.Score < PreMapBucketReprobeThreshold)
        {
            var candidateBucket = PoseClusterer.GetAngleBucket(model, candidate.Template);
            var bestBucketScore = currentInitial;
            for (var t = 0; t < model.Templates.Length; t++)
            {
                if (t == candidate.Template || PoseClusterer.GetAngleBucket(model, t) != candidateBucket)
                {
                    continue;
                }

                var tAngle = model.Templates[t].Angle;
                var (tInitial, tBestPose) = ProbeTemplateScore(
                    gradient,
                    model,
                    candidate.Pose with { X = candidate.Pose.X / factor, Y = candidate.Pose.Y / factor, Angle = tAngle },
                    level,
                    fullSubCell: true);
                if (tInitial.Score > bestBucketScore.Score + BucketImprovementMargin)
                {
                    bestBucketScore = tInitial;
                    pose = tBestPose;
                    candidate = candidate with { Template = t };
                }
            }
        }

        return (pose, candidate);
    }

    private static int SearchLocalOrientationWindow(
        OrientationMaps map,
        ShapeModel model,
        ReadOnlySpan<QuantizedFeature> features,
        int cx,
        int cy,
        bool optimizedLocalSearch,
        ShapeSearchOptions options,
        Span<ushort> localScores,
        out ImagePoint plateauCenter,
        out bool hasPlateau)
    {
        var bestSum = -1;
        var left = Math.Max(0, cx - LocalWindowRadius);
        var top = Math.Max(0, cy - LocalWindowRadius);
        var right = Math.Min(map.Width - 1, cx + LocalWindowRadius);
        var bottom = Math.Min(map.Height - 1, cy + LocalWindowRadius);
        var width = right - left + 1;
        if (optimizedLocalSearch)
        {
            map.AccumulateLocal(features, left, top, width, bottom - top + 1, localScores, options.UseSimd);
        }

        var plateauSumX = 0d;
        var plateauSumY = 0d;
        var plateauCount = 0;
        for (var y = top; y <= bottom; y++)
        {
            for (var x = left; x <= right; x++)
            {
                var score = optimizedLocalSearch
                    ? localScores[((y - top) * width) + x - left]
                    : CoarseLevelScanner.ScoreQuantized(map, features, x, y, bestSum, options.Greediness);
                if (score > bestSum)
                {
                    bestSum = score;
                    plateauSumX = x + 0.5;
                    plateauSumY = y + 0.5;
                    plateauCount = 1;
                }
                else if (model.FineDetail is not null && score == bestSum && bestSum > 0)
                {
                    plateauSumX += x + 0.5;
                    plateauSumY += y + 0.5;
                    plateauCount++;
                }
            }
        }

        hasPlateau = plateauCount > 0;
        plateauCenter = hasPlateau ? new ImagePoint(plateauSumX / plateauCount, plateauSumY / plateauCount) : default;
        return bestSum;
    }

    private static MatchPose ResolvePlateauShiftAlongPrincipalAxes(
        GradientImage gradient,
        ShapeModel model,
        MatchPose pose,
        MatchPose mapPose,
        int level,
        ShapeSearchOptions options)
    {
        if (!options.Verify ||
            model.FineDetail is not null ||
            model.FeaturePartitions.IsDefault ||
            !model.Options.UseFineDetailValidation ||
            (((mapPose.X - pose.X) * (mapPose.X - pose.X)) + ((mapPose.Y - pose.Y) * (mapPose.Y - pose.Y)) <= AxisDecompositionMinShiftSquared))
        {
            return mapPose;
        }

        var horizontalMajor = model.BoundsWidth >= model.BoundsHeight;
        var theta = (pose.Angle - model.TrainingReferenceAngle) + (horizontalMajor ? 0.0 : Math.PI / 2);
        var ux = Math.Cos(theta);
        var uy = Math.Sin(theta);
        var vx = -uy;
        var vy = ux;
        var dx = mapPose.X - pose.X;
        var dy = mapPose.Y - pose.Y;
        var projU = (dx * ux) + (dy * uy);
        var projV = (dx * vx) + (dy * vy);
        var minorOnlyPose = pose with { X = pose.X + (projV * vx), Y = pose.Y + (projV * vy) };
        var majorOnlyPose = pose with { X = pose.X + (projU * ux), Y = pose.Y + (projU * uy) };
        var bestPoseCandidate = pose;
        var bestScoreCandidate = ContinuousVerifier.Score(
            gradient,
            model,
            pose,
            level,
            allowElastic: false,
            options.MaxOcclusionRatio,
            options.StrictContext);
        Span<MatchPose> trials = [minorOnlyPose, majorOnlyPose, mapPose];
        foreach (var trialPose in trials)
        {
            var trialScore = ContinuousVerifier.Score(
                gradient,
                model,
                trialPose,
                level,
                allowElastic: false,
                options.MaxOcclusionRatio,
                options.StrictContext);
            var margin = trialPose.Equals(mapPose) ? 0.01 : -0.005;
            if (trialScore.Score >= bestScoreCandidate.Score + margin)
            {
                bestScoreCandidate = trialScore;
                bestPoseCandidate = trialPose;
            }
        }

        return bestPoseCandidate;
    }

    private static (MatchPose Pose, PoseCandidate Candidate) ReprobeSameBucketAfterMap(
        GradientImage gradient,
        ShapeModel model,
        PoseCandidate candidate,
        MatchPose pose,
        int level)
    {
        var (currentInitial, bestBucketPose) = ProbeTemplateScore(gradient, model, pose, level, fullSubCell: true);
        if (currentInitial.Score < PostMapBucketReprobeThreshold)
        {
            var candidateBucket = PoseClusterer.GetAngleBucket(model, candidate.Template);
            var bestBucketScore = currentInitial;
            pose = bestBucketPose;
            for (var t = 0; t < model.Templates.Length; t++)
            {
                if (t == candidate.Template || PoseClusterer.GetAngleBucket(model, t) != candidateBucket)
                {
                    continue;
                }

                var tAngle = model.Templates[t].Angle;
                if (Math.Abs(tAngle - pose.Angle) < ProbeTieEpsilon)
                {
                    continue;
                }

                var (tInitial, tBestPose) = ProbeTemplateScore(
                    gradient,
                    model,
                    pose with { Angle = tAngle },
                    level,
                    fullSubCell: true);
                if (tInitial.Score > bestBucketScore.Score + BucketImprovementMargin)
                {
                    bestBucketScore = tInitial;
                    pose = tBestPose;
                    candidate = candidate with { Template = t };
                }
            }
        }

        return (pose, candidate);
    }

    private static (MatchPose Pose, PoseCandidate Candidate) ReprobeFineDetailTemplates(
        PreparedSearchImage image,
        ShapeModel model,
        PoseCandidate candidate,
        MatchPose pose,
        int cx,
        int cy,
        int level,
        int featureCount)
    {
        var (bestInitial, bestProbePose) = ProbeTemplateScore(image.Gradients[level], model, pose, level);
        pose = bestProbePose;
        if (bestInitial.Score < FineDetailInitialProbeGate ||
            model.Options.MaximumScale > model.Options.MinimumScale ||
            Math.Abs(pose.Angle - model.TrainingReferenceAngle) > 1e-4)
        {
            var coarseThreshold = (int)Math.Max(0, (candidate.Score - FineDetailCoarseDiscount) * 100 * featureCount);
            var centerPose = new MatchPose(cx + 0.5, cy + 0.5, pose.Angle, pose.Scale);
            for (var t = 0; t < model.Templates.Length; t++)
            {
                if (t == candidate.Template)
                {
                    continue;
                }

                var template = model.Templates[t];
                var tFeatures = template.Levels[level].AsSpan();
                if (CoarseLevelScanner.ScoreQuantized(image.Maps[level], tFeatures, cx, cy, coarseThreshold) < coarseThreshold)
                {
                    continue;
                }

                var (tVerified, tBestPose) = ProbeTemplateScore(
                    image.Gradients[level],
                    model,
                    centerPose with { Angle = template.Angle, Scale = template.Scale },
                    level);
                if (tVerified.Score > bestInitial.Score + ProbeTieEpsilon ||
                    (Math.Abs(tVerified.Score - bestInitial.Score) <= ProbeTieEpsilon &&
                     Math.Abs(template.Angle - model.TrainingReferenceAngle) + Math.Abs(template.Scale - 1) <
                     Math.Abs(model.Templates[candidate.Template].Angle - model.TrainingReferenceAngle) +
                     Math.Abs(model.Templates[candidate.Template].Scale - 1)))
                {
                    bestInitial = tVerified;
                    pose = tBestPose;
                    candidate = candidate with { Template = t };
                }
            }
        }

        return (pose, candidate);
    }

    private static (MatchPose Pose, VerifiedScore Verification, double? DetailConsistency) EvaluateLevelZeroContextAndFineDetail(
        PreparedSearchImage image,
        ShapeModel model,
        MatchPose pose,
        VerifiedScore verification,
        int cx,
        int cy,
        ShapeSearchOptions options,
        CancellationToken token)
    {
        double? detailConsistency = null;
        if (options.StrictContext &&
            options.MaxOcclusionRatio <= StrictOcclusionPhaseGate &&
            !model.FeatureAsymmetryFlags.IsDefault &&
            verification.Score < options.MinimumScore &&
            model.Templates.Length > 1)
        {
            var basePose0 = pose with { X = cx + 0.5, Y = cy + 0.5 };
            Span<double> phaseOffsets = [0.0, Math.PI / 2, -Math.PI / 2, Math.PI];
            foreach (var offset in phaseOffsets)
            {
                var altAngle = offset == 0.0
                    ? model.TrainingReferenceAngle
                    : model.TrainingReferenceAngle + Math.IEEERemainder((pose.Angle + offset) - model.TrainingReferenceAngle, 2 * Math.PI);
                var altInitial = pose with { Angle = altAngle };
                if (ContinuousVerifier.Allowed(altInitial, model, options))
                {
                    var altOpt = ContinuousVerifier.Optimize(image.Gradients[0], model, altInitial, options, token, 0);
                    if (altOpt.Score.Score > verification.Score + TieEpsilon)
                    {
                        pose = altOpt.Pose;
                        verification = altOpt.Score;
                    }
                }

                var altBase = basePose0 with { Angle = altAngle };
                if (ContinuousVerifier.Allowed(altBase, model, options))
                {
                    var altBaseOpt = ContinuousVerifier.Optimize(image.Gradients[0], model, altBase, options, token, 0);
                    if (altBaseOpt.Score.Score > verification.Score + TieEpsilon)
                    {
                        pose = altBaseOpt.Pose;
                        verification = altBaseOpt.Score;
                    }
                }
            }
        }

        if (options.ClutterWeight > 0 &&
            model.FineDetail is { } detail &&
            image.Original is { } original &&
            verification.Score >= options.MinimumScore &&
            verification.Support >= options.MinimumSupport)
        {
            var initialDetail = detail.Score(original, pose, model.Options.Polarity);
            var bestCombined = verification.Score * (1 - (options.ClutterWeight * (1 - initialDetail)));
            var bestDetail = initialDetail;
            if (((detail.HasElasticPatches && bestDetail < ElasticPatchDetailReprobeGate) ||
                 (!model.FeatureAsymmetryFlags.IsDefault && bestDetail < AsymmetryDetailReprobeGate)) &&
                model.Templates.Length > 1)
            {
                var angleStep = model.Options.AngleStepDegrees * Math.PI / 180;
                Span<double> targetAngles =
                [
                    pose.Angle - angleStep,
                    pose.Angle + angleStep,
                    model.TrainingReferenceAngle + Math.IEEERemainder((pose.Angle + (Math.PI / 2)) - model.TrainingReferenceAngle, 2 * Math.PI),
                    model.TrainingReferenceAngle + Math.IEEERemainder((pose.Angle - (Math.PI / 2)) - model.TrainingReferenceAngle, 2 * Math.PI),
                    model.TrainingReferenceAngle + Math.IEEERemainder((pose.Angle + Math.PI) - model.TrainingReferenceAngle, 2 * Math.PI)
                ];
                foreach (var targetAngle in targetAngles)
                {
                    var altInitial = pose with { Angle = targetAngle };
                    if (!ContinuousVerifier.Allowed(altInitial, model, options))
                    {
                        continue;
                    }

                    var altOpt = ContinuousVerifier.Optimize(image.Gradients[0], model, altInitial, options, token, 0);
                    if (altOpt.Score.Score < options.MinimumScore || altOpt.Score.Support < options.MinimumSupport)
                    {
                        continue;
                    }

                    var altDetail = detail.Score(original, altOpt.Pose, model.Options.Polarity);
                    var altCombined = altOpt.Score.Score * (1 - (options.ClutterWeight * (1 - altDetail)));
                    if (altCombined > bestCombined + TieEpsilon)
                    {
                        pose = altOpt.Pose;
                        verification = altOpt.Score;
                        bestDetail = altDetail;
                        bestCombined = altCombined;
                    }
                }
            }

            if (options.Refine)
            {
                var detailResult = detail.Optimize(original, model, pose, options, token);
                var directional = ContinuousVerifier.Score(
                    image.Gradients[0],
                    model,
                    detailResult.Pose,
                    0,
                    allowElastic: true,
                    options.MaxOcclusionRatio,
                    options.StrictContext);
                var refinedCombined = directional.Score * (1 - (options.ClutterWeight * (1 - detailResult.Score)));
                if (directional.Score >= options.MinimumScore &&
                    directional.Support >= options.MinimumSupport &&
                    refinedCombined > bestCombined + TieEpsilon &&
                    ((detailResult.Pose.X - pose.X) * (detailResult.Pose.X - pose.X)) +
                    ((detailResult.Pose.Y - pose.Y) * (detailResult.Pose.Y - pose.Y)) <= MaxDetailRefineDriftSquared)
                {
                    pose = detailResult.Pose;
                    verification = directional;
                    bestDetail = detailResult.Score;
                    bestCombined = refinedCombined;
                }
                else if (detailResult.Score > bestDetail)
                {
                    bestDetail = detailResult.Score;
                    bestCombined = verification.Score * (1 - (options.ClutterWeight * (1 - bestDetail)));
                }
            }

            detailConsistency = bestDetail;
            verification = verification with { Score = bestCombined };
        }

        return (pose, verification, detailConsistency);
    }
}
