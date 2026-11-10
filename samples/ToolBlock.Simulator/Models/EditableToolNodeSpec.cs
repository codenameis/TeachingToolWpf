// File: samples/ToolBlock.Simulator/Models/EditableToolNodeSpec.cs
using System;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.ToolBlock;

namespace ToolBlock.Simulator.Models;

/// <summary>
/// Immutable parameter specification for a node inside the proprietary ImageJ + ToolBlock DAG Simulator.
/// Supports ROI-bound vision algorithms (<c>ShapePatternMatch</c>, <c>DualRakeCornerAlign</c>, <c>RleBlobAnalyzer</c>,
/// <c>BinaryPixelCounter</c>, <c>SubpixelCaliperMetrology</c>, <c>GoldenSurfaceInspector</c>)
/// with per-ROI ImageJ spatial pre-filtering (<see cref="RoiPreFilterKind"/>) and automatic thresholding (<see cref="ImageJAutoThresholdMethod"/>),
/// plus virtual data combiners/judges (<c>GeometryMathCombiner</c>, <c>RelativeOffsetKinematics</c>, <c>ToleranceSpecJudge</c>, <c>FinalVerdictAggregator</c>).
/// </summary>
public sealed record EditableToolNodeSpec(
    int NodeId,
    string Name,
    VisionToolKind Kind,
    Guid? BoundRoiId = null,
    string BoundRoiName = "Virtual (No ROI)",
    RoiPreFilterKind PreFilter = RoiPreFilterKind.None,
    ImageJAutoThresholdMethod AutoThreshold = ImageJAutoThresholdMethod.Manual,
    bool InvertThresholdPolarity = false,
    float PatMaxMinScore = 0.50f,
    float PatMaxAngleRangeDeg = 35.0f,
    float CornerSearchLengthPx = 28.0f,
    int CornerCaliperCount = 12,
    float CornerApexIgnoreMarginPx = 8.0f,
    byte BlobMinThreshold = 150,
    byte BlobMaxThreshold = byte.MaxValue,
    BlobMorphologyOperation BlobMorphology = BlobMorphologyOperation.OpenBox,
    int BlobMinAreaPixels = 40,
    byte BinaryMinThreshold = 140,
    byte BinaryMaxThreshold = byte.MaxValue,
    float BinaryMinPassPercent = 10.0f,
    float BinaryMaxPassPercent = 95.0f,
    CaliperMetroMode CaliperMode = CaliperMetroMode.SpokeCircleFit,
    float CaliperSigma = 1.1f,
    float CaliperMinContrast = 14.0f,
    float CaliperNominalRadiusPx = 16.0f,
    byte VariationAbsThreshold = 18,
    float VariationScale = 3.0f,
    int VariationMinDefectAreaPx = 8,
    GeometryCombinerOperation GeometryOperation = GeometryCombinerOperation.PointToPointDistanceAndAngle,
    double GeometryMmPerPixel = 0.05,
    double OffsetMasterLocalX = 60.0,
    double OffsetMasterLocalY = 0.0,
    double OffsetMasterAngleDeg = 0.0,
    double OffsetStageRadiusMm = 150.0,
    ScalarCombineMode JudgeCombineMode = ScalarCombineMode.SingleValue,
    double JudgeLsl = 2.75,
    double JudgeUsl = 3.25,
    MissingPolicy JudgeMissingPolicy = MissingPolicy.ForceNg,
    double JudgeFallbackValue = 0.0,
    VerdictLogicGate VerdictGate = VerdictLogicGate.AllAnd)
{
    public bool RequiresCanvasRoi => Kind is
        VisionToolKind.ShapePatternMatch or
        VisionToolKind.DualRakeCornerAlign or
        VisionToolKind.RleBlobAnalyzer or
        VisionToolKind.BinaryPixelCounter or
        VisionToolKind.SubpixelCaliperMetrology or
        VisionToolKind.GoldenSurfaceInspector;
}
