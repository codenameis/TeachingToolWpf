using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

/// <summary>
/// Evaluates rigid 4-wide <see cref="Vector256{Double}"/> feature blocks with branch-and-bound early exit when elasticity is inactive.
/// </summary>
internal readonly struct RigidSimdAccumulator : IVerificationStage
{
    private const int VectorLaneCount = 4;
    private const double PixelCenterOffset = 0.5;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Evaluate(ref VerificationAccumulators acc, in VerificationThresholds thresholds)
    {
        // [Perf Why]: Transform 4 double-precision feature coordinates and normals per AVX2 iteration before scalar direction lookup.
        if (thresholds.ElasticFeatures || !Vector256.IsHardwareAccelerated || acc.Count < VectorLaneCount)
        {
            return true;
        }

        var vCos = Vector256.Create(acc.Cos);
        var vSin = Vector256.Create(acc.Sin);
        var vScale = Vector256.Create(acc.Pose.Scale);
        var vPoseX = Vector256.Create(acc.Pose.X);
        var vPoseY = Vector256.Create(acc.Pose.Y);
        var vHalf = Vector256.Create(PixelCenterOffset);
        ref var fxRef = ref MemoryMarshal.GetArrayDataReference(acc.LevelData.FeatureX);
        ref var fyRef = ref MemoryMarshal.GetArrayDataReference(acc.LevelData.FeatureY);
        ref var nxRef = ref MemoryMarshal.GetArrayDataReference(acc.LevelData.NormalX);
        ref var nyRef = ref MemoryMarshal.GetArrayDataReference(acc.LevelData.NormalY);
        var qMask = acc.LevelData.QuadrantMask;
        Span<double> sxBuf = stackalloc double[VectorLaneCount];
        Span<double> syBuf = stackalloc double[VectorLaneCount];
        Span<double> snxBuf = stackalloc double[VectorLaneCount];
        Span<double> snyBuf = stackalloc double[VectorLaneCount];

        var effectiveMin = (thresholds.AllowOcclusion &&
                            thresholds.HasPartitions &&
                            !thresholds.HasUniformGrid &&
                            !thresholds.HasElasticPatches &&
                            acc.Model.Options.UseFineDetailValidation)
            ? thresholds.MinimumScore * VerificationThresholds.OcclusionEarlyExitFactor
            : thresholds.MinimumScore;

        var polarity = acc.Model.Options.Polarity;
        for (; acc.Index <= acc.Count - VectorLaneCount; acc.Index += VectorLaneCount)
        {
            var vfx = Vector256.LoadUnsafe(ref fxRef, (nuint)acc.Index);
            var vfy = Vector256.LoadUnsafe(ref fyRef, (nuint)acc.Index);
            var vfnx = Vector256.LoadUnsafe(ref nxRef, (nuint)acc.Index);
            var vfny = Vector256.LoadUnsafe(ref nyRef, (nuint)acc.Index);
            var vx = (vPoseX + (vScale * ((vCos * vfx) - (vSin * vfy)))) - vHalf;
            var vy = (vPoseY + (vScale * ((vSin * vfx) + (vCos * vfy)))) - vHalf;
            var vnx = (vCos * vfnx) - (vSin * vfny);
            var vny = (vSin * vfnx) + (vCos * vfny);
            vx.CopyTo(sxBuf);
            vy.CopyTo(syBuf);
            vnx.CopyTo(snxBuf);
            vny.CopyTo(snyBuf);

            for (var lane = 0; lane < VectorLaneCount; lane++)
            {
                var fi = acc.Index + lane;
                acc.Image.SampleDirection(sxBuf[lane], syBuf[lane], out var gx, out var gy);
                var dot = (snxBuf[lane] * gx) + (snyBuf[lane] * gy);
                var score = polarity == ContrastPolarity.IgnoreLocal ? Math.Abs(dot) : Math.Max(0, dot);
                if (thresholds.HasChains && polarity == ContrastPolarity.Preserve && dot < 0)
                {
                    score = Math.Max(VerificationThresholds.PolarityReverseFloor, VerificationThresholds.PolarityReverseScale * dot);
                }

                acc.RigidSum += score;
                if (score >= VerificationThresholds.FeatureSupportThreshold)
                {
                    var qm = qMask[fi];
                    acc.RigidSupported++;
                    acc.RigidQuadrants |= qm;
                    acc.Supported++;
                    acc.Quadrants |= qm;
                }

                if (thresholds.HasPartitions)
                {
                    var p = acc.Partitions[fi];
                    acc.PartitionRigidSum[p] += score;
                    acc.PartitionElasticSum[p] += score;
                    acc.PartitionCount[p]++;
                    if (score >= VerificationThresholds.PartitionSupportThreshold)
                    {
                        acc.PartitionSupported[p]++;
                    }
                }

                acc.AccumulateChainsAndAsymmetry(thresholds.HasChains, fi, dot, score, score, thresholds.HasAsymFlags);
                if (thresholds.HasAsymFlags && acc.AsymFlags[fi] != 0)
                {
                    var f = acc.Features[fi];
                    var aq = (f.X >= acc.SymCenter.X ? 1 : 0) + (f.Y >= acc.SymCenter.Y ? 2 : 0);
                    var signedScore = polarity == ContrastPolarity.IgnoreLocal ? score : Math.Max(0, dot);
                    acc.AsymQuadSum[aq] += signedScore;
                    acc.AsymQuadCount[aq]++;
                    if ((acc.AsymFlags[fi] & 1) != 0)
                    {
                        acc.AsymQuadXSum[aq] += signedScore;
                        acc.AsymQuadXCount[aq]++;
                    }

                    if ((acc.AsymFlags[fi] & 2) != 0)
                    {
                        acc.AsymQuadYSum[aq] += signedScore;
                        acc.AsymQuadYCount[aq]++;
                    }
                }

                acc.Sum += score;
            }

            if (acc.Index + VectorLaneCount < acc.Count && double.IsFinite(thresholds.MinimumScore))
            {
                var safeUpperMean = (acc.Sum + ((acc.Count - acc.Index - VectorLaneCount) * thresholds.FeatureUpperBound) +
                    (acc.Count * VerificationThresholds.EarlyExitFloatingPointSlack)) / acc.Count;
                if (thresholds.Greediness <= 0)
                {
                    if (safeUpperMean <= effectiveMin)
                    {
                        return false;
                    }
                }
                else
                {
                    var currentMean = acc.Sum / (acc.Index + VectorLaneCount);
                    if (((1.0 - thresholds.Greediness) * safeUpperMean) + (thresholds.Greediness * currentMean) <= effectiveMin)
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }
}
