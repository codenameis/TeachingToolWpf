// File: src/VisionAlgorithm/OpenSrc/ToolBlock/CornerAlignmentToolNode.cs
using System.Numerics;
using System.Runtime.CompilerServices;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.CornerAlignment.Domain;
using VisionAlgorithm.OpenSrc.CornerAlignment.EdgeRake;
using VisionAlgorithm.OpenSrc.CornerAlignment.LineAndCorner;

namespace VisionAlgorithm.OpenSrc.ToolBlock;

/// <summary>
/// Zero-allocation ROI tool node wrapping <see cref="DualRakeCornerScanner"/>, <see cref="HessianNormalLineFitter"/>,
/// and <see cref="VirtualCornerAligner"/>.
/// Extracts sub-pixel edges along two chamfer-avoiding rakes, fits orthogonal lines via RANSAC + Tukey Biweight IRLS,
/// and publishes the intersected virtual corner pose (<see cref="FixturePose2D"/>), corner point, and both fitted lines.
/// </summary>
public sealed class CornerAlignmentToolNode : IToolBlockNode
{
    public const string PortOutFound = "OutFound";
    public const string PortOutPose = "OutPose";
    public const string PortOutCorner = "OutCorner";
    public const string PortOutLine1 = "OutLine1";
    public const string PortOutLine2 = "OutLine2";
    public const string PortOutIncludedAngleDeg = "OutIncludedAngleDeg";

    private const int MaxStackPoints = 64;
    private const int MinRequiredEdgeHits = 3;

    private readonly CornerAlignmentToolSpec _spec;
    private readonly TerminalHandle<bool> _outFound;
    private readonly TerminalHandle<FixturePose2D> _outPose;
    private readonly TerminalHandle<Point2D> _outCorner;
    private readonly TerminalHandle<Line2D> _outLine1;
    private readonly TerminalHandle<Line2D> _outLine2;
    private readonly TerminalHandle<double> _outIncludedAngleDeg;
    private readonly ToolBlockTerminalDescriptor[] _outputDescriptors;
    private VirtualCornerResult _lastResult = VirtualCornerResult.Invalid;

    public int NodeId { get; }
    public string NodeName { get; }
    public VirtualCornerResult LastResult => _lastResult;

    public TerminalHandle<bool> OutFound => _outFound;
    public TerminalHandle<FixturePose2D> OutPose => _outPose;
    public TerminalHandle<Point2D> OutCorner => _outCorner;
    public TerminalHandle<Line2D> OutLine1 => _outLine1;
    public TerminalHandle<Line2D> OutLine2 => _outLine2;
    public TerminalHandle<double> OutIncludedAngleDeg => _outIncludedAngleDeg;

    public CornerAlignmentToolNode(
        int nodeId,
        string nodeName,
        in CornerAlignmentToolSpec spec,
        TerminalHandle<bool> outFound,
        TerminalHandle<FixturePose2D> outPose,
        TerminalHandle<Point2D> outCorner,
        TerminalHandle<Line2D> outLine1,
        TerminalHandle<Line2D> outLine2,
        TerminalHandle<double> outIncludedAngleDeg)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeName);
        if (!spec.IsInitialized)
        {
            throw new ArgumentException("CornerAlignmentToolSpec must be initialized via CornerAlignmentToolSpec.Create.", nameof(spec));
        }

        NodeId = nodeId;
        NodeName = nodeName;
        _spec = spec;
        _outFound = outFound;
        _outPose = outPose;
        _outCorner = outCorner;
        _outLine1 = outLine1;
        _outLine2 = outLine2;
        _outIncludedAngleDeg = outIncludedAngleDeg;

        _outputDescriptors =
        [
            new ToolBlockTerminalDescriptor(PortOutFound, TerminalDirection.Output, TerminalDataKind.Boolean, outFound.SlotIndex),
            new ToolBlockTerminalDescriptor(PortOutPose, TerminalDirection.Output, TerminalDataKind.Pose, outPose.SlotIndex),
            new ToolBlockTerminalDescriptor(PortOutCorner, TerminalDirection.Output, TerminalDataKind.Point, outCorner.SlotIndex),
            new ToolBlockTerminalDescriptor(PortOutLine1, TerminalDirection.Output, TerminalDataKind.Line, outLine1.SlotIndex),
            new ToolBlockTerminalDescriptor(PortOutLine2, TerminalDirection.Output, TerminalDataKind.Line, outLine2.SlotIndex),
            new ToolBlockTerminalDescriptor(PortOutIncludedAngleDeg, TerminalDirection.Output, TerminalDataKind.Scalar, outIncludedAngleDeg.SlotIndex)
        ];
    }

    public ReadOnlySpan<ToolBlockTerminalDescriptor> GetInputTerminals()
        => [];

    public ReadOnlySpan<ToolBlockTerminalDescriptor> GetOutputTerminals()
        => _outputDescriptors;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Execute(
        ReadOnlyImageView2D<byte> image,
        VisionWorkspace workspace,
        ToolBlockContext context)
    {
        var leg1 = new RakeSearchRegion(
            StartPoint: _spec.ExpectedApexWorld,
            EndPoint: _spec.Leg1EndWorld,
            SearchLength: _spec.SearchLengthPixels,
            ProjectionWidth: _spec.ProjectionWidthPixels,
            CaliperCount: _spec.CaliperCountPerLeg,
            ApexIgnoreMargin: _spec.ApexIgnoreMarginPixels,
            Polarity: _spec.Leg1Polarity);

        var leg2 = new RakeSearchRegion(
            StartPoint: _spec.ExpectedApexWorld,
            EndPoint: _spec.Leg2EndWorld,
            SearchLength: _spec.SearchLengthPixels,
            ProjectionWidth: _spec.ProjectionWidthPixels,
            CaliperCount: _spec.CaliperCountPerLeg,
            ApexIgnoreMargin: _spec.ApexIgnoreMarginPixels,
            Polarity: _spec.Leg2Polarity);

        // [Perf Why]: Stack-allocate edge buffers (<= 64 calipers per leg) to guarantee 0-byte GC allocation on the hot path.
        Span<SubpixelEdgePoint> pts1 = stackalloc SubpixelEdgePoint[MaxStackPoints];
        Span<SubpixelEdgePoint> pts2 = stackalloc SubpixelEdgePoint[MaxStackPoints];

        int count1 = DualRakeCornerScanner.ScanSingleRake(image, leg1, _spec.DetectionOptions, pts1);
        int count2 = DualRakeCornerScanner.ScanSingleRake(image, leg2, _spec.DetectionOptions, pts2);

        if (count1 < MinRequiredEdgeHits || count2 < MinRequiredEdgeHits)
        {
            PublishMissing(context);
            return;
        }

        Vector2 dir1 = leg1.GetUnitLegDirection(out _);
        Vector2 dir2 = leg2.GetUnitLegDirection(out _);

        VisionAlgorithm.OpenSrc.CornerAlignment.Domain.FittedLine2D fitted1 =
            HessianNormalLineFitter.FitLine(pts1.Slice(0, count1), _spec.DetectionOptions, dir1);
        VisionAlgorithm.OpenSrc.CornerAlignment.Domain.FittedLine2D fitted2 =
            HessianNormalLineFitter.FitLine(pts2.Slice(0, count2), _spec.DetectionOptions, dir2);

        int totalCalipers = _spec.CaliperCountPerLeg * 2;
        VirtualCornerResult corner = VirtualCornerAligner.IntersectFittedLines(
            fitted1,
            fitted2,
            totalCalipers,
            _spec.DetectionOptions);

        _lastResult = corner;
        if (!corner.IsValid)
        {
            PublishMissing(context);
            return;
        }

        var cornerPt = new Point2D(corner.CornerX, corner.CornerY);
        var pose = new FixturePose2D(corner.CornerX, corner.CornerY, corner.OrientationRad, 1.0f);

        Vector2 p1End = fitted1.ProjectPoint(_spec.Leg1EndWorld);
        Vector2 p2End = fitted2.ProjectPoint(_spec.Leg2EndWorld);

        Line2D line1Out = Line2D.Create(
            fitted1.Nx,
            fitted1.Ny,
            fitted1.Rho,
            cornerPt,
            new Point2D(p1End.X, p1End.Y));

        Line2D line2Out = Line2D.Create(
            fitted2.Nx,
            fitted2.Ny,
            fitted2.Rho,
            cornerPt,
            new Point2D(p2End.X, p2End.Y));

        context.WriteBool(_outFound, value: true, isValid: true);
        context.WritePose(_outPose, in pose, isValid: true);
        context.WritePoint(_outCorner, cornerPt, isValid: true);
        context.WriteLine(_outLine1, in line1Out, isValid: true);
        context.WriteLine(_outLine2, in line2Out, isValid: true);
        context.WriteScalar(_outIncludedAngleDeg, corner.IncludedAngleDeg, isValid: true);
    }

    private void PublishMissing(ToolBlockContext context)
    {
        context.WriteBool(_outFound, value: false, isValid: false);
        context.InvalidatePose(_outPose);
        context.InvalidatePoint(_outCorner);
        context.InvalidateLine(_outLine1);
        context.InvalidateLine(_outLine2);
        context.InvalidateScalar(_outIncludedAngleDeg);
    }
}
