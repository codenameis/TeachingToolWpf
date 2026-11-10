// File: src/VisionAlgorithm/OpenSrc/PatMax/PatMaxResult.cs
using System.Numerics;
using System.Runtime.InteropServices;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.PatMax;

/// <summary>
/// Sub-pixel geometric pose, similarity score, coverage, clutter breakdown, winning template image provenance,
/// and rich optical/alignment diagnostics from PatMax alignment.
/// Implements <see cref="IVisionToolResult"/> for unified SPC database logging and 100% <c>VisionCanvas</c> replay.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct PatMaxResult(
    float X,
    float Y,
    float AngleRadians,
    float Scale,
    float AspectRatio,
    float Score,
    float Coverage,
    float Clutter,
    int MatchedDipoleCount,
    int IterationCount) : IVisionToolResult
{
    private const float RadiansToDegrees = 180.0f / MathF.PI;
    private const float DefaultBoxDimension = 64.0f;

    /// <summary>0-based index of the template image that achieved the highest match score (<c>-1</c> if not found).</summary>
    public short MatchedTemplateIndex { get; init; } = Score > 0f && Scale > 0f && MatchedDipoleCount > 0 ? (short)0 : VisionResultHeader.UnmatchedModelIndex;

    /// <summary>Persistent database/recipe identifier of the winning template image.</summary>
    public int MatchedTemplateId { get; init; } = Score > 0f && Scale > 0f && MatchedDipoleCount > 0 ? 1 : 0;

    /// <summary>Total number of registered template images evaluated during this search.</summary>
    public short TotalTemplateCount { get; init; } = 1;

    /// <summary>0-based index of the 2nd-best (runner-up) template image (<c>-1</c> when only 1 template was evaluated).</summary>
    public short RunnerUpTemplateIndex { get; init; } = VisionResultHeader.UnmatchedModelIndex;

    /// <summary>Match score of the 2nd-best (runner-up) template image.</summary>
    public float RunnerUpScore { get; init; }

    /// <summary>Score separation margin between the winning template image and the runner-up template image.</summary>
    public float TemplateSeparationMargin => RunnerUpTemplateIndex >= 0 ? MathF.Max(0.0f, Score - RunnerUpScore) : Score;

    /// <summary>Horizontal shift $\Delta X$ from the taught master reference origin (pixels).</summary>
    public float DeltaX { get; init; }

    /// <summary>Vertical shift $\Delta Y$ from the taught master reference origin (pixels).</summary>
    public float DeltaY { get; init; }

    /// <summary>Rotational shift $\Delta\theta$ from the taught master reference angle (radians).</summary>
    public float DeltaAngleRadians { get; init; }

    /// <summary>Root-mean-square sub-pixel normal distance residual across matched boundary dipoles (pixels).</summary>
    public float RmsFitErrorPixels { get; init; }

    /// <summary>Mean edge gradient magnitude (contrast) of matched runtime boundary dipoles.</summary>
    public float MeanEdgeContrast { get; init; }

    /// <summary>Normalized intensity/appearance similarity score in <c>[0, 1]</c> (defaults to <see cref="Coverage"/> when pure geometric).</summary>
    public float AppearanceScore { get; init; } = Coverage;

    /// <summary>Total number of fine-level dipoles in the winning template model.</summary>
    public int ModelDipoleCount { get; init; } = MatchedDipoleCount;

    /// <summary>Number of coarse pose candidates evaluated during the search.</summary>
    public int EvaluatedCandidateCount { get; init; } = 1;

    /// <summary>Width of the winning template pattern in pixels.</summary>
    public float PatternWidth { get; init; } = DefaultBoxDimension;

    /// <summary>Height of the winning template pattern in pixels.</summary>
    public float PatternHeight { get; init; } = DefaultBoxDimension;

    public bool IsValid => Score > 0f && Scale > 0f && MatchedDipoleCount > 0;

    public VisionResultHeader Header => IsValid
        ? VisionResultHeader.SucceededMeasurement(
            category: VisionToolCategory.PatternMatching,
            qualityScore: Score,
            primaryMetric: Score,
            matchedModelIndex: MatchedTemplateIndex,
            matchedModelId: MatchedTemplateId)
        : VisionResultHeader.NotFound(VisionToolCategory.PatternMatching, Score);

    public FixturePose2D WorldPose => IsValid
        ? new FixturePose2D(X, Y, AngleRadians, Scale, Scale * MathF.Max(0.25f, AspectRatio))
        : FixturePose2D.Identity;

    public Vector2 ReferencePointWorld => new(X, Y);

    public static PatMaxResult Empty => default;

    public PatMaxResult WithTemplateProvenance(
        short matchedTemplateIndex,
        int matchedTemplateId,
        short totalTemplateCount,
        short runnerUpTemplateIndex = VisionResultHeader.UnmatchedModelIndex,
        float runnerUpScore = 0.0f) =>
        this with
        {
            MatchedTemplateIndex = matchedTemplateIndex,
            MatchedTemplateId = matchedTemplateId,
            TotalTemplateCount = totalTemplateCount,
            RunnerUpTemplateIndex = runnerUpTemplateIndex,
            RunnerUpScore = runnerUpScore
        };

    public VisionSpcRecord ToSpcRecord(
        int nodeId = 0,
        Guid roiId = default,
        double specLsl = double.NaN,
        double specUsl = double.NaN)
    {
        float marginRatio = VisionSpcRecord.ComputeSpecMarginRatio(Score, specLsl, specUsl);
        return new VisionSpcRecord(
            NodeId: nodeId,
            RoiId: roiId,
            Header: Header,
            TotalModelCount: TotalTemplateCount,
            RunnerUpModelIndex: RunnerUpTemplateIndex,
            RunnerUpScore: RunnerUpScore,
            WorldX: X,
            WorldY: Y,
            WorldAngleDeg: AngleRadians * RadiansToDegrees,
            Scale: Scale,
            DeltaX: DeltaX,
            DeltaY: DeltaY,
            DeltaAngleDeg: DeltaAngleRadians * RadiansToDegrees,
            PrimaryMetric: Score,
            SpecLsl: specLsl,
            SpecUsl: specUsl,
            SpecMarginRatio: marginRatio,
            MeanGrayValue: 0.0f,
            ContrastOrStdDev: MeanEdgeContrast,
            RmsFitErrorPixels: RmsFitErrorPixels,
            AuxMetric1: Coverage,
            AuxMetric2: Clutter,
            AuxMetric3: AppearanceScore,
            HitCount: MatchedDipoleCount,
            TotalSampleCount: Math.Max(MatchedDipoleCount, ModelDipoleCount),
            DecodedText: string.Empty);
    }

    public VisionReplayPayload ToReplayPayload()
    {
        if (!IsValid)
        {
            return VisionReplayPayload.Empty;
        }

        string runnerUpTag = RunnerUpTemplateIndex >= 0
            ? $" (vs #{RunnerUpTemplateIndex}:{RunnerUpScore:F2})"
            : string.Empty;

        string label = $"PatMax Tpl#{MatchedTemplateIndex}[ID:{MatchedTemplateId}]{runnerUpTag}: Score={Score:F3} Cov={Coverage:F2} Clut={Clutter:F2} Err={RmsFitErrorPixels:F2}px";

        return new VisionReplayPayload(
            ShapeKind: ReplayShapeKind.OrientedBox,
            WorldPose: WorldPose,
            WidthOrRadius: PatternWidth > 0f ? PatternWidth : DefaultBoxDimension,
            HeightOrSpan: PatternHeight > 0f ? PatternHeight : DefaultBoxDimension,
            SegmentAStart: new Vector2(X, Y),
            SegmentAEnd: new Vector2(X - DeltaX, Y - DeltaY),
            SegmentBStart: Vector2.Zero,
            SegmentBEnd: Vector2.Zero,
            SubRegions: ReadOnlyMemory<ReplaySubRegion>.Empty,
            InlierPointsWorld: ReadOnlyMemory<Vector2>.Empty,
            OutlierPointsWorld: ReadOnlyMemory<Vector2>.Empty,
            SummaryLabel: label);
    }
}
