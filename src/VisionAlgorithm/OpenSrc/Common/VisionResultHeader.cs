// File: src/VisionAlgorithm/OpenSrc/Common/VisionResultHeader.cs
using System.Runtime.InteropServices;

namespace VisionAlgorithm.OpenSrc.Common;

/// <summary>
/// Blittable 20-byte common header embedded in every standardized vision algorithm result.
/// Captures tool category, execution status, normalized pass/fail verdict, matched template/model provenance,
/// normalized quality score in <c>[0, 1]</c>, and primary measurement metric.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct VisionResultHeader(
    VisionToolCategory Category,
    VisionExecutionStatus Status,
    VisionVerdict Verdict,
    short MatchedModelIndex,
    int MatchedModelId,
    uint DefectFlags,
    float QualityScore,
    float PrimaryMetric)
{
    public const short UnmatchedModelIndex = -1;

    public bool IsInitialized => Status != VisionExecutionStatus.Uninitialized;
    public bool IsFound => Status == VisionExecutionStatus.Succeeded;
    public bool IsPass => Status == VisionExecutionStatus.Succeeded &&
                          (Verdict == VisionVerdict.Pass || Verdict == VisionVerdict.NotEvaluated);

    public static VisionResultHeader NotFound(
        VisionToolCategory category,
        float qualityScore = 0.0f) =>
        new(
            Category: category,
            Status: VisionExecutionStatus.NotFound,
            Verdict: VisionVerdict.Error,
            MatchedModelIndex: UnmatchedModelIndex,
            MatchedModelId: 0,
            DefectFlags: 0u,
            QualityScore: Math.Clamp(qualityScore, 0.0f, 1.0f),
            PrimaryMetric: 0.0f);

    public static VisionResultHeader Degenerate(
        VisionToolCategory category,
        float qualityScore = 0.0f) =>
        new(
            Category: category,
            Status: VisionExecutionStatus.DegenerateGeometry,
            Verdict: VisionVerdict.Error,
            MatchedModelIndex: UnmatchedModelIndex,
            MatchedModelId: 0,
            DefectFlags: 0u,
            QualityScore: Math.Clamp(qualityScore, 0.0f, 1.0f),
            PrimaryMetric: 0.0f);

    public static VisionResultHeader SucceededMeasurement(
        VisionToolCategory category,
        float qualityScore,
        float primaryMetric,
        short matchedModelIndex = 0,
        int matchedModelId = 0) =>
        new(
            Category: category,
            Status: VisionExecutionStatus.Succeeded,
            Verdict: VisionVerdict.NotEvaluated,
            MatchedModelIndex: matchedModelIndex,
            MatchedModelId: matchedModelId,
            DefectFlags: 0u,
            QualityScore: Math.Clamp(qualityScore, 0.0f, 1.0f),
            PrimaryMetric: primaryMetric);

    public static VisionResultHeader SucceededJudge(
        VisionToolCategory category,
        bool isPass,
        float qualityScore,
        float primaryMetric,
        short matchedModelIndex = 0,
        int matchedModelId = 0,
        uint defectFlags = 0u) =>
        new(
            Category: category,
            Status: VisionExecutionStatus.Succeeded,
            Verdict: isPass ? VisionVerdict.Pass : VisionVerdict.Fail,
            MatchedModelIndex: matchedModelIndex,
            MatchedModelId: matchedModelId,
            DefectFlags: defectFlags,
            QualityScore: Math.Clamp(qualityScore, 0.0f, 1.0f),
            PrimaryMetric: primaryMetric);
}
