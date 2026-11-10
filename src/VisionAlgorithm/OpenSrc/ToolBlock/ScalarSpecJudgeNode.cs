// File: src/VisionAlgorithm/OpenSrc/ToolBlock/ScalarSpecJudgeNode.cs
using System.Runtime.CompilerServices;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.ToolBlock;

/// <summary>
/// Virtual tolerance judgment node that evaluates whether a scalar measurement (or difference/ratio of two measurements)
/// falls within <c>[LSL, USL]</c> and enforces the configured <see cref="MissingPolicy"/> when upstream detection fails.
/// </summary>
public sealed class ScalarSpecJudgeNode : IToolBlockNode
{
    public const string PortInPrimaryValue = "InPrimaryValue";
    public const string PortInSecondaryValue = "InSecondaryValue";
    public const string PortOutEvaluatedValue = "OutEvaluatedValue";
    public const string PortOutIsOk = "OutIsOk";

    // [Math Why]: Floor 1e-12 prevents division by zero when evaluating scalar ratios.
    private const double RatioDenominatorFloor = 1e-12;

    private readonly ScalarSpecLimit _specLimit;
    private readonly TerminalHandle<double> _inPrimaryValue;
    private readonly TerminalHandle<double> _inSecondaryValue;
    private readonly TerminalHandle<double> _outEvaluatedValue;
    private readonly TerminalHandle<bool> _outIsOk;

    private readonly ToolBlockTerminalDescriptor[] _inputDescriptors;
    private readonly ToolBlockTerminalDescriptor[] _outputDescriptors;

    public int NodeId { get; }
    public string NodeName { get; }
    public ScalarSpecLimit SpecLimit => _specLimit;

    public TerminalHandle<double> InPrimaryValue => _inPrimaryValue;
    public TerminalHandle<double> InSecondaryValue => _inSecondaryValue;
    public TerminalHandle<double> OutEvaluatedValue => _outEvaluatedValue;
    public TerminalHandle<bool> OutIsOk => _outIsOk;

    public ScalarSpecJudgeNode(
        int nodeId,
        string nodeName,
        in ScalarSpecLimit specLimit,
        TerminalHandle<double> inPrimaryValue,
        TerminalHandle<bool> outIsOk,
        TerminalHandle<double> outEvaluatedValue = default,
        TerminalHandle<double> inSecondaryValue = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeName);
        specLimit.EnsureValid();

        NodeId = nodeId;
        NodeName = nodeName;
        _specLimit = specLimit;
        _inPrimaryValue = inPrimaryValue;
        _inSecondaryValue = inSecondaryValue;
        _outEvaluatedValue = outEvaluatedValue;
        _outIsOk = outIsOk;

        var inputs = new List<ToolBlockTerminalDescriptor>(2)
        {
            new ToolBlockTerminalDescriptor(PortInPrimaryValue, TerminalDirection.Input, TerminalDataKind.Scalar, inPrimaryValue.SlotIndex)
        };
        if (inSecondaryValue.IsBound)
        {
            inputs.Add(new ToolBlockTerminalDescriptor(PortInSecondaryValue, TerminalDirection.Input, TerminalDataKind.Scalar, inSecondaryValue.SlotIndex));
        }

        _inputDescriptors = inputs.ToArray();

        _outputDescriptors =
        [
            new ToolBlockTerminalDescriptor(PortOutIsOk, TerminalDirection.Output, TerminalDataKind.Boolean, outIsOk.SlotIndex),
            new ToolBlockTerminalDescriptor(PortOutEvaluatedValue, TerminalDirection.Output, TerminalDataKind.Scalar, outEvaluatedValue.SlotIndex)
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
        if (!TryComputeScalarValue(context, out double evaluatedValue))
        {
            ApplyMissingPolicy(context);
            return;
        }

        bool isWithinSpec = evaluatedValue >= _specLimit.LowerSpecLimit && evaluatedValue <= _specLimit.UpperSpecLimit;
        context.WriteScalar(_outEvaluatedValue, evaluatedValue, isValid: true);
        context.WriteBool(_outIsOk, isWithinSpec, isValid: true);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryComputeScalarValue(ToolBlockContext context, out double result)
    {
        SlotValue<double> primary = context.ReadScalarSlot(_inPrimaryValue);
        if (!primary.IsValid)
        {
            result = 0.0;
            return false;
        }

        if (_specLimit.CombineMode == ScalarCombineMode.SingleValue)
        {
            result = primary.Value;
            return true;
        }

        SlotValue<double> secondary = context.ReadScalarSlot(_inSecondaryValue);
        if (!secondary.IsValid)
        {
            result = 0.0;
            return false;
        }

        switch (_specLimit.CombineMode)
        {
            case ScalarCombineMode.Difference:
                result = primary.Value - secondary.Value;
                return true;

            case ScalarCombineMode.AbsoluteDifference:
                result = Math.Abs(primary.Value - secondary.Value);
                return true;

            case ScalarCombineMode.Ratio:
                if (Math.Abs(secondary.Value) <= RatioDenominatorFloor)
                {
                    result = 0.0;
                    return false;
                }

                result = primary.Value / secondary.Value;
                return true;

            default:
                result = primary.Value;
                return true;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ApplyMissingPolicy(ToolBlockContext context)
    {
        switch (_specLimit.MissingPolicy)
        {
            case MissingPolicy.ForceNg:
                context.InvalidateScalar(_outEvaluatedValue);
                context.WriteBool(_outIsOk, false, isValid: true);
                break;

            case MissingPolicy.UseFallbackValue:
                double fallback = _specLimit.FallbackValue;
                bool ok = fallback >= _specLimit.LowerSpecLimit && fallback <= _specLimit.UpperSpecLimit;
                context.WriteScalar(_outEvaluatedValue, fallback, isValid: true);
                context.WriteBool(_outIsOk, ok, isValid: true);
                break;

            case MissingPolicy.Ignore:
                context.InvalidateScalar(_outEvaluatedValue);
                context.WriteBool(_outIsOk, true, isValid: true);
                break;

            default:
                context.InvalidateScalar(_outEvaluatedValue);
                context.WriteBool(_outIsOk, false, isValid: true);
                break;
        }
    }
}
