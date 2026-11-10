// File: src/VisionAlgorithm/OpenSrc/ToolBlock/CaliperLineCircleToolNode.cs
using System.Numerics;
using System.Runtime.CompilerServices;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.Metrology;

namespace VisionAlgorithm.OpenSrc.ToolBlock;

/// <summary>
/// Zero-allocation ROI tool node wrapping <see cref="SubpixelCaliper1D"/>, <see cref="RansacIrlsLineFitter"/>,
/// and <see cref="TaubinCircleFitter"/> with fixture-pose compensation.
/// </summary>
public sealed class CaliperLineCircleToolNode : IToolBlockNode
{
    public const string PortInFixture = "InFixture";
    public const string PortOutFound = "OutFound";
    public const string PortOutEdgeOrCenter = "OutEdgeOrCenter";
    public const string PortOutFittedLine = "OutFittedLine";
    public const string PortOutFittedCircle = "OutFittedCircle";
    public const string PortOutMeasuredWidthOrRadius = "OutMeasuredWidthOrRadius";

    // [Perf Why]: Rake caliper counts up to 128 fit on the stack with zero GC allocation.
    private const int MaxStackRakeSamples = 128;

    // [Math Why]: At least 3 non-collinear or collinear edge points are required for line/circle fitting.
    private const int MinimumFitSamples = 3;

    private readonly CaliperLineCircleToolSpec _spec;
    private readonly TerminalHandle<FixturePose2D> _inFixture;
    private readonly TerminalHandle<bool> _outFound;
    private readonly TerminalHandle<Point2D> _outEdgeOrCenter;
    private readonly TerminalHandle<Line2D> _outFittedLine;
    private readonly TerminalHandle<Circle2D> _outFittedCircle;
    private readonly TerminalHandle<double> _outMeasuredWidthOrRadius;

    private readonly ToolBlockTerminalDescriptor[] _inputDescriptors;
    private readonly ToolBlockTerminalDescriptor[] _outputDescriptors;
    private CaliperPairResult _lastPairResult;
    private FittedLine2D _lastLineResult;
    private FittedCircle2D _lastCircleResult;

    public int NodeId { get; }
    public string NodeName { get; }
    public CaliperLineCircleToolSpec Spec => _spec;
    public CaliperPairResult LastPairResult => _lastPairResult;
    public FittedLine2D LastLineResult => _lastLineResult;
    public FittedCircle2D LastCircleResult => _lastCircleResult;

    public TerminalHandle<FixturePose2D> InFixture => _inFixture;
    public TerminalHandle<bool> OutFound => _outFound;
    public TerminalHandle<Point2D> OutEdgeOrCenter => _outEdgeOrCenter;
    public TerminalHandle<Line2D> OutFittedLine => _outFittedLine;
    public TerminalHandle<Circle2D> OutFittedCircle => _outFittedCircle;
    public TerminalHandle<double> OutMeasuredWidthOrRadius => _outMeasuredWidthOrRadius;

    public CaliperLineCircleToolNode(
        int nodeId,
        string nodeName,
        in CaliperLineCircleToolSpec spec,
        TerminalHandle<FixturePose2D> inFixture,
        TerminalHandle<bool> outFound,
        TerminalHandle<Point2D> outEdgeOrCenter,
        TerminalHandle<Line2D> outFittedLine,
        TerminalHandle<Circle2D> outFittedCircle,
        TerminalHandle<double> outMeasuredWidthOrRadius)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeName);
        spec.EnsureValid();

        NodeId = nodeId;
        NodeName = nodeName;
        _spec = spec;
        _inFixture = inFixture;
        _outFound = outFound;
        _outEdgeOrCenter = outEdgeOrCenter;
        _outFittedLine = outFittedLine;
        _outFittedCircle = outFittedCircle;
        _outMeasuredWidthOrRadius = outMeasuredWidthOrRadius;

        _inputDescriptors = inFixture.IsBound
            ? [new ToolBlockTerminalDescriptor(PortInFixture, TerminalDirection.Input, TerminalDataKind.Pose, inFixture.SlotIndex)]
            : [];

        _outputDescriptors =
        [
            new ToolBlockTerminalDescriptor(PortOutFound, TerminalDirection.Output, TerminalDataKind.Boolean, outFound.SlotIndex),
            new ToolBlockTerminalDescriptor(PortOutEdgeOrCenter, TerminalDirection.Output, TerminalDataKind.Point, outEdgeOrCenter.SlotIndex),
            new ToolBlockTerminalDescriptor(PortOutFittedLine, TerminalDirection.Output, TerminalDataKind.Line, outFittedLine.SlotIndex),
            new ToolBlockTerminalDescriptor(PortOutFittedCircle, TerminalDirection.Output, TerminalDataKind.Circle, outFittedCircle.SlotIndex),
            new ToolBlockTerminalDescriptor(PortOutMeasuredWidthOrRadius, TerminalDirection.Output, TerminalDataKind.Scalar, outMeasuredWidthOrRadius.SlotIndex)
        ];
    }

    public ReadOnlySpan<ToolBlockTerminalDescriptor> GetInputTerminals()
        => _inputDescriptors;

    public ReadOnlySpan<ToolBlockTerminalDescriptor> GetOutputTerminals()
        => _outputDescriptors;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Execute(
        ReadOnlyImageView2D<byte> image,
        VisionWorkspace workspace,
        ToolBlockContext context)
    {
        if (!context.TryResolveInputFixture(_inFixture, out FixturePose2D baseFixture))
        {
            WriteFailureOutputs(context);
            return;
        }

        FixturePose2D worldRoiPose = baseFixture.Compose(_spec.LocalRoiOffset);

        switch (_spec.Mode)
        {
            case CaliperMetroMode.SingleEdge:
                ExecuteSingleEdge(image, in worldRoiPose, workspace, context);
                break;

            case CaliperMetroMode.EdgePairWidth:
                ExecuteEdgePair(image, in worldRoiPose, workspace, context);
                break;

            case CaliperMetroMode.RakeLineFit:
                ExecuteRakeLine(image, in worldRoiPose, workspace, context);
                break;

            case CaliperMetroMode.SpokeCircleFit:
                ExecuteSpokeCircle(image, in worldRoiPose, workspace, context);
                break;

            default:
                WriteFailureOutputs(context);
                break;
        }
    }

    private void ExecuteSingleEdge(
        ReadOnlyImageView2D<byte> image,
        in FixturePose2D worldRoiPose,
        VisionWorkspace workspace,
        ToolBlockContext context)
    {
        if (!SubpixelCaliper1D.TryMeasureSingleEdge(
            image,
            worldRoiPose,
            _spec.ScanLengthPixels,
            _spec.ProjectionWidthPixels,
            _spec.FirstPolarity,
            _spec.CaliperSpec,
            workspace,
            out CaliperEdgeHit edgeHit))
        {
            WriteFailureOutputs(context);
            return;
        }

        Point2D edgePoint = Point2D.FromVector2(edgeHit.WorldPoint);
        context.WriteBool(_outFound, true, isValid: true);
        context.WritePoint(_outEdgeOrCenter, edgePoint, isValid: true);
        context.InvalidateLine(_outFittedLine);
        context.InvalidateCircle(_outFittedCircle);
        context.WriteScalar(_outMeasuredWidthOrRadius, edgeHit.SubpixelPosition, isValid: true);
    }

    private void ExecuteEdgePair(
        ReadOnlyImageView2D<byte> image,
        in FixturePose2D worldRoiPose,
        VisionWorkspace workspace,
        ToolBlockContext context)
    {
        CaliperPairResult pair = SubpixelCaliper1D.MeasureEdgePairWidth(
            image,
            worldRoiPose,
            _spec.ScanLengthPixels,
            _spec.ProjectionWidthPixels,
            _spec.FirstPolarity,
            _spec.SecondPolarity,
            _spec.CaliperSpec,
            workspace);
        _lastPairResult = pair;

        if (!pair.IsFound)
        {
            WriteFailureOutputs(context);
            return;
        }

        Point2D midpoint = Point2D.FromVector2(pair.MidpointWorld);
        Point2D p1 = Point2D.FromVector2(pair.FirstEdge.WorldPoint);
        Point2D p2 = Point2D.FromVector2(pair.SecondEdge.WorldPoint);

        context.WriteBool(_outFound, true, isValid: true);
        context.WritePoint(_outEdgeOrCenter, midpoint, isValid: true);
        if (Line2D.TryCreateFromEndpoints(p1, p2, out Line2D connectingLine))
        {
            context.WriteLine(_outFittedLine, in connectingLine, isValid: true);
        }
        else
        {
            context.InvalidateLine(_outFittedLine);
        }

        context.InvalidateCircle(_outFittedCircle);
        context.WriteScalar(_outMeasuredWidthOrRadius, pair.SubpixelWidth, isValid: true);
    }

    private void ExecuteRakeLine(
        ReadOnlyImageView2D<byte> image,
        in FixturePose2D worldRoiPose,
        VisionWorkspace workspace,
        ToolBlockContext context)
    {
        int rakeCount = Math.Clamp(_spec.SampleCount, MinimumFitSamples, MaxStackRakeSamples);
        Span<Vector2> edgePoints = stackalloc Vector2[rakeCount];
        int validCount = 0;

        float halfSpan = _spec.NominalRadiusOrSpanPixels * 0.5f;
        float step = rakeCount > 1 ? _spec.NominalRadiusOrSpanPixels / (rakeCount - 1) : 0f;

        for (int i = 0; i < rakeCount; i++)
        {
            float localY = -halfSpan + (i * step);
            Vector2 subCenter = worldRoiPose.LocalToWorld(0f, localY);
            FixturePose2D subCaliperPose = new(subCenter.X, subCenter.Y, worldRoiPose.AngleRad, worldRoiPose.ScaleX, worldRoiPose.ScaleY);

            if (SubpixelCaliper1D.TryMeasureSingleEdge(
                image,
                subCaliperPose,
                _spec.ScanLengthPixels,
                _spec.ProjectionWidthPixels,
                _spec.FirstPolarity,
                _spec.CaliperSpec,
                workspace,
                out CaliperEdgeHit hit))
            {
                edgePoints[validCount++] = hit.WorldPoint;
            }
        }

        FittedLine2D fitted = RansacIrlsLineFitter.FitLine(
            edgePoints.Slice(0, validCount),
            _spec.RansacInlierThresholdPixels,
            workspace);
        _lastLineResult = fitted;

        if (!fitted.IsValid)
        {
            WriteFailureOutputs(context);
            return;
        }

        Point2D centroid = Point2D.FromVector2(fitted.Centroid);
        Point2D p1 = new(
            fitted.Centroid.X - (fitted.Direction.X * halfSpan),
            fitted.Centroid.Y - (fitted.Direction.Y * halfSpan));
        Point2D p2 = new(
            fitted.Centroid.X + (fitted.Direction.X * halfSpan),
            fitted.Centroid.Y + (fitted.Direction.Y * halfSpan));

        // FittedLine2D has NormalX * x + NormalY * y + DistanceFromOrigin = 0 -> Rho = -DistanceFromOrigin
        Line2D line = Line2D.Create(
            cosTheta: fitted.NormalX,
            sinTheta: fitted.NormalY,
            rho: -fitted.DistanceFromOrigin,
            p1: p1,
            p2: p2);

        context.WriteBool(_outFound, true, isValid: true);
        context.WritePoint(_outEdgeOrCenter, centroid, isValid: true);
        context.WriteLine(_outFittedLine, in line, isValid: true);
        context.InvalidateCircle(_outFittedCircle);
        context.WriteScalar(_outMeasuredWidthOrRadius, fitted.RmsResidualPixels, isValid: true);
    }

    private void ExecuteSpokeCircle(
        ReadOnlyImageView2D<byte> image,
        in FixturePose2D worldRoiPose,
        VisionWorkspace workspace,
        ToolBlockContext context)
    {
        Vector2 nominalCenter = new(worldRoiPose.CenterX, worldRoiPose.CenterY);
        float scaledNominalRadius = _spec.NominalRadiusOrSpanPixels * worldRoiPose.Scale;

        FittedCircle2D fitted = TaubinCircleFitter.MeasureSpokeCircle(
            image,
            nominalCenter,
            scaledNominalRadius,
            _spec.ScanLengthPixels,
            _spec.SampleCount,
            _spec.FirstPolarity,
            _spec.CaliperSpec,
            workspace);
        _lastCircleResult = fitted;

        if (!fitted.IsValid || fitted.Radius <= 0f)
        {
            WriteFailureOutputs(context);
            return;
        }

        Point2D center = Point2D.FromVector2(fitted.Center);
        double circularity = Math.Clamp(1.0 - (fitted.RmsRadialErrorPixels / Math.Max(1.0, fitted.Radius)), 0.0, 1.0);
        Circle2D circle = Circle2D.Create(center, fitted.Radius, circularity);

        context.WriteBool(_outFound, true, isValid: true);
        context.WritePoint(_outEdgeOrCenter, center, isValid: true);
        context.InvalidateLine(_outFittedLine);
        context.WriteCircle(_outFittedCircle, in circle, isValid: true);
        context.WriteScalar(_outMeasuredWidthOrRadius, fitted.Radius, isValid: true);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteFailureOutputs(ToolBlockContext context)
    {
        context.WriteBool(_outFound, false, isValid: true);
        context.InvalidatePoint(_outEdgeOrCenter);
        context.InvalidateLine(_outFittedLine);
        context.InvalidateCircle(_outFittedCircle);
        context.InvalidateScalar(_outMeasuredWidthOrRadius);
    }
}
