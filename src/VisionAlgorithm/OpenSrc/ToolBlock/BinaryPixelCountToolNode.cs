// File: src/VisionAlgorithm/OpenSrc/ToolBlock/BinaryPixelCountToolNode.cs
using System.Runtime.CompilerServices;
using VisionAlgorithm.OpenSrc.BlobRle;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.ToolBlock;

/// <summary>
/// Zero-allocation <c>ToolBlock</c> node wrapping <see cref="BinaryPixelCountInspector"/> (Cognex <c>CogPixelCountTool</c> equivalent).
/// Publishes valid pixel count, area occupancy percentage, center of mass, mean grayscale intensity, and pass/fail status
/// into <see cref="ToolBlockContext"/> slots, and retains <see cref="LastResult"/> for SPC logging and <c>VisionCanvas</c> replay.
/// </summary>
public sealed class BinaryPixelCountToolNode : IToolBlockNode
{
    public const string PortInFixture = "InFixture";
    public const string PortOutFound = "OutFound";
    public const string PortOutIsPass = "OutIsPass";
    public const string PortOutCenterOfMass = "OutCenterOfMass";
    public const string PortOutValidPixelCount = "OutValidPixelCount";
    public const string PortOutAreaRatioPercent = "OutAreaRatioPercent";
    public const string PortOutMeanGray = "OutMeanGray";

    private readonly BinaryPixelCountSpec _spec;
    private readonly TerminalHandle<FixturePose2D> _inFixture;
    private readonly TerminalHandle<bool> _outFound;
    private readonly TerminalHandle<bool> _outIsPass;
    private readonly TerminalHandle<Point2D> _outCenterOfMass;
    private readonly TerminalHandle<double> _outValidPixelCount;
    private readonly TerminalHandle<double> _outAreaRatioPercent;
    private readonly TerminalHandle<double> _outMeanGray;
    private readonly ToolBlockTerminalDescriptor[] _inputDescriptors;
    private readonly ToolBlockTerminalDescriptor[] _outputDescriptors;
    private BinaryPixelCountResult _lastResult;

    public int NodeId { get; }
    public string NodeName { get; }
    public BinaryPixelCountSpec Spec => _spec;
    public BinaryPixelCountResult LastResult => _lastResult;

    public TerminalHandle<FixturePose2D> InFixture => _inFixture;
    public TerminalHandle<bool> OutFound => _outFound;
    public TerminalHandle<bool> OutIsPass => _outIsPass;
    public TerminalHandle<Point2D> OutCenterOfMass => _outCenterOfMass;
    public TerminalHandle<double> OutValidPixelCount => _outValidPixelCount;
    public TerminalHandle<double> OutAreaRatioPercent => _outAreaRatioPercent;
    public TerminalHandle<double> OutMeanGray => _outMeanGray;

    public BinaryPixelCountToolNode(
        int nodeId,
        string nodeName,
        in BinaryPixelCountSpec spec,
        TerminalHandle<FixturePose2D> inFixture,
        TerminalHandle<bool> outFound,
        TerminalHandle<bool> outIsPass,
        TerminalHandle<Point2D> outCenterOfMass,
        TerminalHandle<double> outValidPixelCount,
        TerminalHandle<double> outAreaRatioPercent,
        TerminalHandle<double> outMeanGray)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeName);
        spec.EnsureInitialized();

        NodeId = nodeId;
        NodeName = nodeName;
        _spec = spec;
        _inFixture = inFixture;
        _outFound = outFound;
        _outIsPass = outIsPass;
        _outCenterOfMass = outCenterOfMass;
        _outValidPixelCount = outValidPixelCount;
        _outAreaRatioPercent = outAreaRatioPercent;
        _outMeanGray = outMeanGray;
        _lastResult = BinaryPixelCountResult.Empty;

        _inputDescriptors = inFixture.IsBound
            ? [new ToolBlockTerminalDescriptor(PortInFixture, TerminalDirection.Input, TerminalDataKind.Pose, inFixture.SlotIndex)]
            : [];

        _outputDescriptors =
        [
            new ToolBlockTerminalDescriptor(PortOutFound, TerminalDirection.Output, TerminalDataKind.Boolean, outFound.SlotIndex),
            new ToolBlockTerminalDescriptor(PortOutIsPass, TerminalDirection.Output, TerminalDataKind.Boolean, outIsPass.SlotIndex),
            new ToolBlockTerminalDescriptor(PortOutCenterOfMass, TerminalDirection.Output, TerminalDataKind.Point, outCenterOfMass.SlotIndex),
            new ToolBlockTerminalDescriptor(PortOutValidPixelCount, TerminalDirection.Output, TerminalDataKind.Scalar, outValidPixelCount.SlotIndex),
            new ToolBlockTerminalDescriptor(PortOutAreaRatioPercent, TerminalDirection.Output, TerminalDataKind.Scalar, outAreaRatioPercent.SlotIndex),
            new ToolBlockTerminalDescriptor(PortOutMeanGray, TerminalDirection.Output, TerminalDataKind.Scalar, outMeanGray.SlotIndex)
        ];
    }

    public ReadOnlySpan<ToolBlockTerminalDescriptor> GetInputTerminals() => _inputDescriptors;

    public ReadOnlySpan<ToolBlockTerminalDescriptor> GetOutputTerminals() => _outputDescriptors;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Execute(
        ReadOnlyImageView2D<byte> image,
        VisionWorkspace workspace,
        ToolBlockContext context)
    {
        if (!context.TryResolveInputFixture(_inFixture, out FixturePose2D fixture))
        {
            _lastResult = BinaryPixelCountResult.Empty;
            PublishInvalid(context);
            return;
        }

        BinaryPixelCountResult res = BinaryPixelCountInspector.Inspect(image, in fixture, in _spec, workspace);
        _lastResult = res;

        context.WriteBool(_outFound, value: true, isValid: true);
        context.WriteBool(_outIsPass, value: res.IsPass, isValid: true);
        context.WritePoint(_outCenterOfMass, new Point2D(res.CenterOfMassWorld.X, res.CenterOfMassWorld.Y), isValid: true);
        context.WriteScalar(_outValidPixelCount, res.ValidPixelCount, isValid: true);
        context.WriteScalar(_outAreaRatioPercent, res.AreaRatioPercent, isValid: true);
        context.WriteScalar(_outMeanGray, res.MeanGrayValue, isValid: true);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PublishInvalid(ToolBlockContext context)
    {
        context.WriteBool(_outFound, value: false, isValid: true);
        context.InvalidateBool(_outIsPass);
        context.InvalidatePoint(_outCenterOfMass);
        context.InvalidateScalar(_outValidPixelCount);
        context.InvalidateScalar(_outAreaRatioPercent);
        context.InvalidateScalar(_outMeanGray);
    }
}
