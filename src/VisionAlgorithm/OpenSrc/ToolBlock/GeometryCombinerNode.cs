// File: src/VisionAlgorithm/OpenSrc/ToolBlock/GeometryCombinerNode.cs
using System.Numerics;
using System.Runtime.CompilerServices;
using VisionAlgorithm.OpenSrc.Calibration;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.ToolBlock;

/// <summary>
/// Virtual data-linking combiner node that takes upstream <see cref="Point2D"/> or <see cref="Line2D"/> terminal handles
/// and computes calibrated Euclidean distances, orthogonal point-to-line projections, line-to-line intersections, or midpoints.
/// </summary>
public sealed class GeometryCombinerNode : IToolBlockNode
{
    public const string PortInPointA = "InPointA";
    public const string PortInPointB = "InPointB";
    public const string PortInLineA = "InLineA";
    public const string PortInLineB = "InLineB";

    public const string PortOutValid = "OutValid";
    public const string PortOutDistance = "OutDistance";
    public const string PortOutDeltaX = "OutDeltaX";
    public const string PortOutDeltaY = "OutDeltaY";
    public const string PortOutAngleDeg = "OutAngleDeg";
    public const string PortOutPoint = "OutPoint";

    // [Math Why]: 180.0 / pi converts radians to degrees.
    private const double RadiansToDegrees = 180.0 / Math.PI;

    // [Math Why]: Determinant floor 1e-12 detects parallel or near-parallel lines before intersection division.
    private const double ParallelDeterminantFloor = 1e-12;

    private readonly GeometryCombinerOperation _operation;
    private readonly double _mmPerPixel;
    private readonly PlanarHomography2D? _homography;

    private readonly TerminalHandle<Point2D> _inPointA;
    private readonly TerminalHandle<Point2D> _inPointB;
    private readonly TerminalHandle<Line2D> _inLineA;
    private readonly TerminalHandle<Line2D> _inLineB;

    private readonly TerminalHandle<bool> _outValid;
    private readonly TerminalHandle<double> _outDistance;
    private readonly TerminalHandle<double> _outDeltaX;
    private readonly TerminalHandle<double> _outDeltaY;
    private readonly TerminalHandle<double> _outAngleDeg;
    private readonly TerminalHandle<Point2D> _outPoint;

    private readonly ToolBlockTerminalDescriptor[] _inputDescriptors;
    private readonly ToolBlockTerminalDescriptor[] _outputDescriptors;

    public int NodeId { get; }
    public string NodeName { get; }
    public GeometryCombinerOperation Operation => _operation;
    public double MmPerPixel => _mmPerPixel;

    public GeometryCombinerNode(
        int nodeId,
        string nodeName,
        GeometryCombinerOperation operation,
        double mmPerPixel,
        PlanarHomography2D? homography,
        TerminalHandle<Point2D> inPointA,
        TerminalHandle<Point2D> inPointB,
        TerminalHandle<Line2D> inLineA,
        TerminalHandle<Line2D> inLineB,
        TerminalHandle<bool> outValid,
        TerminalHandle<double> outDistance,
        TerminalHandle<double> outDeltaX,
        TerminalHandle<double> outDeltaY,
        TerminalHandle<double> outAngleDeg,
        TerminalHandle<Point2D> outPoint)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeName);
        if (!double.IsFinite(mmPerPixel) || mmPerPixel <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(mmPerPixel), "mmPerPixel scale factor must be finite and positive.");
        }

        NodeId = nodeId;
        NodeName = nodeName;
        _operation = operation;
        _mmPerPixel = mmPerPixel;
        _homography = homography;

        _inPointA = inPointA;
        _inPointB = inPointB;
        _inLineA = inLineA;
        _inLineB = inLineB;

        _outValid = outValid;
        _outDistance = outDistance;
        _outDeltaX = outDeltaX;
        _outDeltaY = outDeltaY;
        _outAngleDeg = outAngleDeg;
        _outPoint = outPoint;

        var inputs = new List<ToolBlockTerminalDescriptor>();
        if (inPointA.IsBound) inputs.Add(new ToolBlockTerminalDescriptor(PortInPointA, TerminalDirection.Input, TerminalDataKind.Point, inPointA.SlotIndex));
        if (inPointB.IsBound) inputs.Add(new ToolBlockTerminalDescriptor(PortInPointB, TerminalDirection.Input, TerminalDataKind.Point, inPointB.SlotIndex));
        if (inLineA.IsBound) inputs.Add(new ToolBlockTerminalDescriptor(PortInLineA, TerminalDirection.Input, TerminalDataKind.Line, inLineA.SlotIndex));
        if (inLineB.IsBound) inputs.Add(new ToolBlockTerminalDescriptor(PortInLineB, TerminalDirection.Input, TerminalDataKind.Line, inLineB.SlotIndex));
        _inputDescriptors = inputs.ToArray();

        _outputDescriptors =
        [
            new ToolBlockTerminalDescriptor(PortOutValid, TerminalDirection.Output, TerminalDataKind.Boolean, outValid.SlotIndex),
            new ToolBlockTerminalDescriptor(PortOutDistance, TerminalDirection.Output, TerminalDataKind.Scalar, outDistance.SlotIndex),
            new ToolBlockTerminalDescriptor(PortOutDeltaX, TerminalDirection.Output, TerminalDataKind.Scalar, outDeltaX.SlotIndex),
            new ToolBlockTerminalDescriptor(PortOutDeltaY, TerminalDirection.Output, TerminalDataKind.Scalar, outDeltaY.SlotIndex),
            new ToolBlockTerminalDescriptor(PortOutAngleDeg, TerminalDirection.Output, TerminalDataKind.Scalar, outAngleDeg.SlotIndex),
            new ToolBlockTerminalDescriptor(PortOutPoint, TerminalDirection.Output, TerminalDataKind.Point, outPoint.SlotIndex)
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
        switch (_operation)
        {
            case GeometryCombinerOperation.PointToPointDistanceAndAngle:
                ExecutePointToPoint(context);
                break;

            case GeometryCombinerOperation.PointToLineOrthogonalDistance:
                ExecutePointToLine(context);
                break;

            case GeometryCombinerOperation.LineToLineIntersection:
                ExecuteLineToLine(context);
                break;

            case GeometryCombinerOperation.MidpointGenerator:
                ExecuteMidpoint(context);
                break;

            default:
                WriteInvalidOutputs(context);
                break;
        }
    }

    private void ExecutePointToPoint(ToolBlockContext context)
    {
        SlotValue<Point2D> slotA = context.ReadPointSlot(_inPointA);
        SlotValue<Point2D> slotB = context.ReadPointSlot(_inPointB);
        if (!slotA.IsValid || !slotB.IsValid)
        {
            WriteInvalidOutputs(context);
            return;
        }

        Point2D pA = TransformToPhysical(slotA.Value);
        Point2D pB = TransformToPhysical(slotB.Value);

        double dx = pB.X - pA.X;
        double dy = pB.Y - pA.Y;
        double distance = Math.Sqrt((dx * dx) + (dy * dy));
        double angleDeg = Math.Atan2(dy, dx) * RadiansToDegrees;
        Point2D midpointPx = new(
            (slotA.Value.X + slotB.Value.X) * 0.5,
            (slotA.Value.Y + slotB.Value.Y) * 0.5);

        context.WriteBool(_outValid, true, isValid: true);
        context.WriteScalar(_outDistance, distance, isValid: true);
        context.WriteScalar(_outDeltaX, dx, isValid: true);
        context.WriteScalar(_outDeltaY, dy, isValid: true);
        context.WriteScalar(_outAngleDeg, angleDeg, isValid: true);
        context.WritePoint(_outPoint, midpointPx, isValid: true);
    }

    private void ExecutePointToLine(ToolBlockContext context)
    {
        SlotValue<Point2D> slotPoint = context.ReadPointSlot(_inPointA);
        SlotValue<Line2D> slotLine = context.ReadLineSlot(_inLineA);
        if (!slotPoint.IsValid || !slotLine.IsValid)
        {
            WriteInvalidOutputs(context);
            return;
        }

        Point2D pt = slotPoint.Value;
        Line2D line = slotLine.Value;

        Point2D footPx = line.ProjectOrthogonalFoot(pt);
        Point2D physPt = TransformToPhysical(pt);
        Point2D physFoot = TransformToPhysical(footPx);

        double dx = physPt.X - physFoot.X;
        double dy = physPt.Y - physFoot.Y;
        double orthogonalDistance = Math.Sqrt((dx * dx) + (dy * dy));
        double normalAngleDeg = Math.Atan2(line.SinTheta, line.CosTheta) * RadiansToDegrees;

        context.WriteBool(_outValid, true, isValid: true);
        context.WriteScalar(_outDistance, orthogonalDistance, isValid: true);
        context.WriteScalar(_outDeltaX, dx, isValid: true);
        context.WriteScalar(_outDeltaY, dy, isValid: true);
        context.WriteScalar(_outAngleDeg, normalAngleDeg, isValid: true);
        context.WritePoint(_outPoint, footPx, isValid: true);
    }

    private void ExecuteLineToLine(ToolBlockContext context)
    {
        SlotValue<Line2D> slotLineA = context.ReadLineSlot(_inLineA);
        SlotValue<Line2D> slotLineB = context.ReadLineSlot(_inLineB);
        if (!slotLineA.IsValid || !slotLineB.IsValid)
        {
            WriteInvalidOutputs(context);
            return;
        }

        Line2D l1 = slotLineA.Value;
        Line2D l2 = slotLineB.Value;

        double det = (l1.CosTheta * l2.SinTheta) - (l1.SinTheta * l2.CosTheta);
        if (Math.Abs(det) <= ParallelDeterminantFloor)
        {
            WriteInvalidOutputs(context);
            return;
        }

        double invDet = 1.0 / det;
        double ix = ((l1.Rho * l2.SinTheta) - (l1.SinTheta * l2.Rho)) * invDet;
        double iy = ((l1.CosTheta * l2.Rho) - (l1.Rho * l2.CosTheta)) * invDet;
        Point2D intersectionPx = new(ix, iy);

        double dot = Math.Clamp((l1.CosTheta * l2.CosTheta) + (l1.SinTheta * l2.SinTheta), -1.0, 1.0);
        double crossAngleDeg = Math.Acos(Math.Abs(dot)) * RadiansToDegrees;

        Point2D physIntersection = TransformToPhysical(intersectionPx);

        context.WriteBool(_outValid, true, isValid: true);
        context.WriteScalar(_outDistance, 0.0, isValid: true);
        context.WriteScalar(_outDeltaX, physIntersection.X, isValid: true);
        context.WriteScalar(_outDeltaY, physIntersection.Y, isValid: true);
        context.WriteScalar(_outAngleDeg, crossAngleDeg, isValid: true);
        context.WritePoint(_outPoint, intersectionPx, isValid: true);
    }

    private void ExecuteMidpoint(ToolBlockContext context)
    {
        SlotValue<Point2D> slotA = context.ReadPointSlot(_inPointA);
        SlotValue<Point2D> slotB = context.ReadPointSlot(_inPointB);
        if (!slotA.IsValid || !slotB.IsValid)
        {
            WriteInvalidOutputs(context);
            return;
        }

        Point2D midpointPx = new(
            (slotA.Value.X + slotB.Value.X) * 0.5,
            (slotA.Value.Y + slotB.Value.Y) * 0.5);
        Point2D physA = TransformToPhysical(slotA.Value);
        Point2D physB = TransformToPhysical(slotB.Value);
        double spanDist = physA.DistanceTo(physB);

        context.WriteBool(_outValid, true, isValid: true);
        context.WriteScalar(_outDistance, spanDist, isValid: true);
        context.WriteScalar(_outDeltaX, (physA.X + physB.X) * 0.5, isValid: true);
        context.WriteScalar(_outDeltaY, (physA.Y + physB.Y) * 0.5, isValid: true);
        context.WriteScalar(_outAngleDeg, 0.0, isValid: true);
        context.WritePoint(_outPoint, midpointPx, isValid: true);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Point2D TransformToPhysical(Point2D pixelPoint)
    {
        if (_homography is not null)
        {
            Vector2 mapped = _homography.PixelToWorldMm(pixelPoint.ToVector2());
            return new Point2D(mapped.X, mapped.Y);
        }

        return new Point2D(pixelPoint.X * _mmPerPixel, pixelPoint.Y * _mmPerPixel);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteInvalidOutputs(ToolBlockContext context)
    {
        context.WriteBool(_outValid, false, isValid: true);
        context.InvalidateScalar(_outDistance);
        context.InvalidateScalar(_outDeltaX);
        context.InvalidateScalar(_outDeltaY);
        context.InvalidateScalar(_outAngleDeg);
        context.InvalidatePoint(_outPoint);
    }
}
