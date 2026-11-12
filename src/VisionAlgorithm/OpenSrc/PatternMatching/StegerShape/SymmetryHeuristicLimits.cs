namespace VisionAlgorithm.OpenSrc.PatternMatching;

/// <summary>
/// Geometric and statistical thresholds governing self-symmetry core detection, mirror/rotational key classification,
/// and 4-cardinal context sector boundary verification during shape model training.
/// </summary>
public sealed record SymmetryHeuristicLimits
{
    public const int DefaultMinimumCandidateCount = 16;
    public const double DefaultClusterMaxDistanceSquared = 6.5;
    public const double DefaultClusterMinNormalDot = 0.72;
    public const double DefaultClusterTangentGateDistanceSquared = 1.8;
    public const double DefaultClusterMaxNormalCrossOffset = 0.75;

    public const int DefaultClosedComponentMinSize = 12;
    public const int DefaultFullQuadrantMask = 15;
    public const double DefaultClosedCentroidOffsetRatio = 0.18;
    public const double DefaultClosedMaxAspectRatio = 1.45;

    public const double DefaultRadialBandMinRatio = 0.08;
    public const double DefaultRadialBandMaxMinSpanRatio = 0.355;
    public const double DefaultRadialBandMaxTotalSpanRatio = 0.34;
    public const double DefaultRadialNormalMinDot = 0.94;
    public const double DefaultAxisNormalMinComponent = 0.95;
    public const double DefaultCollinearMaxCrossOffset = 0.75;
    public const double DefaultCollinearMinAlongOffset = 2.0;
    public const double DefaultCollinearMaxAlongOffset = 6.0;
    public const int DefaultCollinearMinNeighbors = 3;

    public const int DefaultRadialCoreMinPoints = 20;
    public const int DefaultRadialCoreMinOctants = 5;
    public const double DefaultOppositeOctantBalanceRatio = 0.15;
    public const int DefaultMinBalancedOctantPairs = 2;
    public const double DefaultMaxRadialCenterDriftRatio = 0.03;
    public const double DefaultClosedToRadialMinSpanRatio = 0.85;
    public const double DefaultClosedToRadialMaxSpanRatio = 1.15;
    public const double DefaultClosedToRadialMaxCenterDeltaRatio = 0.01;

    public const double DefaultOuterRingGateSpanScale = 1.24;
    public const double DefaultOuterRingGateTotalRatio = 0.68;
    public const double DefaultOuterRingMinRadiusRatio = 0.53;
    public const double DefaultOuterRingMaxRadiusRatio = 0.66;
    public const double DefaultOuterRingTotalMaxRadiusRatio = 0.31;
    public const double DefaultOuterRingRadialStep = 0.40;
    public const double DefaultOuterRingMinGradientDot = 0.90;
    public const int DefaultOuterRingMinQuadrants = 3;
    public const double DefaultOuterRingPromotionRatio = 1.15;

    public const double DefaultExteriorCoreRadiusRatio = 0.56;
    public const double DefaultNonRadialMaxDot = 0.88;
    public const double DefaultAxisAlignedBorderMinNormal = 0.85;
    public const double DefaultMinCoreToMinBoxRatio = 0.28;
    public const double DefaultMinTotalToCoreSpanRatio = 1.25;
    public const int DefaultMinExteriorLinearEdges = 4;
    public const double DefaultMinNonCoreFraction = 0.18;
    public const int DefaultMinNonRadialExteriorEdges = 8;
    public const double DefaultSquareAspectRatioGate = 1.06;

    public const double DefaultCoreExcludeRadiusRatio = 0.55;
    public const double DefaultMinAxisOffsetRatio = 0.48;
    public const double DefaultMinMirrorToleranceSquared = 4.0;
    public const double DefaultMirrorToleranceCoreRatio = 0.11;
    public const double DefaultMinRotationToleranceSquared = 1.0;
    public const double DefaultRotationToleranceCoreRatio = 0.055;
    public const double DefaultCounterpartMinNormalDot = 0.80;
    public const double DefaultCornerMarginCoreRatio = 0.08;
    public const double DefaultAxisKeyMinNormalComponent = 0.55;
    public const double DefaultRotationKeyMinRadiusRatio = 0.72;
    public const int DefaultMinAxisKeyCount = 1;
    public const int DefaultMinRotationKeyCount = 2;
    public const double DefaultMaxKeySelectionFraction = 0.45;

    public const double DefaultTangentStepCoreRatio = 0.11;
    public const double DefaultRadialStepCoreRatio = 0.075;
    public const double DefaultMinTangentRadiusCoreRatio = 0.57;
    public const double DefaultDefaultSectorRadiusCoreRatio = 0.62;
    public const double DefaultAsymCoordGateScale = 0.82;
    public const double DefaultDenseOrthoMaxCoreRatio = 0.55;
    public const double DefaultLongBorderMinCoreRatio = 0.45;
    public const double DefaultLongBorderDominanceRatio = 1.35;
    public const double DefaultProbeGradientMinDot = 0.80;
    public const double DefaultExtendedScanMinCoreRatio = 0.76;
    public const double DefaultExtendedScanMaxCoreRatio = 0.96;

    public static SymmetryHeuristicLimits Default { get; } = new();

    public int MinimumCandidateCount { get; init; } = DefaultMinimumCandidateCount;
    public double ClusterMaxDistanceSquared { get; init; } = DefaultClusterMaxDistanceSquared;
    public double ClusterMinNormalDot { get; init; } = DefaultClusterMinNormalDot;
    public double ClusterTangentGateDistanceSquared { get; init; } = DefaultClusterTangentGateDistanceSquared;
    public double ClusterMaxNormalCrossOffset { get; init; } = DefaultClusterMaxNormalCrossOffset;
    public int ClosedComponentMinSize { get; init; } = DefaultClosedComponentMinSize;
    public double ClosedCentroidOffsetRatio { get; init; } = DefaultClosedCentroidOffsetRatio;
    public double ClosedMaxAspectRatio { get; init; } = DefaultClosedMaxAspectRatio;
    public double CoreExcludeRadiusRatio { get; init; } = DefaultCoreExcludeRadiusRatio;
    public double MinAxisOffsetRatio { get; init; } = DefaultMinAxisOffsetRatio;
    public double CounterpartMinNormalDot { get; init; } = DefaultCounterpartMinNormalDot;
    public double AxisKeyMinNormalComponent { get; init; } = DefaultAxisKeyMinNormalComponent;
    public double RotationKeyMinRadiusRatio { get; init; } = DefaultRotationKeyMinRadiusRatio;
    public int MinAxisKeyCount { get; init; } = DefaultMinAxisKeyCount;
    public int MinRotationKeyCount { get; init; } = DefaultMinRotationKeyCount;
    public double MaxKeySelectionFraction { get; init; } = DefaultMaxKeySelectionFraction;
}
