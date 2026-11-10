// File: src/VisionAlgorithm/OpenSrc/ToolBlock/ToolBlockScenarioPipeline.cs
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.CornerAlignment.Kinematics;
using VisionAlgorithm.OpenSrc.PatMax;

namespace VisionAlgorithm.OpenSrc.ToolBlock;

/// <summary>
/// End-to-End reference pipeline assembling:
/// <list type="number">
/// <item><description><see cref="PatMaxToolNode"/> (Node 10: Pattern Alignment in ROI 1)</description></item>
/// <item><description><see cref="LargestBlobToolNode"/> (Node 20: Fixture-linked RLE Largest Blob in ROI 2)</description></item>
/// <item><description><see cref="GeometryCombinerNode"/> (Node 30: Euclidean Distance, Angle, and Midpoint between ROI 1 &amp; ROI 2)</description></item>
/// <item><description><see cref="RelativeOffsetCombinerNode"/> (Node 40: Rotation-compensated Local Offset &amp; UVW Stage Inverse Kinematics)</description></item>
/// <item><description><see cref="ScalarSpecJudgeNode"/> (Nodes 50, 51, 52: LSL/USL Tolerance Judges for Distance, Area, and Offset)</description></item>
/// <item><description><see cref="FinalVerdictAggregatorNode"/> (Node 60: Final Verdict &amp; Motion Control Aggregator)</description></item>
/// </list>
/// </summary>
public sealed class ToolBlockScenarioPipeline : IDisposable
{
    public const int NodeIdPatMax = 10;
    public const int NodeIdLargestBlob = 20;
    public const int NodeIdGeometryCombiner = 30;
    public const int NodeIdRelativeOffset = 40;
    public const int NodeIdDistanceJudge = 50;
    public const int NodeIdAreaJudge = 51;
    public const int NodeIdOffsetJudge = 52;
    public const int NodeIdFinalVerdict = 60;

    public const int DefaultImageWidth = 384;
    public const int DefaultImageHeight = 256;
    public const int DefaultTemplateSize = 80;
    public const double DefaultMmPerPixel = 0.05; // 50 um / pixel -> 60 px = 3.00 mm
    public const float NominalLocalBlobOffsetX = 60.0f;
    public const float NominalLocalBlobOffsetY = 0.0f;
    public const float NominalBlobRadiusPixels = 16.0f;

    private const byte BackgroundIntensity = 32;
    private const byte FiducialBrightIntensity = 215;
    private const byte BlobForegroundIntensity = 235;
    private const float FiducialCrossHalfExtent = 22.0f;
    private const float FiducialBarHalfThickness = 6.5f;
    private const float FiducialCornerNotchMin = 8.0f;
    private const float FiducialCornerNotchMax = 20.0f;
    private const float SoftDiskAAWindowRadius = 2.0f;
    private const int DefaultBlobRoiSize = 72;
    private const byte DefaultBlobMinIntensity = 160;
    private const long DefaultBlobMinArea = 80;
    private const double StandardUvwStageRadiusMm = 150.0;

    private readonly PatMaxEngine _patMaxEngine;
    private readonly PatMaxModel _patMaxModel;
    private readonly VisionWorkspace _workspace;
    private readonly ToolBlockContext _context;
    private readonly ToolBlockRunner _runner;

    // Terminal handles for inspecting intermediate values on UI / tests
    private readonly TerminalHandle<bool> _hPatFound;
    private readonly TerminalHandle<FixturePose2D> _hPatPose;
    private readonly TerminalHandle<Point2D> _hPatCenter;
    private readonly TerminalHandle<double> _hPatAngleDeg;
    private readonly TerminalHandle<double> _hPatScore;

    private readonly TerminalHandle<bool> _hBlobFound;
    private readonly TerminalHandle<double> _hBlobCount;
    private readonly TerminalHandle<Point2D> _hBlobCenter;
    private readonly TerminalHandle<double> _hBlobArea;
    private readonly TerminalHandle<double> _hBlobAngleDeg;
    private readonly TerminalHandle<double> _hBlobEccentricity;

    private readonly TerminalHandle<bool> _hGeoValid;
    private readonly TerminalHandle<double> _hGeoDistMm;
    private readonly TerminalHandle<double> _hGeoDxMm;
    private readonly TerminalHandle<double> _hGeoDyMm;
    private readonly TerminalHandle<double> _hGeoAngleDeg;
    private readonly TerminalHandle<Point2D> _hGeoMidpoint;

    private readonly TerminalHandle<bool> _hRelValid;
    private readonly TerminalHandle<double> _hRelLocalDxMm;
    private readonly TerminalHandle<double> _hRelLocalDyMm;
    private readonly TerminalHandle<double> _hRelDeltaThetaDeg;
    private readonly TerminalHandle<double> _hRelTotalOffsetMm;
    private readonly TerminalHandle<double> _hStrokeU;
    private readonly TerminalHandle<double> _hStrokeV;
    private readonly TerminalHandle<double> _hStrokeW;

    private readonly TerminalHandle<bool> _hDistOk;
    private readonly TerminalHandle<double> _hDistEval;
    private readonly TerminalHandle<bool> _hAreaOk;
    private readonly TerminalHandle<double> _hAreaEval;
    private readonly TerminalHandle<bool> _hOffsetOk;
    private readonly TerminalHandle<double> _hOffsetEval;
    private readonly TerminalHandle<bool> _hFinalOk;

    private bool _disposed;

    public ToolBlockRunner Runner => _runner;
    public ToolBlockContext Context => _context;

    public ToolBlockScenarioPipeline(
        double distanceLslMm = 2.75,
        double distanceUslMm = 3.25,
        double blobAreaLslPx = 600.0,
        double blobAreaUslPx = 1050.0,
        double maxRelativeOffsetMm = 0.25,
        double mmPerPixel = DefaultMmPerPixel)
    {
        _patMaxEngine = new PatMaxEngine();
        _patMaxModel = TrainCrossSquareFiducialModel(DefaultTemplateSize);
        _workspace = new VisionWorkspace(DefaultImageWidth * DefaultImageHeight);

        var builder = new ToolBlockBuilder();

        // 1. Allocate strongly-typed terminal slots
        _hPatFound = builder.AllocateBoolSlot();
        _hPatPose = builder.AllocatePoseSlot();
        _hPatCenter = builder.AllocatePointSlot();
        _hPatAngleDeg = builder.AllocateScalarSlot();
        _hPatScore = builder.AllocateScalarSlot();

        _hBlobFound = builder.AllocateBoolSlot();
        _hBlobCount = builder.AllocateScalarSlot();
        _hBlobCenter = builder.AllocatePointSlot();
        _hBlobArea = builder.AllocateScalarSlot();
        _hBlobAngleDeg = builder.AllocateScalarSlot();
        _hBlobEccentricity = builder.AllocateScalarSlot();

        _hGeoValid = builder.AllocateBoolSlot();
        _hGeoDistMm = builder.AllocateScalarSlot();
        _hGeoDxMm = builder.AllocateScalarSlot();
        _hGeoDyMm = builder.AllocateScalarSlot();
        _hGeoAngleDeg = builder.AllocateScalarSlot();
        _hGeoMidpoint = builder.AllocatePointSlot();

        _hRelValid = builder.AllocateBoolSlot();
        _hRelLocalDxMm = builder.AllocateScalarSlot();
        _hRelLocalDyMm = builder.AllocateScalarSlot();
        _hRelDeltaThetaDeg = builder.AllocateScalarSlot();
        _hRelTotalOffsetMm = builder.AllocateScalarSlot();
        _hStrokeU = builder.AllocateScalarSlot();
        _hStrokeV = builder.AllocateScalarSlot();
        _hStrokeW = builder.AllocateScalarSlot();

        _hDistOk = builder.AllocateBoolSlot();
        _hDistEval = builder.AllocateScalarSlot();
        _hAreaOk = builder.AllocateBoolSlot();
        _hAreaEval = builder.AllocateScalarSlot();
        _hOffsetOk = builder.AllocateBoolSlot();
        _hOffsetEval = builder.AllocateScalarSlot();
        _hFinalOk = builder.AllocateBoolSlot();

        // 2. Instantiate ROI Tool Nodes & Virtual Combiner Nodes
        PatMaxSearchParams searchParams = PatMaxSearchParams.Create(
            acceptThreshold: 0.50f,
            angleStartRadians: -0.60f,
            angleExtentRadians: 1.20f,
            scaleMin: 0.95f,
            scaleMax: 1.05f,
            runtimeEdgeThreshold: 12.0f,
            ignorePolarity: false,
            maxCandidates: 16,
            maxResults: 1,
            minSeparationPixels: 8.0f);

        var patMaxNode = new PatMaxToolNode(
            nodeId: NodeIdPatMax,
            nodeName: "ROI1_PatMaxAlign",
            engine: _patMaxEngine,
            model: _patMaxModel,
            searchParams: in searchParams,
            outFound: _hPatFound,
            outPose: _hPatPose,
            outCenter: _hPatCenter,
            outAngleDeg: _hPatAngleDeg,
            outScore: _hPatScore);

        var blobSpec = new LargestBlobToolSpec(
            localRoiOffset: new FixturePose2D(NominalLocalBlobOffsetX, NominalLocalBlobOffsetY, 0f, 1f),
            roiWidth: DefaultBlobRoiSize,
            roiHeight: DefaultBlobRoiSize,
            minIntensity: DefaultBlobMinIntensity,
            maxIntensity: byte.MaxValue,
            minimumAreaPixels: DefaultBlobMinArea,
            morphologyOperation: BlobMorphologyOperation.OpenBox,
            morphologyRadiusX: 1,
            morphologyRadiusY: 1);

        var blobNode = new LargestBlobToolNode(
            nodeId: NodeIdLargestBlob,
            nodeName: "ROI2_LargestBlob",
            spec: in blobSpec,
            inFixture: _hPatPose, // Fixturing Link from PatMax OutPose!
            outFound: _hBlobFound,
            outBlobCount: _hBlobCount,
            outMaxBlobCenter: _hBlobCenter,
            outMaxBlobArea: _hBlobArea,
            outMaxBlobAngleDeg: _hBlobAngleDeg,
            outEccentricity: _hBlobEccentricity);

        var geoNode = new GeometryCombinerNode(
            nodeId: NodeIdGeometryCombiner,
            nodeName: "Geo_CenterToBlobDist",
            operation: GeometryCombinerOperation.PointToPointDistanceAndAngle,
            mmPerPixel: mmPerPixel,
            homography: null,
            inPointA: _hPatCenter,
            inPointB: _hBlobCenter,
            inLineA: TerminalHandle<Line2D>.Unbound,
            inLineB: TerminalHandle<Line2D>.Unbound,
            outValid: _hGeoValid,
            outDistance: _hGeoDistMm,
            outDeltaX: _hGeoDxMm,
            outDeltaY: _hGeoDyMm,
            outAngleDeg: _hGeoAngleDeg,
            outPoint: _hGeoMidpoint);

        var relSpec = new RelativeOffsetSpec(
            masterLocalX: NominalLocalBlobOffsetX,
            masterLocalY: NominalLocalBlobOffsetY,
            masterAngleRad: 0.0,
            mmPerPixel: mmPerPixel);

        var uvwStage = UvwStageInverseKinematics.CreateStandardStage(stageRadiusMm: StandardUvwStageRadiusMm);

        var relNode = new RelativeOffsetCombinerNode(
            nodeId: NodeIdRelativeOffset,
            nodeName: "Rel_LocalOffsetUvw",
            spec: in relSpec,
            uvwKinematics: uvwStage,
            inReferencePose: _hPatPose,
            inTargetPoint: _hBlobCenter,
            outValid: _hRelValid,
            outLocalDeltaX: _hRelLocalDxMm,
            outLocalDeltaY: _hRelLocalDyMm,
            outDeltaThetaDeg: _hRelDeltaThetaDeg,
            outTotalOffsetMagnitude: _hRelTotalOffsetMm,
            outStrokeU: _hStrokeU,
            outStrokeV: _hStrokeV,
            outStrokeW: _hStrokeW);

        var distLimit = new ScalarSpecLimit(distanceLslMm, distanceUslMm, MissingPolicy.ForceNg);
        var distJudge = new ScalarSpecJudgeNode(
            nodeId: NodeIdDistanceJudge,
            nodeName: "Spec_PitchDistanceMm",
            specLimit: in distLimit,
            inPrimaryValue: _hGeoDistMm,
            outIsOk: _hDistOk,
            outEvaluatedValue: _hDistEval);

        var areaLimit = new ScalarSpecLimit(blobAreaLslPx, blobAreaUslPx, MissingPolicy.ForceNg);
        var areaJudge = new ScalarSpecJudgeNode(
            nodeId: NodeIdAreaJudge,
            nodeName: "Spec_SolderBlobAreaPx",
            specLimit: in areaLimit,
            inPrimaryValue: _hBlobArea,
            outIsOk: _hAreaOk,
            outEvaluatedValue: _hAreaEval);

        var offsetLimit = new ScalarSpecLimit(0.0, maxRelativeOffsetMm, MissingPolicy.ForceNg);
        var offsetJudge = new ScalarSpecJudgeNode(
            nodeId: NodeIdOffsetJudge,
            nodeName: "Spec_RelativeOffsetMm",
            specLimit: in offsetLimit,
            inPrimaryValue: _hRelTotalOffsetMm,
            outIsOk: _hOffsetOk,
            outEvaluatedValue: _hOffsetEval);

        VerdictGateBinding[] gates =
        [
            new VerdictGateBinding(NodeIdPatMax, "Gate_PatMaxFound", _hPatFound),
            new VerdictGateBinding(NodeIdLargestBlob, "Gate_BlobFound", _hBlobFound),
            new VerdictGateBinding(NodeIdDistanceJudge, "Gate_PitchDistanceOk", _hDistOk),
            new VerdictGateBinding(NodeIdAreaJudge, "Gate_BlobAreaOk", _hAreaOk),
            new VerdictGateBinding(NodeIdOffsetJudge, "Gate_RelativeOffsetOk", _hOffsetOk)
        ];

        var finalVerdictNode = new FinalVerdictAggregatorNode(
            nodeId: NodeIdFinalVerdict,
            nodeName: "FinalVerdict_And_UvwControl",
            logicGate: VerdictLogicGate.AllAnd,
            gateBindings: gates,
            outFinalOk: _hFinalOk,
            inPrimaryMetric: _hGeoDistMm,
            inSecondaryMetric: _hBlobArea,
            inAlignDeltaX: _hRelLocalDxMm,
            inAlignDeltaY: _hRelLocalDyMm,
            inAlignDeltaThetaDeg: _hRelDeltaThetaDeg,
            inStrokeU: _hStrokeU,
            inStrokeV: _hStrokeV,
            inStrokeW: _hStrokeW);

        // Register nodes intentionally out of topological order to verify Kahn's DAG compiler sorting
        builder
            .AddNode(finalVerdictNode)
            .AddNode(distJudge)
            .AddNode(areaJudge)
            .AddNode(offsetJudge)
            .AddNode(geoNode)
            .AddNode(relNode)
            .AddNode(blobNode)
            .AddNode(patMaxNode);

        // Register explicit data links for topological verification & UI visualization
        builder
            .AddLink(NodeIdPatMax, PatMaxToolNode.PortOutPose, NodeIdLargestBlob, LargestBlobToolNode.PortInFixture, _hPatPose)
            .AddLink(NodeIdPatMax, PatMaxToolNode.PortOutCenter, NodeIdGeometryCombiner, GeometryCombinerNode.PortInPointA, _hPatCenter)
            .AddLink(NodeIdLargestBlob, LargestBlobToolNode.PortOutMaxBlobCenter, NodeIdGeometryCombiner, GeometryCombinerNode.PortInPointB, _hBlobCenter)
            .AddLink(NodeIdPatMax, PatMaxToolNode.PortOutPose, NodeIdRelativeOffset, RelativeOffsetCombinerNode.PortInReferencePose, _hPatPose)
            .AddLink(NodeIdLargestBlob, LargestBlobToolNode.PortOutMaxBlobCenter, NodeIdRelativeOffset, RelativeOffsetCombinerNode.PortInTargetPoint, _hBlobCenter)
            .AddLink(NodeIdGeometryCombiner, GeometryCombinerNode.PortOutDistance, NodeIdDistanceJudge, ScalarSpecJudgeNode.PortInPrimaryValue, _hGeoDistMm)
            .AddLink(NodeIdLargestBlob, LargestBlobToolNode.PortOutMaxBlobArea, NodeIdAreaJudge, ScalarSpecJudgeNode.PortInPrimaryValue, _hBlobArea)
            .AddLink(NodeIdRelativeOffset, RelativeOffsetCombinerNode.PortOutTotalOffsetMagnitude, NodeIdOffsetJudge, ScalarSpecJudgeNode.PortInPrimaryValue, _hRelTotalOffsetMm);

        _context = builder.CreateContext();
        _runner = builder.BuildPlan();
    }

    /// <summary>Executes the compiled ToolBlock DAG on <paramref name="image"/> and returns the detailed snapshot.</summary>
    public ToolBlockScenarioResult Execute(ReadOnlyImageView2D<byte> image)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ToolBlockInspectionResult verdict = _runner.Execute(image, _workspace, _context);

        return new ToolBlockScenarioResult(
            PatMaxFound: _context.ReadBoolSlot(_hPatFound).Value,
            PatMaxPose: _context.ReadPoseSlot(_hPatPose).Value,
            PatMaxCenter: _context.ReadPointSlot(_hPatCenter).Value,
            PatMaxAngleDeg: _context.ReadScalarSlot(_hPatAngleDeg).Value,
            PatMaxScore: _context.ReadScalarSlot(_hPatScore).Value,
            BlobFound: _context.ReadBoolSlot(_hBlobFound).Value,
            BlobCount: _context.ReadScalarSlot(_hBlobCount).Value,
            BlobWorldCenter: _context.ReadPointSlot(_hBlobCenter).Value,
            BlobAreaPixels: _context.ReadScalarSlot(_hBlobArea).Value,
            BlobAngleDeg: _context.ReadScalarSlot(_hBlobAngleDeg).Value,
            BlobEccentricity: _context.ReadScalarSlot(_hBlobEccentricity).Value,
            MeasuredDistanceMm: _context.ReadScalarSlot(_hGeoDistMm).Value,
            MeasuredSlopeAngleDeg: _context.ReadScalarSlot(_hGeoAngleDeg).Value,
            MidpointWorld: _context.ReadPointSlot(_hGeoMidpoint).Value,
            LocalDeltaXMm: _context.ReadScalarSlot(_hRelLocalDxMm).Value,
            LocalDeltaYMm: _context.ReadScalarSlot(_hRelLocalDyMm).Value,
            RelativeDeltaThetaDeg: _context.ReadScalarSlot(_hRelDeltaThetaDeg).Value,
            RelativeOffsetMagnitudeMm: _context.ReadScalarSlot(_hRelTotalOffsetMm).Value,
            ActuatorStrokeUMm: _context.ReadScalarSlot(_hStrokeU).Value,
            ActuatorStrokeVMm: _context.ReadScalarSlot(_hStrokeV).Value,
            ActuatorStrokeWMm: _context.ReadScalarSlot(_hStrokeW).Value,
            IsDistanceSpecOk: _context.ReadBoolSlot(_hDistOk).Value,
            IsBlobAreaSpecOk: _context.ReadBoolSlot(_hAreaOk).Value,
            IsOffsetSpecOk: _context.ReadBoolSlot(_hOffsetOk).Value,
            FinalVerdict: verdict);
    }

    /// <summary>
    /// Renders a synthetic 8-bit inspection image containing the L-shaped fiducial pattern at <c>(partCenterX, partCenterY, partAngleRad)</c>
    /// and a circular solder pad blob at local offset <c>(NominalLocalBlobOffsetX + blobShiftLocalX, NominalLocalBlobOffsetY + blobShiftLocalY)</c>
    /// with radius <paramref name="blobRadiusPixels"/>.
    /// </summary>
    public static void RenderSyntheticInspectionImage(
        Span<byte> destination,
        int width,
        int height,
        float partCenterX,
        float partCenterY,
        float partAngleRad,
        float blobShiftLocalX = 0f,
        float blobShiftLocalY = 0f,
        float blobRadiusPixels = NominalBlobRadiusPixels,
        bool omitPartFiducial = false,
        bool omitBlob = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, checked(width * height));
        destination.Slice(0, width * height).Fill(BackgroundIntensity);

        FixturePose2D partPose = new(partCenterX, partCenterY, partAngleRad, 1f);

        if (!omitPartFiducial)
        {
            RenderFiducialAtPose(destination, width, height, in partPose);
        }

        if (!omitBlob && blobRadiusPixels > 1.0f)
        {
            System.Numerics.Vector2 blobWorld = partPose.LocalToWorld(
                NominalLocalBlobOffsetX + blobShiftLocalX,
                NominalLocalBlobOffsetY + blobShiftLocalY);
            RenderSoftDisk(destination, width, height, blobWorld.X, blobWorld.Y, blobRadiusPixels, BlobForegroundIntensity);
        }
    }

    private static PatMaxModel TrainCrossSquareFiducialModel(int templateSize)
    {
        byte[] templateBytes = new byte[templateSize * templateSize];
        templateBytes.AsSpan().Fill(BackgroundIntensity);

        FixturePose2D centerPose = new(templateSize * 0.5f, templateSize * 0.5f, 0f, 1f);
        RenderFiducialAtPose(templateBytes, templateSize, templateSize, in centerPose);

        PatMaxTrainParams trainParams = PatMaxTrainParams.Create(
            coarseGranularity: 2,
            fineGranularity: 1,
            edgeThreshold: 15.0f,
            elasticityPixels: 2.0f,
            minFeatureCount: 8,
            maxConditionNumber: 40.0f);

        return PatMaxTrainer.Train(
            templateBytes,
            templateSize,
            templateSize,
            templateSize,
            in trainParams,
            templateSize * 0.5f,
            templateSize * 0.5f);
    }

    private static void RenderFiducialAtPose(
        Span<byte> image,
        int width,
        int height,
        in FixturePose2D pose)
    {
        for (int r = 0; r < height; r++)
        {
            float wy = r + 0.5f;
            int rowOffset = r * width;
            for (int c = 0; c < width; c++)
            {
                float wx = c + 0.5f;
                System.Numerics.Vector2 local = pose.WorldToLocal(wx, wy);
                float u = local.X;
                float v = local.Y;

                // Asymmetric L-cross + corner notch fiducial for unambiguous 360-degree orientation
                bool inHorizontalBar = MathF.Abs(u) <= FiducialCrossHalfExtent && MathF.Abs(v) <= FiducialBarHalfThickness;
                bool inVerticalBar = MathF.Abs(u) <= FiducialBarHalfThickness && MathF.Abs(v) <= FiducialCrossHalfExtent;
                bool inTopRightCornerBlock = u >= FiducialCornerNotchMin && u <= FiducialCornerNotchMax
                    && v >= -FiducialCornerNotchMax && v <= -FiducialCornerNotchMin;

                if (inHorizontalBar || inVerticalBar || inTopRightCornerBlock)
                {
                    image[rowOffset + c] = FiducialBrightIntensity;
                }
            }
        }
    }

    private static void RenderSoftDisk(
        Span<byte> image,
        int width,
        int height,
        float centerX,
        float centerY,
        float radius,
        byte intensity)
    {
        int minX = Math.Clamp((int)MathF.Floor(centerX - radius - SoftDiskAAWindowRadius), 0, width - 1);
        int maxX = Math.Clamp((int)MathF.Ceiling(centerX + radius + SoftDiskAAWindowRadius), 0, width - 1);
        int minY = Math.Clamp((int)MathF.Floor(centerY - radius - SoftDiskAAWindowRadius), 0, height - 1);
        int maxY = Math.Clamp((int)MathF.Ceiling(centerY + radius + SoftDiskAAWindowRadius), 0, height - 1);

        for (int r = minY; r <= maxY; r++)
        {
            float dy = (r + 0.5f) - centerY;
            int rowOffset = r * width;
            for (int c = minX; c <= maxX; c++)
            {
                float dx = (c + 0.5f) - centerX;
                float dist = MathF.Sqrt((dx * dx) + (dy * dy));
                if (dist <= radius - 0.5f)
                {
                    image[rowOffset + c] = intensity;
                }
                else if (dist <= radius + 0.5f)
                {
                    float t = (radius + 0.5f) - dist;
                    byte cur = image[rowOffset + c];
                    int blended = (int)MathF.Round(cur + ((intensity - cur) * t));
                    image[rowOffset + c] = (byte)Math.Clamp(blended, 0, byte.MaxValue);
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _patMaxEngine.Dispose();
        _patMaxModel.Dispose();
        _workspace.Dispose();
    }
}
