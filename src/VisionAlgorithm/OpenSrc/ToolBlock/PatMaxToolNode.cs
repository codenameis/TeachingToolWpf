// File: src/VisionAlgorithm/OpenSrc/ToolBlock/PatMaxToolNode.cs
using System.Runtime.CompilerServices;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.PatMax;

namespace VisionAlgorithm.OpenSrc.ToolBlock;

/// <summary>
/// ROI tool node wrapping <see cref="PatMaxEngine"/> to locate one or multiple trained <see cref="PatMaxModel"/> templates
/// and publish the resulting <see cref="FixturePose2D"/>, center <see cref="Point2D"/>, rotation angle, score,
/// and winning template index into <see cref="ToolBlockContext"/> without boxing or heap allocations.
/// </summary>
public sealed class PatMaxToolNode : IToolBlockNode
{
    public const string PortOutFound = "OutFound";
    public const string PortOutPose = "OutPose";
    public const string PortOutCenter = "OutCenter";
    public const string PortOutAngleDeg = "OutAngleDeg";
    public const string PortOutScore = "OutScore";
    public const string PortOutMatchedTemplateIndex = "OutMatchedTemplateIndex";

    // [Math Why]: 180.0 / pi converts radians to degrees.
    private const double RadiansToDegrees = 180.0 / Math.PI;

    private readonly PatMaxEngine _engine;
    private readonly PatMaxModel[] _models;
    private readonly int[] _modelIds;
    private readonly PatMaxSearchParams _searchParams;

    private readonly TerminalHandle<bool> _outFound;
    private readonly TerminalHandle<FixturePose2D> _outPose;
    private readonly TerminalHandle<Point2D> _outCenter;
    private readonly TerminalHandle<double> _outAngleDeg;
    private readonly TerminalHandle<double> _outScore;
    private readonly TerminalHandle<double> _outMatchedTemplateIndex;

    private readonly ToolBlockTerminalDescriptor[] _outputDescriptors;
    private PatMaxResult _lastResult;

    public int NodeId { get; }
    public string NodeName { get; }
    public PatMaxResult LastResult => _lastResult;
    public int TemplateCount => _models.Length;

    public TerminalHandle<bool> OutFound => _outFound;
    public TerminalHandle<FixturePose2D> OutPose => _outPose;
    public TerminalHandle<Point2D> OutCenter => _outCenter;
    public TerminalHandle<double> OutAngleDeg => _outAngleDeg;
    public TerminalHandle<double> OutScore => _outScore;
    public TerminalHandle<double> OutMatchedTemplateIndex => _outMatchedTemplateIndex;

    public PatMaxToolNode(
        int nodeId,
        string nodeName,
        PatMaxEngine engine,
        PatMaxModel model,
        in PatMaxSearchParams searchParams,
        TerminalHandle<bool> outFound,
        TerminalHandle<FixturePose2D> outPose,
        TerminalHandle<Point2D> outCenter,
        TerminalHandle<double> outAngleDeg,
        TerminalHandle<double> outScore,
        TerminalHandle<double> outMatchedTemplateIndex = default)
        : this(
            nodeId,
            nodeName,
            engine,
            [model ?? throw new ArgumentNullException(nameof(model))],
            [1],
            in searchParams,
            outFound,
            outPose,
            outCenter,
            outAngleDeg,
            outScore,
            outMatchedTemplateIndex)
    {
    }

    public PatMaxToolNode(
        int nodeId,
        string nodeName,
        PatMaxEngine engine,
        PatMaxModel[] models,
        int[] modelIds,
        in PatMaxSearchParams searchParams,
        TerminalHandle<bool> outFound,
        TerminalHandle<FixturePose2D> outPose,
        TerminalHandle<Point2D> outCenter,
        TerminalHandle<double> outAngleDeg,
        TerminalHandle<double> outScore,
        TerminalHandle<double> outMatchedTemplateIndex = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeName);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(modelIds);
        if (models.Length == 0)
        {
            throw new ArgumentException("At least one PatMaxModel template must be provided.", nameof(models));
        }

        if (searchParams.IsEmpty)
        {
            throw new ArgumentException("PatMaxSearchParams must not be empty.", nameof(searchParams));
        }

        NodeId = nodeId;
        NodeName = nodeName;
        _engine = engine;
        _models = models;
        _modelIds = modelIds;
        _searchParams = searchParams;
        _outFound = outFound;
        _outPose = outPose;
        _outCenter = outCenter;
        _outAngleDeg = outAngleDeg;
        _outScore = outScore;
        _outMatchedTemplateIndex = outMatchedTemplateIndex;
        _lastResult = PatMaxResult.Empty;

        if (outMatchedTemplateIndex.IsBound)
        {
            _outputDescriptors =
            [
                new ToolBlockTerminalDescriptor(PortOutFound, TerminalDirection.Output, TerminalDataKind.Boolean, outFound.SlotIndex),
                new ToolBlockTerminalDescriptor(PortOutPose, TerminalDirection.Output, TerminalDataKind.Pose, outPose.SlotIndex),
                new ToolBlockTerminalDescriptor(PortOutCenter, TerminalDirection.Output, TerminalDataKind.Point, outCenter.SlotIndex),
                new ToolBlockTerminalDescriptor(PortOutAngleDeg, TerminalDirection.Output, TerminalDataKind.Scalar, outAngleDeg.SlotIndex),
                new ToolBlockTerminalDescriptor(PortOutScore, TerminalDirection.Output, TerminalDataKind.Scalar, outScore.SlotIndex),
                new ToolBlockTerminalDescriptor(PortOutMatchedTemplateIndex, TerminalDirection.Output, TerminalDataKind.Scalar, outMatchedTemplateIndex.SlotIndex)
            ];
        }
        else
        {
            _outputDescriptors =
            [
                new ToolBlockTerminalDescriptor(PortOutFound, TerminalDirection.Output, TerminalDataKind.Boolean, outFound.SlotIndex),
                new ToolBlockTerminalDescriptor(PortOutPose, TerminalDirection.Output, TerminalDataKind.Pose, outPose.SlotIndex),
                new ToolBlockTerminalDescriptor(PortOutCenter, TerminalDirection.Output, TerminalDataKind.Point, outCenter.SlotIndex),
                new ToolBlockTerminalDescriptor(PortOutAngleDeg, TerminalDirection.Output, TerminalDataKind.Scalar, outAngleDeg.SlotIndex),
                new ToolBlockTerminalDescriptor(PortOutScore, TerminalDirection.Output, TerminalDataKind.Scalar, outScore.SlotIndex)
            ];
        }
    }

    public ReadOnlySpan<ToolBlockTerminalDescriptor> GetInputTerminals()
        => ReadOnlySpan<ToolBlockTerminalDescriptor>.Empty;

    public ReadOnlySpan<ToolBlockTerminalDescriptor> GetOutputTerminals()
        => _outputDescriptors;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Execute(
        ReadOnlyImageView2D<byte> image,
        VisionWorkspace workspace,
        ToolBlockContext context)
    {
        PatMaxResult best = _engine.FindBestAcrossModels(
            _models,
            _modelIds,
            image.Pixels,
            image.Width,
            image.Height,
            image.Stride,
            in _searchParams);

        _lastResult = best;

        if (!best.IsValid)
        {
            context.WriteBool(_outFound, false, isValid: true);
            context.InvalidatePose(_outPose);
            context.InvalidatePoint(_outCenter);
            context.InvalidateScalar(_outAngleDeg);
            context.InvalidateScalar(_outScore);
            context.InvalidateScalar(_outMatchedTemplateIndex);
            return;
        }

        FixturePose2D pose = new(
            centerX: best.X,
            centerY: best.Y,
            angleRad: best.AngleRadians,
            scaleX: best.Scale,
            scaleY: best.Scale * best.AspectRatio);

        Point2D center = new(best.X, best.Y);
        double angleDeg = best.AngleRadians * RadiansToDegrees;

        context.WriteBool(_outFound, true, isValid: true);
        context.WritePose(_outPose, in pose, isValid: true);
        context.WritePoint(_outCenter, center, isValid: true);
        context.WriteScalar(_outAngleDeg, angleDeg, isValid: true);
        context.WriteScalar(_outScore, best.Score, isValid: true);
        context.WriteScalar(_outMatchedTemplateIndex, best.MatchedTemplateIndex, isValid: true);
    }
}
