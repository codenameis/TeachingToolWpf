// File: src/VisionAlgorithm/OpenSrc/ToolBlock/RelativeOffsetCombinerNode.cs
using System.Numerics;
using System.Runtime.CompilerServices;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.CornerAlignment.Domain;
using VisionAlgorithm.OpenSrc.CornerAlignment.Kinematics;

namespace VisionAlgorithm.OpenSrc.ToolBlock;

/// <summary>
/// Virtual data-linking combiner node that projects a target world point (e.g., ROI 2 blob center) into
/// a reference fixture's local coordinate frame (e.g., ROI 1 PatMax pose), computes the rotation-compensated
/// relative deviation $(\Delta X', \Delta Y', \Delta\theta)$ against the master teaching spec, and optionally computes
/// UVW parallel stage actuator strokes via <see cref="UvwStageInverseKinematics"/>.
/// </summary>
public sealed class RelativeOffsetCombinerNode : IToolBlockNode
{
    public const string PortInReferencePose = "InReferencePose";
    public const string PortInTargetPoint = "InTargetPoint";

    public const string PortOutValid = "OutValid";
    public const string PortOutLocalDeltaX = "OutLocalDeltaX";
    public const string PortOutLocalDeltaY = "OutLocalDeltaY";
    public const string PortOutDeltaThetaDeg = "OutDeltaThetaDeg";
    public const string PortOutTotalOffsetMagnitude = "OutTotalOffsetMagnitude";
    public const string PortOutStrokeU = "OutStrokeU";
    public const string PortOutStrokeV = "OutStrokeV";
    public const string PortOutStrokeW = "OutStrokeW";

    // [Math Why]: 180.0 / pi converts radians to degrees.
    private const double RadiansToDegrees = 180.0 / Math.PI;

    private readonly RelativeOffsetSpec _spec;
    private readonly UvwStageInverseKinematics? _uvwKinematics;

    private readonly TerminalHandle<FixturePose2D> _inReferencePose;
    private readonly TerminalHandle<Point2D> _inTargetPoint;

    private readonly TerminalHandle<bool> _outValid;
    private readonly TerminalHandle<double> _outLocalDeltaX;
    private readonly TerminalHandle<double> _outLocalDeltaY;
    private readonly TerminalHandle<double> _outDeltaThetaDeg;
    private readonly TerminalHandle<double> _outTotalOffsetMagnitude;
    private readonly TerminalHandle<double> _outStrokeU;
    private readonly TerminalHandle<double> _outStrokeV;
    private readonly TerminalHandle<double> _outStrokeW;

    private readonly ToolBlockTerminalDescriptor[] _inputDescriptors;
    private readonly ToolBlockTerminalDescriptor[] _outputDescriptors;

    public int NodeId { get; }
    public string NodeName { get; }
    public RelativeOffsetSpec Spec => _spec;

    public TerminalHandle<bool> OutValid => _outValid;
    public TerminalHandle<double> OutLocalDeltaX => _outLocalDeltaX;
    public TerminalHandle<double> OutLocalDeltaY => _outLocalDeltaY;
    public TerminalHandle<double> OutDeltaThetaDeg => _outDeltaThetaDeg;
    public TerminalHandle<double> OutTotalOffsetMagnitude => _outTotalOffsetMagnitude;
    public TerminalHandle<double> OutStrokeU => _outStrokeU;
    public TerminalHandle<double> OutStrokeV => _outStrokeV;
    public TerminalHandle<double> OutStrokeW => _outStrokeW;

    public RelativeOffsetCombinerNode(
        int nodeId,
        string nodeName,
        in RelativeOffsetSpec spec,
        UvwStageInverseKinematics? uvwKinematics,
        TerminalHandle<FixturePose2D> inReferencePose,
        TerminalHandle<Point2D> inTargetPoint,
        TerminalHandle<bool> outValid,
        TerminalHandle<double> outLocalDeltaX,
        TerminalHandle<double> outLocalDeltaY,
        TerminalHandle<double> outDeltaThetaDeg,
        TerminalHandle<double> outTotalOffsetMagnitude,
        TerminalHandle<double> outStrokeU = default,
        TerminalHandle<double> outStrokeV = default,
        TerminalHandle<double> outStrokeW = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeName);
        spec.EnsureValid();

        NodeId = nodeId;
        NodeName = nodeName;
        _spec = spec;
        _uvwKinematics = uvwKinematics;

        _inReferencePose = inReferencePose;
        _inTargetPoint = inTargetPoint;

        _outValid = outValid;
        _outLocalDeltaX = outLocalDeltaX;
        _outLocalDeltaY = outLocalDeltaY;
        _outDeltaThetaDeg = outDeltaThetaDeg;
        _outTotalOffsetMagnitude = outTotalOffsetMagnitude;
        _outStrokeU = outStrokeU;
        _outStrokeV = outStrokeV;
        _outStrokeW = outStrokeW;

        _inputDescriptors =
        [
            new ToolBlockTerminalDescriptor(PortInReferencePose, TerminalDirection.Input, TerminalDataKind.Pose, inReferencePose.SlotIndex),
            new ToolBlockTerminalDescriptor(PortInTargetPoint, TerminalDirection.Input, TerminalDataKind.Point, inTargetPoint.SlotIndex)
        ];

        _outputDescriptors =
        [
            new ToolBlockTerminalDescriptor(PortOutValid, TerminalDirection.Output, TerminalDataKind.Boolean, outValid.SlotIndex),
            new ToolBlockTerminalDescriptor(PortOutLocalDeltaX, TerminalDirection.Output, TerminalDataKind.Scalar, outLocalDeltaX.SlotIndex),
            new ToolBlockTerminalDescriptor(PortOutLocalDeltaY, TerminalDirection.Output, TerminalDataKind.Scalar, outLocalDeltaY.SlotIndex),
            new ToolBlockTerminalDescriptor(PortOutDeltaThetaDeg, TerminalDirection.Output, TerminalDataKind.Scalar, outDeltaThetaDeg.SlotIndex),
            new ToolBlockTerminalDescriptor(PortOutTotalOffsetMagnitude, TerminalDirection.Output, TerminalDataKind.Scalar, outTotalOffsetMagnitude.SlotIndex),
            new ToolBlockTerminalDescriptor(PortOutStrokeU, TerminalDirection.Output, TerminalDataKind.Scalar, outStrokeU.SlotIndex),
            new ToolBlockTerminalDescriptor(PortOutStrokeV, TerminalDirection.Output, TerminalDataKind.Scalar, outStrokeV.SlotIndex),
            new ToolBlockTerminalDescriptor(PortOutStrokeW, TerminalDirection.Output, TerminalDataKind.Scalar, outStrokeW.SlotIndex)
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
        SlotValue<FixturePose2D> poseSlot = context.ReadPoseSlot(_inReferencePose);
        SlotValue<Point2D> targetSlot = context.ReadPointSlot(_inTargetPoint);

        if (!poseSlot.IsValid || !targetSlot.IsValid)
        {
            WriteInvalidOutputs(context);
            return;
        }

        FixturePose2D refPose = poseSlot.Value;
        Point2D targetWorld = targetSlot.Value;

        // Project world point into the reference fixture's local coordinate system
        Vector2 measuredLocal = refPose.WorldToLocal((float)targetWorld.X, (float)targetWorld.Y);

        double deltaLocalXMm = (measuredLocal.X - _spec.MasterLocalX) * _spec.MmPerPixel;
        double deltaLocalYMm = (measuredLocal.Y - _spec.MasterLocalY) * _spec.MmPerPixel;
        double deltaThetaRad = WrapAngleRadians(refPose.AngleRad - _spec.MasterAngleRad);
        double deltaThetaDeg = deltaThetaRad * RadiansToDegrees;
        double offsetMagnitudeMm = Math.Sqrt((deltaLocalXMm * deltaLocalXMm) + (deltaLocalYMm * deltaLocalYMm));

        double strokeU = 0.0;
        double strokeV = 0.0;
        double strokeW = 0.0;

        if (_uvwKinematics is not null)
        {
            // Required stage correction is the negative of the measured part error (-deltaX, -deltaY, -deltaTheta)
            MotionCorrectionCommand command = _uvwKinematics.ComputeActuatorStrokes(
                deltaX_Mm: -deltaLocalXMm,
                deltaY_Mm: -deltaLocalYMm,
                deltaThetaRad: -deltaThetaRad);
            strokeU = command.DeltaU_Mm;
            strokeV = command.DeltaV_Mm;
            strokeW = command.DeltaW_Mm;
        }

        context.WriteBool(_outValid, true, isValid: true);
        context.WriteScalar(_outLocalDeltaX, deltaLocalXMm, isValid: true);
        context.WriteScalar(_outLocalDeltaY, deltaLocalYMm, isValid: true);
        context.WriteScalar(_outDeltaThetaDeg, deltaThetaDeg, isValid: true);
        context.WriteScalar(_outTotalOffsetMagnitude, offsetMagnitudeMm, isValid: true);
        context.WriteScalar(_outStrokeU, strokeU, isValid: true);
        context.WriteScalar(_outStrokeV, strokeV, isValid: true);
        context.WriteScalar(_outStrokeW, strokeW, isValid: true);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double WrapAngleRadians(double angleRad)
        => Math.Atan2(Math.Sin(angleRad), Math.Cos(angleRad));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteInvalidOutputs(ToolBlockContext context)
    {
        context.WriteBool(_outValid, false, isValid: true);
        context.InvalidateScalar(_outLocalDeltaX);
        context.InvalidateScalar(_outLocalDeltaY);
        context.InvalidateScalar(_outDeltaThetaDeg);
        context.InvalidateScalar(_outTotalOffsetMagnitude);
        context.InvalidateScalar(_outStrokeU);
        context.InvalidateScalar(_outStrokeV);
        context.InvalidateScalar(_outStrokeW);
    }
}
