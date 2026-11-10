// File: src/VisionAlgorithm/OpenSrc/ToolBlock/FinalVerdictAggregatorNode.cs
using System.Runtime.CompilerServices;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.ToolBlock;

/// <summary>
/// Terminal sink node that combines multiple <see cref="VerdictGateBinding"/> boolean slots via <see cref="VerdictLogicGate"/>
/// (<c>AllAnd</c>, <c>AnyOr</c>, or <c>CustomMask</c>) and packages the final <see cref="FinalDecision"/>, first failing node ID,
/// summary metrics, and alignment control offsets into <see cref="ToolBlockContext.FinalResult"/> with zero heap allocations.
/// </summary>
public sealed class FinalVerdictAggregatorNode : IToolBlockNode
{
    public const string PortInPrimaryMetric = "InPrimaryMetric";
    public const string PortInSecondaryMetric = "InSecondaryMetric";
    public const string PortInAlignDeltaX = "InAlignDeltaX";
    public const string PortInAlignDeltaY = "InAlignDeltaY";
    public const string PortInAlignDeltaThetaDeg = "InAlignDeltaThetaDeg";
    public const string PortInStrokeU = "InStrokeU";
    public const string PortInStrokeV = "InStrokeV";
    public const string PortInStrokeW = "InStrokeW";
    public const string PortOutFinalOk = "OutFinalOk";

    // [Perf Why]: Up to 8 optional scalar telemetry ports (Primary, Secondary, dX, dY, dTheta, dU, dV, dW) can be attached alongside boolean gates.
    private const int MaxOptionalScalarInputPorts = 8;

    private readonly VerdictLogicGate _logicGate;
    private readonly ulong _customBitMask;
    private readonly VerdictGateBinding[] _gateBindings;

    private readonly TerminalHandle<double> _inPrimaryMetric;
    private readonly TerminalHandle<double> _inSecondaryMetric;
    private readonly TerminalHandle<double> _inAlignDeltaX;
    private readonly TerminalHandle<double> _inAlignDeltaY;
    private readonly TerminalHandle<double> _inAlignDeltaThetaDeg;
    private readonly TerminalHandle<double> _inStrokeU;
    private readonly TerminalHandle<double> _inStrokeV;
    private readonly TerminalHandle<double> _inStrokeW;
    private readonly TerminalHandle<bool> _outFinalOk;

    private readonly ToolBlockTerminalDescriptor[] _inputDescriptors;
    private readonly ToolBlockTerminalDescriptor[] _outputDescriptors;

    public int NodeId { get; }
    public string NodeName { get; }
    public VerdictLogicGate LogicGate => _logicGate;
    public ReadOnlySpan<VerdictGateBinding> GateBindings => _gateBindings;

    public FinalVerdictAggregatorNode(
        int nodeId,
        string nodeName,
        VerdictLogicGate logicGate,
        VerdictGateBinding[] gateBindings,
        TerminalHandle<bool> outFinalOk,
        TerminalHandle<double> inPrimaryMetric = default,
        TerminalHandle<double> inSecondaryMetric = default,
        TerminalHandle<double> inAlignDeltaX = default,
        TerminalHandle<double> inAlignDeltaY = default,
        TerminalHandle<double> inAlignDeltaThetaDeg = default,
        TerminalHandle<double> inStrokeU = default,
        TerminalHandle<double> inStrokeV = default,
        TerminalHandle<double> inStrokeW = default,
        ulong customBitMask = ulong.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeName);
        ArgumentNullException.ThrowIfNull(gateBindings);
        if (gateBindings.Length == 0)
        {
            throw new ArgumentException("At least one VerdictGateBinding is required.", nameof(gateBindings));
        }

        NodeId = nodeId;
        NodeName = nodeName;
        _logicGate = logicGate;
        _customBitMask = customBitMask;
        _gateBindings = gateBindings;

        _inPrimaryMetric = inPrimaryMetric;
        _inSecondaryMetric = inSecondaryMetric;
        _inAlignDeltaX = inAlignDeltaX;
        _inAlignDeltaY = inAlignDeltaY;
        _inAlignDeltaThetaDeg = inAlignDeltaThetaDeg;
        _inStrokeU = inStrokeU;
        _inStrokeV = inStrokeV;
        _inStrokeW = inStrokeW;
        _outFinalOk = outFinalOk;

        var inputs = new List<ToolBlockTerminalDescriptor>(gateBindings.Length + MaxOptionalScalarInputPorts);
        for (int i = 0; i < gateBindings.Length; i++)
        {
            VerdictGateBinding g = gateBindings[i];
            inputs.Add(new ToolBlockTerminalDescriptor(g.GateName, TerminalDirection.Input, TerminalDataKind.Boolean, g.Handle.SlotIndex));
        }

        if (inPrimaryMetric.IsBound) inputs.Add(new ToolBlockTerminalDescriptor(PortInPrimaryMetric, TerminalDirection.Input, TerminalDataKind.Scalar, inPrimaryMetric.SlotIndex));
        if (inSecondaryMetric.IsBound) inputs.Add(new ToolBlockTerminalDescriptor(PortInSecondaryMetric, TerminalDirection.Input, TerminalDataKind.Scalar, inSecondaryMetric.SlotIndex));
        if (inAlignDeltaX.IsBound) inputs.Add(new ToolBlockTerminalDescriptor(PortInAlignDeltaX, TerminalDirection.Input, TerminalDataKind.Scalar, inAlignDeltaX.SlotIndex));
        if (inAlignDeltaY.IsBound) inputs.Add(new ToolBlockTerminalDescriptor(PortInAlignDeltaY, TerminalDirection.Input, TerminalDataKind.Scalar, inAlignDeltaY.SlotIndex));
        if (inAlignDeltaThetaDeg.IsBound) inputs.Add(new ToolBlockTerminalDescriptor(PortInAlignDeltaThetaDeg, TerminalDirection.Input, TerminalDataKind.Scalar, inAlignDeltaThetaDeg.SlotIndex));
        if (inStrokeU.IsBound) inputs.Add(new ToolBlockTerminalDescriptor(PortInStrokeU, TerminalDirection.Input, TerminalDataKind.Scalar, inStrokeU.SlotIndex));
        if (inStrokeV.IsBound) inputs.Add(new ToolBlockTerminalDescriptor(PortInStrokeV, TerminalDirection.Input, TerminalDataKind.Scalar, inStrokeV.SlotIndex));
        if (inStrokeW.IsBound) inputs.Add(new ToolBlockTerminalDescriptor(PortInStrokeW, TerminalDirection.Input, TerminalDataKind.Scalar, inStrokeW.SlotIndex));

        _inputDescriptors = inputs.ToArray();
        _outputDescriptors =
        [
            new ToolBlockTerminalDescriptor(PortOutFinalOk, TerminalDirection.Output, TerminalDataKind.Boolean, outFinalOk.SlotIndex)
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
        VerdictGateBinding[] gates = _gateBindings;
        int totalGates = gates.Length;
        int evaluatedCount = 0;
        int passedCount = 0;
        int firstFailedNodeId = 0;
        bool hasInvalidError = false;

        for (int i = 0; i < totalGates; i++)
        {
            if (_logicGate == VerdictLogicGate.CustomMask && ((_customBitMask >> i) & 1UL) == 0UL)
            {
                continue;
            }

            evaluatedCount++;
            VerdictGateBinding binding = gates[i];
            SlotValue<bool> slot = context.ReadBoolSlot(binding.Handle);

            if (!slot.IsValid)
            {
                hasInvalidError = true;
                if (firstFailedNodeId == 0)
                {
                    firstFailedNodeId = binding.SourceNodeId;
                }

                continue;
            }

            if (slot.Value)
            {
                passedCount++;
            }
            else if (firstFailedNodeId == 0)
            {
                firstFailedNodeId = binding.SourceNodeId;
            }
        }

        FinalDecision decision;
        if (hasInvalidError)
        {
            decision = FinalDecision.Error;
        }
        else
        {
            bool isOk = _logicGate switch
            {
                VerdictLogicGate.AllAnd => evaluatedCount > 0 && passedCount == evaluatedCount,
                VerdictLogicGate.AnyOr => passedCount > 0,
                VerdictLogicGate.CustomMask => evaluatedCount == 0 || passedCount == evaluatedCount,
                _ => false
            };

            decision = isOk ? FinalDecision.Ok : FinalDecision.Ng;
            if (isOk)
            {
                firstFailedNodeId = 0;
            }
        }

        context.WriteBool(_outFinalOk, decision == FinalDecision.Ok, isValid: true);

        SlotValue<double> primaryMetric = context.ReadScalarSlot(_inPrimaryMetric);
        SlotValue<double> secondaryMetric = context.ReadScalarSlot(_inSecondaryMetric);
        SlotValue<double> alignDx = context.ReadScalarSlot(_inAlignDeltaX);
        SlotValue<double> alignDy = context.ReadScalarSlot(_inAlignDeltaY);
        SlotValue<double> alignDTheta = context.ReadScalarSlot(_inAlignDeltaThetaDeg);
        SlotValue<double> strokeU = context.ReadScalarSlot(_inStrokeU);
        SlotValue<double> strokeV = context.ReadScalarSlot(_inStrokeV);
        SlotValue<double> strokeW = context.ReadScalarSlot(_inStrokeW);

        bool hasAlignment = alignDx.IsValid && alignDy.IsValid && alignDTheta.IsValid;

        var finalVerdict = new ToolBlockInspectionResult(
            Decision: decision,
            FirstFailedNodeId: firstFailedNodeId,
            EvaluatedGateCount: evaluatedCount,
            PassedGateCount: passedCount,
            PrimaryMetricValue: primaryMetric.IsValid ? primaryMetric.Value : 0.0,
            SecondaryMetricValue: secondaryMetric.IsValid ? secondaryMetric.Value : 0.0,
            AlignmentDeltaX: alignDx.IsValid ? alignDx.Value : 0.0,
            AlignmentDeltaY: alignDy.IsValid ? alignDy.Value : 0.0,
            AlignmentDeltaThetaDeg: alignDTheta.IsValid ? alignDTheta.Value : 0.0,
            ActuatorStrokeU: strokeU.IsValid ? strokeU.Value : 0.0,
            ActuatorStrokeV: strokeV.IsValid ? strokeV.Value : 0.0,
            ActuatorStrokeW: strokeW.IsValid ? strokeW.Value : 0.0,
            HasValidAlignment: hasAlignment);

        context.WriteFinalResult(in finalVerdict);
    }
}
