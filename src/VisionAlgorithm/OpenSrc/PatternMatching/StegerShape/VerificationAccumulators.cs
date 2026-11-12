using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

/// <summary>
/// Stack-allocated mutable state shared across <see cref="IVerificationStage"/> pipeline stages during single-pose verification.
/// </summary>
internal ref struct VerificationAccumulators
{
    public GradientImage Image;
    public ShapeModel Model;
    public MatchPose Pose;
    public ModelLevel LevelData;
    public ImmutableArray<ModelFeature> Features;
    public int Count;
    public double Cos;
    public double Sin;

    public ImmutableArray<byte> Chains;
    public ImmutableArray<byte> AsymFlags;
    public ImmutableArray<byte> Partitions;
    public ImagePoint SymCenter;

    public Span<double> FeatureScores;
    public Span<double> AsymQuadSum;
    public Span<int> AsymQuadCount;
    public Span<double> AsymQuadXSum;
    public Span<int> AsymQuadXCount;
    public Span<double> AsymQuadYSum;
    public Span<int> AsymQuadYCount;
    public Span<double> PartitionRigidSum;
    public Span<double> PartitionElasticSum;
    public Span<int> PartitionCount;
    public Span<int> PartitionSupported;
    public Span<double> PartitionNormalShiftSum;
    public Span<double> PartitionNormalShiftSq;
    public Span<int> PartitionNormalShifted;

    public int Index;
    public double Sum;
    public int Supported;
    public int Quadrants;
    public double RigidSum;
    public int RigidSupported;
    public int RigidQuadrants;

    public double OuterSum;
    public double InnerSum;
    public int OuterCount;
    public int InnerCount;
    public double OuterSigned;
    public double InnerSigned;

    public double AsymXSum;
    public double AsymYSum;
    public double AsymR90Sum;
    public double AsymR270Sum;
    public int AsymXCount;
    public int AsymYCount;
    public int AsymR90Count;
    public int AsymR270Count;
    public int UnmatchedAsymCount;
    public int UnmatchedTotalCount;

    public double MeanScore;
    public bool AsymKeyMissing;
    public double MinAsymKeyScore;
    public double MinAsymQuadScore;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AccumulateChainsAndAsymmetry(
        bool hasChains,
        int featureIndex,
        double dot,
        double score,
        double rigidScore,
        bool hasAsymFlags)
    {
        if (hasChains)
        {
            if (Chains[featureIndex] == 0)
            {
                OuterSigned += dot;
                OuterSum += score;
                OuterCount++;
            }
            else
            {
                InnerSigned += dot;
                InnerSum += score;
                InnerCount++;
            }
        }

        if (hasAsymFlags)
        {
            // [Perf Why]: Evaluate asymmetry keys using pure rigid alignment without elastic shift or negative chain penalty distortion.
            var signedRigid = dot < 0 ? 0.0 : rigidScore;
            var flag = AsymFlags[featureIndex];
            if ((flag & 1) != 0)
            {
                AsymXSum += signedRigid;
                AsymXCount++;
            }

            if ((flag & 2) != 0)
            {
                AsymYSum += signedRigid;
                AsymYCount++;
            }

            if ((flag & 4) != 0)
            {
                AsymR90Sum += signedRigid;
                AsymR90Count++;
            }

            if ((flag & 8) != 0)
            {
                AsymR270Sum += signedRigid;
                AsymR270Count++;
            }
        }
    }
}
