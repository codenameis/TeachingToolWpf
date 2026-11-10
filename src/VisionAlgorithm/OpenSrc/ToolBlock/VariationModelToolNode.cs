// File: src/VisionAlgorithm/OpenSrc/ToolBlock/VariationModelToolNode.cs
using System.Runtime.CompilerServices;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.GoldenVariation;

namespace VisionAlgorithm.OpenSrc.ToolBlock;

/// <summary>
/// Zero-allocation ROI tool node wrapping <see cref="VariationModelInspector"/> to inspect a fixture-linked region
/// against a statistical <see cref="VariationModel"/> and publish defect-free status, defect pixel count, and Z-score.
/// </summary>
public sealed class VariationModelToolNode : IToolBlockNode
{
    public const string PortInFixture = "InFixture";
    public const string PortOutIsDefectFree = "OutIsDefectFree";
    public const string PortOutDefectPixelCount = "OutDefectPixelCount";
    public const string PortOutMaxExcessDiff = "OutMaxExcessDiff";
    public const string PortOutMaxZScore = "OutMaxZScore";

    private readonly VariationModel _model;
    private readonly VariationModelToolSpec _spec;
    private readonly TerminalHandle<FixturePose2D> _inFixture;
    private readonly TerminalHandle<bool> _outIsDefectFree;
    private readonly TerminalHandle<double> _outDefectPixelCount;
    private readonly TerminalHandle<double> _outMaxExcessDiff;
    private readonly TerminalHandle<double> _outMaxZScore;

    private readonly ToolBlockTerminalDescriptor[] _inputDescriptors;
    private readonly ToolBlockTerminalDescriptor[] _outputDescriptors;
    private VariationInspectionResult _lastResult;

    public int NodeId { get; }
    public string NodeName { get; }
    public VariationInspectionResult LastResult => _lastResult;

    public VariationModelToolNode(
        int nodeId,
        string nodeName,
        VariationModel model,
        in VariationModelToolSpec spec,
        TerminalHandle<FixturePose2D> inFixture,
        TerminalHandle<bool> outIsDefectFree,
        TerminalHandle<double> outDefectPixelCount,
        TerminalHandle<double> outMaxExcessDiff,
        TerminalHandle<double> outMaxZScore)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeName);
        ArgumentNullException.ThrowIfNull(model);
        spec.EnsureValid();

        NodeId = nodeId;
        NodeName = nodeName;
        _model = model;
        _spec = spec;
        _inFixture = inFixture;
        _outIsDefectFree = outIsDefectFree;
        _outDefectPixelCount = outDefectPixelCount;
        _outMaxExcessDiff = outMaxExcessDiff;
        _outMaxZScore = outMaxZScore;

        _inputDescriptors = inFixture.IsBound
            ? [new ToolBlockTerminalDescriptor(PortInFixture, TerminalDirection.Input, TerminalDataKind.Pose, inFixture.SlotIndex)]
            : [];

        _outputDescriptors =
        [
            new ToolBlockTerminalDescriptor(PortOutIsDefectFree, TerminalDirection.Output, TerminalDataKind.Boolean, outIsDefectFree.SlotIndex),
            new ToolBlockTerminalDescriptor(PortOutDefectPixelCount, TerminalDirection.Output, TerminalDataKind.Scalar, outDefectPixelCount.SlotIndex),
            new ToolBlockTerminalDescriptor(PortOutMaxExcessDiff, TerminalDirection.Output, TerminalDataKind.Scalar, outMaxExcessDiff.SlotIndex),
            new ToolBlockTerminalDescriptor(PortOutMaxZScore, TerminalDirection.Output, TerminalDataKind.Scalar, outMaxZScore.SlotIndex)
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
            context.WriteBool(_outIsDefectFree, false, isValid: false);
            context.InvalidateScalar(_outDefectPixelCount);
            context.InvalidateScalar(_outMaxExcessDiff);
            context.InvalidateScalar(_outMaxZScore);
            return;
        }

        FixturePose2D worldRoiPose = baseFixture.Compose(_spec.LocalRoiOffset);
        int pixelCount = checked(_model.Width * _model.Height);
        Span<byte> defectMask = workspace.GetByteBufferA(pixelCount);

        VariationInspectionResult res = VariationModelInspector.Inspect(
            image,
            worldRoiPose,
            _model,
            _spec.MinimumDefectAreaPixels,
            workspace,
            defectMask);

        _lastResult = res;
        context.WriteBool(_outIsDefectFree, res.IsDefectFree, isValid: true);
        context.WriteScalar(_outDefectPixelCount, res.DefectPixelCount, isValid: true);
        context.WriteScalar(_outMaxExcessDiff, res.MaxExcessDifference, isValid: true);
        context.WriteScalar(_outMaxZScore, res.MaxNormalizedZScore, isValid: true);
    }
}
