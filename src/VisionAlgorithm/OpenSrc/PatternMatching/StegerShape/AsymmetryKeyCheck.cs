using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

/// <summary>
/// Evaluates pre-trimming asymmetry key minimums and post-chain SE(2)-equivariant mirror-intrusion,
/// 90°/270° rotational rejection, and flip-rejection penalties.
/// </summary>
internal readonly struct AsymmetryKeyCheck : IVerificationStage
{
    private const double PixelCenterOffset = 0.5;
    private const double SingleKeyMirrorActivationScore = 0.50;
    private const double MultiKeyMirrorActivationScore = 0.78;
    private const double MirrorScoreHitThreshold = 0.50;
    private const double MirrorAdvantageMargin = 0.05;
    private const double FallbackCoreDiameter = 18.0;
    private const double FallbackSectorRadiusRatio = 0.64;
    private const double TangentStepCoreRatio = 0.11;
    private const double RadialStepCoreRatio = 0.075;
    private const double ContextProbeDirectionThreshold = 0.65;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SummarizeKeyScores(ref VerificationAccumulators acc, in VerificationThresholds thresholds)
    {
        acc.MinAsymKeyScore = 1.0;
        acc.MinAsymQuadScore = 1.0;
        if (!thresholds.HasAsymFlags)
        {
            return;
        }

        if (acc.AsymXCount > 0)
        {
            acc.MinAsymKeyScore = Math.Min(acc.MinAsymKeyScore, acc.AsymXSum / acc.AsymXCount);
        }

        if (acc.AsymYCount > 0)
        {
            acc.MinAsymKeyScore = Math.Min(acc.MinAsymKeyScore, acc.AsymYSum / acc.AsymYCount);
        }

        if (acc.AsymR90Count > 0)
        {
            acc.MinAsymKeyScore = Math.Min(acc.MinAsymKeyScore, acc.AsymR90Sum / acc.AsymR90Count);
        }

        if (acc.AsymR270Count > 0)
        {
            acc.MinAsymKeyScore = Math.Min(acc.MinAsymKeyScore, acc.AsymR270Sum / acc.AsymR270Count);
        }

        for (var aq = 0; aq < VerificationThresholds.SymmetryQuadrantCount; aq++)
        {
            if (acc.AsymQuadCount[aq] >= 2)
            {
                acc.MinAsymQuadScore = Math.Min(acc.MinAsymQuadScore, acc.AsymQuadSum[aq] / acc.AsymQuadCount[aq]);
            }

            if (acc.AsymQuadXCount[aq] >= 2)
            {
                acc.MinAsymQuadScore = Math.Min(acc.MinAsymQuadScore, acc.AsymQuadXSum[aq] / acc.AsymQuadXCount[aq]);
            }

            if (acc.AsymQuadYCount[aq] >= 2)
            {
                acc.MinAsymQuadScore = Math.Min(acc.MinAsymQuadScore, acc.AsymQuadYSum[aq] / acc.AsymQuadYCount[aq]);
            }
        }

        if (thresholds.AllowElastic && acc.Model.OccupiedContextSectorMask != 0)
        {
            acc.MinAsymKeyScore = Math.Min(acc.MinAsymKeyScore, acc.MinAsymQuadScore);
        }

        if (acc.Model.OccupiedContextSectorMask != 0 && acc.MinAsymKeyScore < VerificationThresholds.MinOccupiedAsymKeyThreshold)
        {
            acc.AsymKeyMissing = true;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Evaluate(ref VerificationAccumulators acc, in VerificationThresholds thresholds)
    {
        if (!thresholds.HasAsymFlags || acc.Model.OccupiedContextSectorMask == 0)
        {
            return true;
        }

        var isStrictEvaluation = thresholds.AllowElastic &&
                                 thresholds.StrictContext &&
                                 thresholds.MaxOcclusionRatio <= VerificationThresholds.StrictMaxOcclusionGate;

        if (isStrictEvaluation &&
            HasMirrorCounterpartIntrusion(
                acc.Image,
                acc.Model,
                acc.Pose,
                acc.Cos,
                acc.Sin,
                acc.AsymFlags,
                acc.AsymXSum,
                acc.AsymXCount,
                acc.AsymYSum,
                acc.AsymYCount))
        {
            acc.MeanScore = Math.Min(
                acc.MeanScore * VerificationThresholds.StrictViolationPenaltyScale,
                VerificationThresholds.StrictViolationScoreCap);
        }
        else if (isStrictEvaluation &&
                 ((acc.AsymR90Count > 0 && (acc.AsymR90Sum / acc.AsymR90Count) < VerificationThresholds.StrictRotationalAsymThreshold) ||
                  (acc.AsymR270Count > 0 && (acc.AsymR270Sum / acc.AsymR270Count) < VerificationThresholds.StrictRotationalAsymThreshold)))
        {
            acc.MeanScore = Math.Min(
                acc.MeanScore * VerificationThresholds.StrictViolationPenaltyScale,
                VerificationThresholds.StrictViolationScoreCap);
        }
        else if (isStrictEvaluation &&
                 ((acc.AsymXCount >= 1 && (acc.AsymXSum / acc.AsymXCount) < VerificationThresholds.StrictAxisAsymThreshold) ||
                  (acc.AsymYCount >= 1 && (acc.AsymYSum / acc.AsymYCount) < VerificationThresholds.StrictAxisAsymThreshold) ||
                  acc.MinAsymQuadScore < VerificationThresholds.StrictQuadrantAsymThreshold ||
                  (acc.RigidSum / acc.Count) < VerificationThresholds.StrictRigidMeanThreshold ||
                  acc.UnmatchedAsymCount >= VerificationThresholds.MaxAllowedUnmatchedAsymFeatures ||
                  acc.UnmatchedTotalCount >= VerificationThresholds.MaxAllowedUnmatchedTotalFeatures))
        {
            acc.MeanScore = Math.Min(
                acc.MeanScore * VerificationThresholds.StrictViolationPenaltyScale,
                VerificationThresholds.StrictViolationScoreCap);
        }

        var singleKey = (acc.AsymXCount == 1 && acc.AsymYCount == 0) || (acc.AsymYCount == 1 && acc.AsymXCount == 0);
        var keyThreshold = singleKey
            ? VerificationThresholds.SingleKeyTargetThreshold
            : VerificationThresholds.MultiKeyTargetThreshold;

        if (acc.MeanScore > VerificationThresholds.StrictViolationScoreCap && acc.MinAsymKeyScore < keyThreshold)
        {
            var keyAchievement = Math.Clamp(acc.MinAsymKeyScore / keyThreshold, 0.0, 1.0);
            acc.MeanScore *= keyAchievement * keyAchievement;
            var strictKeyFloor = singleKey
                ? VerificationThresholds.SingleKeyStrictFloor
                : VerificationThresholds.MultiKeyStrictFloor;
            var softKeyFloor = singleKey
                ? VerificationThresholds.SingleKeySoftFloor
                : VerificationThresholds.MultiKeySoftFloor;
            if (isStrictEvaluation && acc.MinAsymKeyScore < strictKeyFloor)
            {
                acc.MeanScore = Math.Min(
                    acc.MeanScore * VerificationThresholds.StrictViolationPenaltyScale,
                    VerificationThresholds.StrictViolationScoreCap);
            }
            else if (acc.MinAsymKeyScore < softKeyFloor)
            {
                acc.MeanScore = Math.Min(acc.MeanScore, VerificationThresholds.SoftFloorScoreCap);
            }
        }

        return true;
    }

    internal static bool HasMirrorCounterpartIntrusion(
        GradientImage image,
        ShapeModel model,
        MatchPose pose,
        double cos,
        double sin,
        ImmutableArray<byte> asymFlags,
        double asymXSum,
        int asymXCount,
        double asymYSum,
        int asymYCount)
    {
        var features = model.Levels[0].Features;
        var symCenter = model.SymmetryCenter;
        if (asymYCount > 0 && (asymYSum / asymYCount) < (asymYCount == 1 ? SingleKeyMirrorActivationScore : MultiKeyMirrorActivationScore))
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
                var sx = pose.X + (pose.Scale * ((cos * mx) - (sin * my))) - PixelCenterOffset;
                var sy = pose.Y + (pose.Scale * ((sin * mx) + (cos * my))) - PixelCenterOffset;
                var snx = (cos * mnx) - (sin * mny);
                var sny = (sin * mnx) + (cos * mny);
                image.SampleDirection(sx, sy, out var gx, out var gy);
                var dot = (snx * gx) + (sny * gy);
                var mirrorScore = Math.Abs(dot);
                mirrorYSum += mirrorScore;
                if (mirrorScore >= MirrorScoreHitThreshold)
                {
                    flipHits++;
                }
            }

            if ((mirrorYSum / asymYCount) > (asymYSum / asymYCount) + MirrorAdvantageMargin ||
                flipHits >= Math.Max(1, (asymYCount + 1) / 2))
            {
                return true;
            }
        }

        if (asymXCount > 0 && (asymXSum / asymXCount) < (asymXCount == 1 ? SingleKeyMirrorActivationScore : MultiKeyMirrorActivationScore))
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
                var sx = pose.X + (pose.Scale * ((cos * mx) - (sin * my))) - PixelCenterOffset;
                var sy = pose.Y + (pose.Scale * ((sin * mx) + (cos * my))) - PixelCenterOffset;
                var snx = (cos * mnx) - (sin * mny);
                var sny = (sin * mnx) + (cos * mny);
                image.SampleDirection(sx, sy, out var gx, out var gy);
                var dot = (snx * gx) + (sny * gy);
                var mirrorScore = Math.Abs(dot);
                mirrorXSum += mirrorScore;
                if (mirrorScore >= MirrorScoreHitThreshold)
                {
                    flipHits++;
                }
            }

            if ((mirrorXSum / asymXCount) > (asymXSum / asymXCount) + MirrorAdvantageMargin ||
                flipHits >= Math.Max(1, (asymXCount + 1) / 2))
            {
                return true;
            }
        }

        var occupiedMask = model.OccupiedContextSectorMask;
        var emptyMask = model.EmptyContextSectorMask;
        if (occupiedMask == 0 && emptyMask == 0)
        {
            return false;
        }

        var coreDiameter = model.SymmetryCoreDiameter > 1.0 ? model.SymmetryCoreDiameter : FallbackCoreDiameter;
        var rxBase = model.ContextSectorRadii.X > 1.0 ? model.ContextSectorRadii.X : coreDiameter * FallbackSectorRadiusRatio;
        var ryBase = model.ContextSectorRadii.Y > 1.0 ? model.ContextSectorRadii.Y : rxBase;
        var minBase = Math.Min(rxBase, ryBase);
        var tangentStep = Math.Max(1.0, coreDiameter * TangentStepCoreRatio);
        var radialStep = Math.Max(0.8, coreDiameter * RadialStepCoreRatio);

        Span<int> hitsAtOwnRadius = stackalloc int[4];
        Span<int> hitsInBand = stackalloc int[4];
        Span<int> hitsInFarBand = stackalloc int[4];
        for (var dir = 0; dir < 4; dir++)
        {
            var isXDir = dir < 2;
            var sign = (dir & 1) == 0 ? -1.0 : 1.0;
            var rOwn = isXDir ? rxBase : ryBase;
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
                    var sx = pose.X + (pose.Scale * ((cos * mx) - (sin * my))) - PixelCenterOffset;
                    var sy = pose.Y + (pose.Scale * ((sin * mx) + (cos * my))) - PixelCenterOffset;
                    image.SampleDirection(sx, sy, out var gx, out var gy);
                    if (Math.Abs((expectedNx * gx) + (expectedNy * gy)) >= ContextProbeDirectionThreshold)
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
                    var sx = pose.X + (pose.Scale * ((cos * mx) - (sin * my))) - PixelCenterOffset;
                    var sy = pose.Y + (pose.Scale * ((sin * mx) + (cos * my))) - PixelCenterOffset;
                    image.SampleDirection(sx, sy, out var gx, out var gy);
                    if (Math.Abs((expectedNx * gx) + (expectedNy * gy)) >= ContextProbeDirectionThreshold)
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

        for (var dir = 0; dir < 4; dir++)
        {
            if ((occupiedMask & (1 << dir)) != 0 && hitsAtOwnRadius[dir] < 3)
            {
                return true;
            }
        }

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
}
