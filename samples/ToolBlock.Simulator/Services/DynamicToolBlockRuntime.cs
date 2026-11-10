// File: samples/ToolBlock.Simulator/Services/DynamicToolBlockRuntime.cs
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Numerics;
using ToolBlock.Simulator.Models;
using VisionAlgorithm.OpenSrc.BlobRle;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.CornerAlignment.Kinematics;
using VisionAlgorithm.OpenSrc.GoldenVariation;
using VisionAlgorithm.OpenSrc.Metrology;
using VisionAlgorithm.OpenSrc.PatMax;
using VisionAlgorithm.OpenSrc.ToolBlock;
using VisionCanvas.Core.Editing;
using VisionCanvas.Rendering;
using VisionGeometry;

namespace ToolBlock.Simulator.Services;

/// <summary>
/// Cold-path DAG compiler and zero-allocation hot-path execution bridge for the proprietary
/// ImageJ + ToolBlock Algorithm Combination Simulator.
/// Connects <c>VisionCanvas</c> (<see cref="DocumentState"/>) with <see cref="ToolBlockRunner"/>,
/// integrates per-ROI ImageJ spatial filters (<see cref="RoiPreFilterKind"/>) and auto-thresholds (<see cref="ImageJAutoThresholdMethod"/>),
/// builds <see cref="ResultOverlay"/> graphics, and exports standardized <see cref="VisionInspectionEnvelope"/> records.
/// </summary>
public sealed class DynamicToolBlockRuntime : IDisposable
{
    private const double RadiansToDegrees = 180.0 / Math.PI;
    private const double DegreesToRadians = Math.PI / 180.0;
    private const int MinCropDimension = 16;
    private const int CirclePolygonSegments = 36;
    private const int DefaultWorkspaceBytes = 4 * 1024 * 1024;

    private static readonly Vector4 ColorPass = new(0.20f, 0.83f, 0.60f, 1.0f);
    private static readonly Vector4 ColorFail = new(0.97f, 0.44f, 0.44f, 1.0f);
    private static readonly Vector4 ColorFixture = new(0.22f, 0.74f, 0.97f, 1.0f);
    private static readonly Vector4 ColorCombiner = new(0.98f, 0.75f, 0.14f, 1.0f);

    private readonly VisionWorkspace _workspace = new(DefaultWorkspaceBytes);
    private readonly InspectionWorkspace _particleWorkspace = new();
    private readonly PatMaxEngine _patMaxEngine = new();
    private bool _disposed;

    public DynamicToolBlockSnapshot CompileAndExecute(
        ReadOnlyImageView2D<byte> masterImage,
        ReadOnlyImageView2D<byte> runtimeImage,
        DocumentState canvasState,
        IReadOnlyList<EditableToolNodeSpec> nodeSpecs,
        IReadOnlyList<EditablePortLink> links)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(canvasState);
        ArgumentNullException.ThrowIfNull(nodeSpecs);
        ArgumentNullException.ThrowIfNull(links);

        if (nodeSpecs.Count == 0)
        {
            var idleOverlay = new ResultOverlay(
                Summary: "[ImageJ + ToolBlock] 대기 중 (0 nodes) · 이미지를 열고 ROI를 그린 뒤 좌측에서 알고리즘을 선택해 추가하세요.",
                Color: ColorFixture,
                SearchBoundary: [],
                Matches: [],
                ShowFeatures: true);

            return new DynamicToolBlockSnapshot(
                ToolBlockCompileStatus.Success,
                "대기 중 (0 nodes) · 좌측 팔레트에서 원하는 알고리즘을 선택해 추가하세요.",
                new ToolBlockInspectionResult(FinalDecision.Ok, 0, 0, 0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, HasValidAlignment: false),
                0.0,
                idleOverlay,
                [],
                [],
                [],
                [],
                [],
                [],
                [],
                0.0,
                0.0,
                0.0,
                0.0);
        }

        var builder = new ToolBlockBuilder();
        var specById = new Dictionary<int, EditableToolNodeSpec>(nodeSpecs.Count);
        var masterPosesByNodeId = new Dictionary<int, FixturePose2D>(nodeSpecs.Count);
        var worldRoiRectByNodeId = new Dictionary<int, (int X, int Y, int Width, int Height)>(nodeSpecs.Count);
        var localPoseByNodeId = new Dictionary<int, FixturePose2D>(nodeSpecs.Count);

        // Pass 1: Resolve Canvas ROI rectangles & Master Reference Poses for Fixturing nodes
        for (int i = 0; i < nodeSpecs.Count; i++)
        {
            EditableToolNodeSpec spec = nodeSpecs[i];
            specById[spec.NodeId] = spec;

            (int rx, int ry, int rw, int rh) = ResolveCanvasRoiRect(canvasState, spec.BoundRoiId, runtimeImage.Width, runtimeImage.Height, i);
            worldRoiRectByNodeId[spec.NodeId] = (rx, ry, rw, rh);

            if (spec.Kind == VisionToolKind.ShapePatternMatch)
            {
                float cx = rx + (rw * 0.5f);
                float cy = ry + (rh * 0.5f);
                masterPosesByNodeId[spec.NodeId] = new FixturePose2D(cx, cy, 0.0f, 1.0f);
            }
            else if (spec.Kind == VisionToolKind.DualRakeCornerAlign)
            {
                CornerAlignmentToolSpec cornerSpec = CornerAlignmentToolSpec.CreateFromRoiBounds(
                    rx,
                    ry,
                    rw,
                    rh,
                    spec.CornerSearchLengthPx,
                    spec.CornerCaliperCount,
                    spec.CornerApexIgnoreMarginPx);
                masterPosesByNodeId[spec.NodeId] = new FixturePose2D(
                    cornerSpec.ExpectedApexWorld.X,
                    cornerSpec.ExpectedApexWorld.Y,
                    0.0f,
                    1.0f);
            }
        }

        // Pass 2: Allocate all output slots for every node upfront so input links can reference them
        var poseOutSlots = new Dictionary<(int NodeId, string Port), TerminalHandle<FixturePose2D>>();
        var pointOutSlots = new Dictionary<(int NodeId, string Port), TerminalHandle<Point2D>>();
        var lineOutSlots = new Dictionary<(int NodeId, string Port), TerminalHandle<Line2D>>();
        var circleOutSlots = new Dictionary<(int NodeId, string Port), TerminalHandle<Circle2D>>();
        var scalarOutSlots = new Dictionary<(int NodeId, string Port), TerminalHandle<double>>();
        var boolOutSlots = new Dictionary<(int NodeId, string Port), TerminalHandle<bool>>();

        for (int i = 0; i < nodeSpecs.Count; i++)
        {
            EditableToolNodeSpec spec = nodeSpecs[i];
            int nid = spec.NodeId;
            switch (spec.Kind)
            {
                case VisionToolKind.ShapePatternMatch:
                    boolOutSlots[(nid, PatMaxToolNode.PortOutFound)] = builder.AllocateBoolSlot();
                    poseOutSlots[(nid, PatMaxToolNode.PortOutPose)] = builder.AllocatePoseSlot();
                    pointOutSlots[(nid, PatMaxToolNode.PortOutCenter)] = builder.AllocatePointSlot();
                    scalarOutSlots[(nid, PatMaxToolNode.PortOutAngleDeg)] = builder.AllocateScalarSlot();
                    scalarOutSlots[(nid, PatMaxToolNode.PortOutScore)] = builder.AllocateScalarSlot();
                    scalarOutSlots[(nid, PatMaxToolNode.PortOutMatchedTemplateIndex)] = builder.AllocateScalarSlot();
                    break;

                case VisionToolKind.DualRakeCornerAlign:
                    boolOutSlots[(nid, CornerAlignmentToolNode.PortOutFound)] = builder.AllocateBoolSlot();
                    poseOutSlots[(nid, CornerAlignmentToolNode.PortOutPose)] = builder.AllocatePoseSlot();
                    pointOutSlots[(nid, CornerAlignmentToolNode.PortOutCorner)] = builder.AllocatePointSlot();
                    lineOutSlots[(nid, CornerAlignmentToolNode.PortOutLine1)] = builder.AllocateLineSlot();
                    lineOutSlots[(nid, CornerAlignmentToolNode.PortOutLine2)] = builder.AllocateLineSlot();
                    scalarOutSlots[(nid, CornerAlignmentToolNode.PortOutIncludedAngleDeg)] = builder.AllocateScalarSlot();
                    break;

                case VisionToolKind.RleBlobAnalyzer:
                    boolOutSlots[(nid, LargestBlobToolNode.PortOutFound)] = builder.AllocateBoolSlot();
                    scalarOutSlots[(nid, LargestBlobToolNode.PortOutBlobCount)] = builder.AllocateScalarSlot();
                    pointOutSlots[(nid, LargestBlobToolNode.PortOutMaxBlobCenter)] = builder.AllocatePointSlot();
                    scalarOutSlots[(nid, LargestBlobToolNode.PortOutMaxBlobArea)] = builder.AllocateScalarSlot();
                    scalarOutSlots[(nid, LargestBlobToolNode.PortOutMaxBlobAngleDeg)] = builder.AllocateScalarSlot();
                    scalarOutSlots[(nid, LargestBlobToolNode.PortOutEccentricity)] = builder.AllocateScalarSlot();
                    break;

                case VisionToolKind.BinaryPixelCounter:
                    boolOutSlots[(nid, BinaryPixelCountToolNode.PortOutFound)] = builder.AllocateBoolSlot();
                    boolOutSlots[(nid, BinaryPixelCountToolNode.PortOutIsPass)] = builder.AllocateBoolSlot();
                    pointOutSlots[(nid, BinaryPixelCountToolNode.PortOutCenterOfMass)] = builder.AllocatePointSlot();
                    scalarOutSlots[(nid, BinaryPixelCountToolNode.PortOutValidPixelCount)] = builder.AllocateScalarSlot();
                    scalarOutSlots[(nid, BinaryPixelCountToolNode.PortOutAreaRatioPercent)] = builder.AllocateScalarSlot();
                    scalarOutSlots[(nid, BinaryPixelCountToolNode.PortOutMeanGray)] = builder.AllocateScalarSlot();
                    break;

                case VisionToolKind.SubpixelCaliperMetrology:
                    boolOutSlots[(nid, CaliperLineCircleToolNode.PortOutFound)] = builder.AllocateBoolSlot();
                    pointOutSlots[(nid, CaliperLineCircleToolNode.PortOutEdgeOrCenter)] = builder.AllocatePointSlot();
                    lineOutSlots[(nid, CaliperLineCircleToolNode.PortOutFittedLine)] = builder.AllocateLineSlot();
                    circleOutSlots[(nid, CaliperLineCircleToolNode.PortOutFittedCircle)] = builder.AllocateCircleSlot();
                    scalarOutSlots[(nid, CaliperLineCircleToolNode.PortOutMeasuredWidthOrRadius)] = builder.AllocateScalarSlot();
                    break;

                case VisionToolKind.GoldenSurfaceInspector:
                    boolOutSlots[(nid, VariationModelToolNode.PortOutIsDefectFree)] = builder.AllocateBoolSlot();
                    scalarOutSlots[(nid, VariationModelToolNode.PortOutDefectPixelCount)] = builder.AllocateScalarSlot();
                    scalarOutSlots[(nid, VariationModelToolNode.PortOutMaxExcessDiff)] = builder.AllocateScalarSlot();
                    scalarOutSlots[(nid, VariationModelToolNode.PortOutMaxZScore)] = builder.AllocateScalarSlot();
                    break;

                case VisionToolKind.GeometryMathCombiner:
                    boolOutSlots[(nid, GeometryCombinerNode.PortOutValid)] = builder.AllocateBoolSlot();
                    scalarOutSlots[(nid, GeometryCombinerNode.PortOutDistance)] = builder.AllocateScalarSlot();
                    scalarOutSlots[(nid, GeometryCombinerNode.PortOutDeltaX)] = builder.AllocateScalarSlot();
                    scalarOutSlots[(nid, GeometryCombinerNode.PortOutDeltaY)] = builder.AllocateScalarSlot();
                    scalarOutSlots[(nid, GeometryCombinerNode.PortOutAngleDeg)] = builder.AllocateScalarSlot();
                    pointOutSlots[(nid, GeometryCombinerNode.PortOutPoint)] = builder.AllocatePointSlot();
                    break;

                case VisionToolKind.RelativeOffsetKinematics:
                    boolOutSlots[(nid, RelativeOffsetCombinerNode.PortOutValid)] = builder.AllocateBoolSlot();
                    scalarOutSlots[(nid, RelativeOffsetCombinerNode.PortOutLocalDeltaX)] = builder.AllocateScalarSlot();
                    scalarOutSlots[(nid, RelativeOffsetCombinerNode.PortOutLocalDeltaY)] = builder.AllocateScalarSlot();
                    scalarOutSlots[(nid, RelativeOffsetCombinerNode.PortOutDeltaThetaDeg)] = builder.AllocateScalarSlot();
                    scalarOutSlots[(nid, RelativeOffsetCombinerNode.PortOutTotalOffsetMagnitude)] = builder.AllocateScalarSlot();
                    scalarOutSlots[(nid, RelativeOffsetCombinerNode.PortOutStrokeU)] = builder.AllocateScalarSlot();
                    scalarOutSlots[(nid, RelativeOffsetCombinerNode.PortOutStrokeV)] = builder.AllocateScalarSlot();
                    scalarOutSlots[(nid, RelativeOffsetCombinerNode.PortOutStrokeW)] = builder.AllocateScalarSlot();
                    break;

                case VisionToolKind.ToleranceSpecJudge:
                    boolOutSlots[(nid, ScalarSpecJudgeNode.PortOutIsOk)] = builder.AllocateBoolSlot();
                    scalarOutSlots[(nid, ScalarSpecJudgeNode.PortOutEvaluatedValue)] = builder.AllocateScalarSlot();
                    break;

                case VisionToolKind.FinalVerdictAggregator:
                    boolOutSlots[(nid, FinalVerdictAggregatorNode.PortOutFinalOk)] = builder.AllocateBoolSlot();
                    break;
            }
        }

        // Pass 3: Instantiate concrete IToolBlockNode implementations and register links
        var concreteNodes = new Dictionary<int, IToolBlockNode>(nodeSpecs.Count);
        var disposableModels = new List<IDisposable>(4);
        var validLinks = new List<EditablePortLink>(links.Count);

        try
        {
            for (int i = 0; i < nodeSpecs.Count; i++)
            {
                EditableToolNodeSpec spec = nodeSpecs[i];
                (int rx, int ry, int rw, int rh) = worldRoiRectByNodeId[spec.NodeId];
                FixturePose2D refPose = FindLinkedFixtureMasterPose(spec.NodeId, links, masterPosesByNodeId);
                FixturePose2D localOffsetPose = ComputeLocalOffsetPose(rx, ry, rw, rh, refPose);
                localPoseByNodeId[spec.NodeId] = localOffsetPose;

                IToolBlockNode node = CreateConcreteNode(
                    spec,
                    masterImage,
                    runtimeImage,
                    (rx, ry, rw, rh),
                    localOffsetPose,
                    links,
                    poseOutSlots,
                    pointOutSlots,
                    lineOutSlots,
                    circleOutSlots,
                    scalarOutSlots,
                    boolOutSlots,
                    disposableModels);

                concreteNodes[spec.NodeId] = node;
                builder.AddNode(node);
            }

            // Register explicit links on builder
            for (int i = 0; i < links.Count; i++)
            {
                EditablePortLink link = links[i];
                if (!concreteNodes.ContainsKey(link.SourceNodeId) || !concreteNodes.ContainsKey(link.TargetNodeId))
                {
                    continue;
                }

                if (TryRegisterBuilderLink(
                    builder,
                    link,
                    poseOutSlots,
                    pointOutSlots,
                    lineOutSlots,
                    circleOutSlots,
                    scalarOutSlots,
                    boolOutSlots))
                {
                    validLinks.Add(link);
                }
            }

            (List<TerminalPortOption> outPorts, List<TerminalPortOption> inPorts) =
                CollectPortOptions(nodeSpecs, concreteNodes);

            if (!builder.TryBuildPlan(out ToolBlockRunner? runner, out ToolBlockCompileStatus status, out string errorMsg) || runner is null)
            {
                var errOverlay = new ResultOverlay(
                    Summary: $"[ToolBlock 컴파일 오류] {status}: {errorMsg}",
                    Color: ColorFail,
                    SearchBoundary: [],
                    Matches: [],
                    ShowFeatures: true);

                return new DynamicToolBlockSnapshot(
                    status,
                    $"{status}: {errorMsg}",
                    default,
                    0.0,
                    errOverlay,
                    [],
                    [],
                    [],
                    outPorts,
                    inPorts,
                    [],
                    [],
                    0.0,
                    0.0,
                    0.0,
                    0.0);
            }

            ToolBlockContext context = builder.CreateContext();

            // Pass 4: Execute Hot Path (0-byte allocation)
            long startStamp = Stopwatch.GetTimestamp();
            ToolBlockInspectionResult verdict = runner.Execute(runtimeImage, _workspace, context);
            long elapsedTicks = Stopwatch.GetTimestamp() - startStamp;
            double elapsedUs = elapsedTicks * 1_000_000.0 / Stopwatch.Frequency;

            double strokeU = verdict.ActuatorStrokeU;
            double strokeV = verdict.ActuatorStrokeV;
            double strokeW = verdict.ActuatorStrokeW;

            // Pass 5: Build VisionCanvas ResultOverlay, DAG items, Link rows, Slot items, and Standardized Envelopes
            ResultOverlay overlay = BuildCanvasOverlay(
                runner,
                context,
                specById,
                worldRoiRectByNodeId,
                localPoseByNodeId,
                verdict,
                elapsedUs);

            IReadOnlyList<DagNodeDisplayItem> dagItems = BuildOrderedDagItems(
                runner,
                context,
                specById);

            IReadOnlyList<TerminalLinkRowItem> linkRows = BuildLinkRows(
                validLinks,
                concreteNodes,
                context,
                specById);

            IReadOnlyList<TerminalSlotDisplayItem> slotItems = BuildSlotDisplayItems(
                runner,
                context,
                validLinks,
                specById);

            (List<AlgorithmResultSummaryRow> summaryRows, List<VisionInspectionEnvelope> envelopes) =
                CollectStandardizedEnvelopesAndRows(runner, context, specById);

            return new DynamicToolBlockSnapshot(
                status,
                $"DAG Compiled OK ({runner.NodeCount} nodes, {validLinks.Count} links · Topological Sort Ready)",
                verdict,
                elapsedUs,
                overlay,
                dagItems,
                linkRows,
                slotItems,
                outPorts,
                inPorts,
                summaryRows,
                envelopes,
                strokeU,
                strokeU,
                strokeV,
                strokeW);
        }
        finally
        {
            for (int i = 0; i < disposableModels.Count; i++)
            {
                disposableModels[i].Dispose();
            }
        }
    }

    /// <summary>
    /// Executes ImageJ-style "Analyze Particles &amp; Shape Descriptors" on the specified ROI
    /// and returns a row per connected component sorted by descending area.
    /// </summary>
    public IReadOnlyList<ImageJParticleRowItem> AnalyzeParticlesInRoi(
        ReadOnlyImageView2D<byte> image,
        int rx,
        int ry,
        int rw,
        int rh,
        RoiPreFilterKind preFilter,
        byte minThreshold,
        byte maxThreshold,
        int minPassAreaPixels)
    {
        if (!image.IsValid || rw <= 0 || rh <= 0)
        {
            return [];
        }

        (int cx, int cy, int cw, int ch) = ClampRect(rx, ry, rw, rh, image.Width, image.Height);
        ReadOnlyImageView2D<byte> activeImage = RoiImageProcessor.ApplyRoiFilterIfNeeded(image, cx, cy, cw, ch, preFilter);
        byte[] crop = ExtractCropBytes(activeImage, cx, cy, cw, ch);

        byte lowT = Math.Min(minThreshold, maxThreshold);
        byte highT = Math.Max(minThreshold, maxThreshold);
        RleRegion region = RleRegion.Threshold(crop, cw, ch, cw, lowT, highT);
        if (region.IsEmpty)
        {
            return [];
        }

        List<BlobComponentFeature> components = RleBlobAnalyzer.AnalyzeComponents(region, minimumAreaPixels: 1, _particleWorkspace);
        if (components.Count == 0)
        {
            return [];
        }

        components.Sort(static (a, b) => b.AreaPixels.CompareTo(a.AreaPixels));

        var rows = new List<ImageJParticleRowItem>(components.Count);
        for (int i = 0; i < components.Count; i++)
        {
            BlobComponentFeature comp = components[i];
            float worldX = cx + comp.Centroid.X;
            float worldY = cy + comp.Centroid.Y;
            int bw = Math.Max(1, comp.BoundingWidth);
            int bh = Math.Max(1, comp.BoundingHeight);
            double aspect = (double)Math.Max(bw, bh) / Math.Max(1, Math.Min(bw, bh));
            double boxFill = Math.Clamp((double)comp.AreaPixels / (bw * bh), 0.0, 1.0);
            double circularity = Math.Clamp(boxFill * (4.0 / Math.PI) * (1.0 - (comp.Eccentricity * 0.35)), 0.05, 1.0);
            double angleDeg = comp.PrincipalAxisAngleRad * RadiansToDegrees;
            bool passesArea = comp.AreaPixels >= minPassAreaPixels;

            rows.Add(new ImageJParticleRowItem(
                ParticleIndex: i + 1,
                AreaPixels: (int)comp.AreaPixels,
                CentroidWorldText: $"({worldX:F1}, {worldY:F1})",
                BoundingBoxText: $"[{cx + comp.MinCol}, {cy + comp.MinRow}, {bw}×{bh}]",
                CircularityText: $"{circularity:F3}",
                AspectRatioText: $"{aspect:F2}",
                EccentricityText: $"{comp.Eccentricity:F3}",
                OrientationDegText: $"{angleDeg:+0.0;-0.0;0.0}°",
                FilterPassBadge: passesArea ? "PASS" : $"<{minPassAreaPixels}px²",
                BadgeHex: passesArea ? "#10B981" : "#64748B"));
        }

        return rows;
    }

    private IToolBlockNode CreateConcreteNode(
        EditableToolNodeSpec spec,
        ReadOnlyImageView2D<byte> masterImage,
        ReadOnlyImageView2D<byte> runtimeImage,
        (int X, int Y, int Width, int Height) worldRect,
        FixturePose2D localOffsetPose,
        IReadOnlyList<EditablePortLink> links,
        IReadOnlyDictionary<(int NodeId, string Port), TerminalHandle<FixturePose2D>> poseOutSlots,
        IReadOnlyDictionary<(int NodeId, string Port), TerminalHandle<Point2D>> pointOutSlots,
        IReadOnlyDictionary<(int NodeId, string Port), TerminalHandle<Line2D>> lineOutSlots,
        IReadOnlyDictionary<(int NodeId, string Port), TerminalHandle<Circle2D>> circleOutSlots,
        IReadOnlyDictionary<(int NodeId, string Port), TerminalHandle<double>> scalarOutSlots,
        IReadOnlyDictionary<(int NodeId, string Port), TerminalHandle<bool>> boolOutSlots,
        List<IDisposable> disposableModels)
    {
        ReadOnlyImageView2D<byte> filteredMaster = spec.RequiresCanvasRoi
            ? RoiImageProcessor.ApplyRoiFilterIfNeeded(masterImage, worldRect.X, worldRect.Y, worldRect.Width, worldRect.Height, spec.PreFilter)
            : masterImage;
        ReadOnlyImageView2D<byte> filteredRuntime = spec.RequiresCanvasRoi
            ? RoiImageProcessor.ApplyRoiFilterIfNeeded(runtimeImage, worldRect.X, worldRect.Y, worldRect.Width, worldRect.Height, spec.PreFilter)
            : runtimeImage;

        IToolBlockNode coreNode = CreateUnwrappedNode(
            spec,
            filteredMaster,
            filteredRuntime,
            worldRect,
            localOffsetPose,
            links,
            poseOutSlots,
            pointOutSlots,
            lineOutSlots,
            circleOutSlots,
            scalarOutSlots,
            boolOutSlots,
            disposableModels);

        if (spec.RequiresCanvasRoi && spec.PreFilter != RoiPreFilterKind.None)
        {
            return new PreFilteredToolNodeDecorator(
                coreNode,
                spec.PreFilter,
                runtimeImage.Width,
                runtimeImage.Height,
                runtimeImage.Stride,
                worldRect.X,
                worldRect.Y,
                worldRect.Width,
                worldRect.Height);
        }

        return coreNode;
    }

    private IToolBlockNode CreateUnwrappedNode(
        EditableToolNodeSpec spec,
        ReadOnlyImageView2D<byte> masterImage,
        ReadOnlyImageView2D<byte> runtimeImage,
        (int X, int Y, int Width, int Height) worldRect,
        FixturePose2D localOffsetPose,
        IReadOnlyList<EditablePortLink> links,
        IReadOnlyDictionary<(int NodeId, string Port), TerminalHandle<FixturePose2D>> poseOutSlots,
        IReadOnlyDictionary<(int NodeId, string Port), TerminalHandle<Point2D>> pointOutSlots,
        IReadOnlyDictionary<(int NodeId, string Port), TerminalHandle<Line2D>> lineOutSlots,
        IReadOnlyDictionary<(int NodeId, string Port), TerminalHandle<Circle2D>> circleOutSlots,
        IReadOnlyDictionary<(int NodeId, string Port), TerminalHandle<double>> scalarOutSlots,
        IReadOnlyDictionary<(int NodeId, string Port), TerminalHandle<bool>> boolOutSlots,
        List<IDisposable> disposableModels)
    {
        int nid = spec.NodeId;
        switch (spec.Kind)
        {
            case VisionToolKind.ShapePatternMatch:
            {
                (int mx, int my, int mw, int mh) = ClampRect(worldRect.X, worldRect.Y, worldRect.Width, worldRect.Height, masterImage.Width, masterImage.Height);
                byte[] crop = ExtractCropBytes(masterImage, mx, my, mw, mh);
                PatMaxTrainParams trainParams = PatMaxTrainParams.Create(
                    coarseGranularity: 2,
                    fineGranularity: 1,
                    edgeThreshold: 12.0f,
                    elasticityPixels: 2.0f,
                    minFeatureCount: 4,
                    maxConditionNumber: 60.0f);

                PatMaxModel model = PatMaxTrainer.Train(
                    crop,
                    mw,
                    mh,
                    mw,
                    in trainParams,
                    mw * 0.5f,
                    mh * 0.5f);
                disposableModels.Add(model);

                float radExtent = Math.Clamp(spec.PatMaxAngleRangeDeg, 5.0f, 180.0f) * (MathF.PI / 180.0f);
                PatMaxSearchParams searchParams = PatMaxSearchParams.Create(
                    acceptThreshold: Math.Clamp(spec.PatMaxMinScore, 0.15f, 0.98f),
                    angleStartRadians: -radExtent,
                    angleExtentRadians: radExtent * 2.0f,
                    scaleMin: 0.95f,
                    scaleMax: 1.05f,
                    runtimeEdgeThreshold: 10.0f,
                    ignorePolarity: false,
                    maxCandidates: 16,
                    maxResults: 1,
                    minSeparationPixels: 8.0f);

                return new PatMaxToolNode(
                    nid,
                    spec.Name,
                    _patMaxEngine,
                    model,
                    in searchParams,
                    boolOutSlots[(nid, PatMaxToolNode.PortOutFound)],
                    poseOutSlots[(nid, PatMaxToolNode.PortOutPose)],
                    pointOutSlots[(nid, PatMaxToolNode.PortOutCenter)],
                    scalarOutSlots[(nid, PatMaxToolNode.PortOutAngleDeg)],
                    scalarOutSlots[(nid, PatMaxToolNode.PortOutScore)],
                    scalarOutSlots[(nid, PatMaxToolNode.PortOutMatchedTemplateIndex)]);
            }

            case VisionToolKind.DualRakeCornerAlign:
            {
                CornerAlignmentToolSpec cornerSpec = CornerAlignmentToolSpec.CreateFromRoiBounds(
                    worldRect.X,
                    worldRect.Y,
                    worldRect.Width,
                    worldRect.Height,
                    spec.CornerSearchLengthPx,
                    spec.CornerCaliperCount,
                    spec.CornerApexIgnoreMarginPx);

                return new CornerAlignmentToolNode(
                    nid,
                    spec.Name,
                    in cornerSpec,
                    boolOutSlots[(nid, CornerAlignmentToolNode.PortOutFound)],
                    poseOutSlots[(nid, CornerAlignmentToolNode.PortOutPose)],
                    pointOutSlots[(nid, CornerAlignmentToolNode.PortOutCorner)],
                    lineOutSlots[(nid, CornerAlignmentToolNode.PortOutLine1)],
                    lineOutSlots[(nid, CornerAlignmentToolNode.PortOutLine2)],
                    scalarOutSlots[(nid, CornerAlignmentToolNode.PortOutIncludedAngleDeg)]);
            }

            case VisionToolKind.RleBlobAnalyzer:
            {
                TerminalHandle<FixturePose2D> inFixture = ResolveInputPoseHandle(nid, LargestBlobToolNode.PortInFixture, links, poseOutSlots);
                (byte minT, byte maxT) = ResolveEffectiveThresholdRange(
                    runtimeImage,
                    worldRect,
                    spec.AutoThreshold,
                    spec.InvertThresholdPolarity,
                    spec.BlobMinThreshold,
                    spec.BlobMaxThreshold);

                var blobSpec = new LargestBlobToolSpec(
                    localRoiOffset: localOffsetPose,
                    roiWidth: worldRect.Width,
                    roiHeight: worldRect.Height,
                    minIntensity: minT,
                    maxIntensity: maxT,
                    minimumAreaPixels: Math.Max(1, spec.BlobMinAreaPixels),
                    morphologyOperation: spec.BlobMorphology,
                    morphologyRadiusX: 1,
                    morphologyRadiusY: 1);

                return new LargestBlobToolNode(
                    nid,
                    spec.Name,
                    in blobSpec,
                    inFixture,
                    boolOutSlots[(nid, LargestBlobToolNode.PortOutFound)],
                    scalarOutSlots[(nid, LargestBlobToolNode.PortOutBlobCount)],
                    pointOutSlots[(nid, LargestBlobToolNode.PortOutMaxBlobCenter)],
                    scalarOutSlots[(nid, LargestBlobToolNode.PortOutMaxBlobArea)],
                    scalarOutSlots[(nid, LargestBlobToolNode.PortOutMaxBlobAngleDeg)],
                    scalarOutSlots[(nid, LargestBlobToolNode.PortOutEccentricity)]);
            }

            case VisionToolKind.BinaryPixelCounter:
            {
                TerminalHandle<FixturePose2D> inFixture = ResolveInputPoseHandle(nid, BinaryPixelCountToolNode.PortInFixture, links, poseOutSlots);
                (byte minT, byte maxT) = ResolveEffectiveThresholdRange(
                    runtimeImage,
                    worldRect,
                    spec.AutoThreshold,
                    spec.InvertThresholdPolarity,
                    spec.BinaryMinThreshold,
                    spec.BinaryMaxThreshold);

                var binSpec = new BinaryPixelCountSpec(
                    localRoiOffset: localOffsetPose,
                    roiWidth: worldRect.Width,
                    roiHeight: worldRect.Height,
                    minGrayThreshold: minT,
                    maxGrayThreshold: maxT,
                    minPassAreaPercent: spec.BinaryMinPassPercent,
                    maxPassAreaPercent: spec.BinaryMaxPassPercent,
                    conditionIndex: 0,
                    conditionId: nid);

                return new BinaryPixelCountToolNode(
                    nid,
                    spec.Name,
                    in binSpec,
                    inFixture,
                    boolOutSlots[(nid, BinaryPixelCountToolNode.PortOutFound)],
                    boolOutSlots[(nid, BinaryPixelCountToolNode.PortOutIsPass)],
                    pointOutSlots[(nid, BinaryPixelCountToolNode.PortOutCenterOfMass)],
                    scalarOutSlots[(nid, BinaryPixelCountToolNode.PortOutValidPixelCount)],
                    scalarOutSlots[(nid, BinaryPixelCountToolNode.PortOutAreaRatioPercent)],
                    scalarOutSlots[(nid, BinaryPixelCountToolNode.PortOutMeanGray)]);
            }

            case VisionToolKind.SubpixelCaliperMetrology:
            {
                TerminalHandle<FixturePose2D> inFixture = ResolveInputPoseHandle(nid, CaliperLineCircleToolNode.PortInFixture, links, poseOutSlots);
                var calMeasSpec = new CaliperMeasurementSpec(
                    gaussianSigma: Math.Clamp(spec.CaliperSigma, 0.5f, 6.0f),
                    minimumContrast: Math.Clamp(spec.CaliperMinContrast, 2.0f, 180.0f),
                    projectionStripCount: 9);

                var calSpec = new CaliperLineCircleToolSpec(
                    mode: spec.CaliperMode,
                    localRoiOffset: localOffsetPose,
                    scanLengthPixels: MathF.Max(16.0f, worldRect.Width * 0.45f),
                    projectionWidthPixels: 7.0f,
                    firstPolarity: CaliperEdgePolarity.Any,
                    secondPolarity: CaliperEdgePolarity.Any,
                    caliperSpec: calMeasSpec,
                    sampleCount: 16,
                    nominalRadiusOrSpanPixels: MathF.Max(6.0f, spec.CaliperNominalRadiusPx));

                return new CaliperLineCircleToolNode(
                    nid,
                    spec.Name,
                    in calSpec,
                    inFixture,
                    boolOutSlots[(nid, CaliperLineCircleToolNode.PortOutFound)],
                    pointOutSlots[(nid, CaliperLineCircleToolNode.PortOutEdgeOrCenter)],
                    lineOutSlots[(nid, CaliperLineCircleToolNode.PortOutFittedLine)],
                    circleOutSlots[(nid, CaliperLineCircleToolNode.PortOutFittedCircle)],
                    scalarOutSlots[(nid, CaliperLineCircleToolNode.PortOutMeasuredWidthOrRadius)]);
            }

            case VisionToolKind.GoldenSurfaceInspector:
            {
                TerminalHandle<FixturePose2D> inFixture = ResolveInputPoseHandle(nid, VariationModelToolNode.PortInFixture, links, poseOutSlots);
                (int mx, int my, int mw, int mh) = ClampRect(worldRect.X, worldRect.Y, worldRect.Width, worldRect.Height, masterImage.Width, masterImage.Height);
                var masterWorldPose = new FixturePose2D(
                    mx + (mw * 0.5f),
                    my + (mh * 0.5f),
                    0.0f,
                    1.0f);

                byte[] masterCopy = masterImage.Pixels.ToArray();
                VariationTrainingSample[] trainSamples =
                [
                    new VariationTrainingSample(masterCopy, masterImage.Width, masterImage.Height, masterImage.Stride, masterWorldPose)
                ];

                var trainSpec = new VariationTrainingSpec(
                    absoluteMinimumThreshold: Math.Max(4.0f, spec.VariationAbsThreshold),
                    sigmaMultiplier: Math.Max(1.0f, spec.VariationScale),
                    edgeJitterTolerancePixels: 0.75f);

                using var trainWs = new InspectionWorkspace();
                VariationModel trainedModel = VariationModelTrainer.Train(
                    trainSamples,
                    mw,
                    mh,
                    trainSpec,
                    trainWs);

                var varSpec = new VariationModelToolSpec(
                    localOffsetPose,
                    minimumDefectAreaPixels: Math.Max(1, spec.VariationMinDefectAreaPx));

                return new VariationModelToolNode(
                    nid,
                    spec.Name,
                    trainedModel,
                    in varSpec,
                    inFixture,
                    boolOutSlots[(nid, VariationModelToolNode.PortOutIsDefectFree)],
                    scalarOutSlots[(nid, VariationModelToolNode.PortOutDefectPixelCount)],
                    scalarOutSlots[(nid, VariationModelToolNode.PortOutMaxExcessDiff)],
                    scalarOutSlots[(nid, VariationModelToolNode.PortOutMaxZScore)]);
            }

            case VisionToolKind.GeometryMathCombiner:
            {
                TerminalHandle<Point2D> inPtA = ResolveInputPointHandle(nid, GeometryCombinerNode.PortInPointA, links, pointOutSlots);
                TerminalHandle<Point2D> inPtB = ResolveInputPointHandle(nid, GeometryCombinerNode.PortInPointB, links, pointOutSlots);
                TerminalHandle<Line2D> inLnA = ResolveInputLineHandle(nid, GeometryCombinerNode.PortInLineA, links, lineOutSlots);
                TerminalHandle<Line2D> inLnB = ResolveInputLineHandle(nid, GeometryCombinerNode.PortInLineB, links, lineOutSlots);

                return new GeometryCombinerNode(
                    nid,
                    spec.Name,
                    spec.GeometryOperation,
                    spec.GeometryMmPerPixel,
                    homography: null,
                    inPtA,
                    inPtB,
                    inLnA,
                    inLnB,
                    boolOutSlots[(nid, GeometryCombinerNode.PortOutValid)],
                    scalarOutSlots[(nid, GeometryCombinerNode.PortOutDistance)],
                    scalarOutSlots[(nid, GeometryCombinerNode.PortOutDeltaX)],
                    scalarOutSlots[(nid, GeometryCombinerNode.PortOutDeltaY)],
                    scalarOutSlots[(nid, GeometryCombinerNode.PortOutAngleDeg)],
                    pointOutSlots[(nid, GeometryCombinerNode.PortOutPoint)]);
            }

            case VisionToolKind.RelativeOffsetKinematics:
            {
                TerminalHandle<FixturePose2D> inRefPose = ResolveInputPoseHandle(nid, RelativeOffsetCombinerNode.PortInReferencePose, links, poseOutSlots);
                TerminalHandle<Point2D> inTargetPt = ResolveInputPointHandle(nid, RelativeOffsetCombinerNode.PortInTargetPoint, links, pointOutSlots);

                var relSpec = new RelativeOffsetSpec(
                    masterLocalX: spec.OffsetMasterLocalX,
                    masterLocalY: spec.OffsetMasterLocalY,
                    masterAngleRad: spec.OffsetMasterAngleDeg * DegreesToRadians,
                    mmPerPixel: spec.GeometryMmPerPixel > 0.0 ? spec.GeometryMmPerPixel : 0.05);

                var uvwStage = UvwStageInverseKinematics.CreateStandardStage(
                    stageRadiusMm: Math.Max(20.0, spec.OffsetStageRadiusMm));

                return new RelativeOffsetCombinerNode(
                    nid,
                    spec.Name,
                    in relSpec,
                    uvwStage,
                    inRefPose,
                    inTargetPt,
                    boolOutSlots[(nid, RelativeOffsetCombinerNode.PortOutValid)],
                    scalarOutSlots[(nid, RelativeOffsetCombinerNode.PortOutLocalDeltaX)],
                    scalarOutSlots[(nid, RelativeOffsetCombinerNode.PortOutLocalDeltaY)],
                    scalarOutSlots[(nid, RelativeOffsetCombinerNode.PortOutDeltaThetaDeg)],
                    scalarOutSlots[(nid, RelativeOffsetCombinerNode.PortOutTotalOffsetMagnitude)],
                    scalarOutSlots[(nid, RelativeOffsetCombinerNode.PortOutStrokeU)],
                    scalarOutSlots[(nid, RelativeOffsetCombinerNode.PortOutStrokeV)],
                    scalarOutSlots[(nid, RelativeOffsetCombinerNode.PortOutStrokeW)]);
            }

            case VisionToolKind.ToleranceSpecJudge:
            {
                TerminalHandle<double> inPrimary = ResolveInputScalarHandle(nid, ScalarSpecJudgeNode.PortInPrimaryValue, links, scalarOutSlots);
                TerminalHandle<double> inSecondary = ResolveInputScalarHandle(nid, ScalarSpecJudgeNode.PortInSecondaryValue, links, scalarOutSlots);

                var limit = new ScalarSpecLimit(
                    lowerSpecLimit: Math.Min(spec.JudgeLsl, spec.JudgeUsl),
                    upperSpecLimit: Math.Max(spec.JudgeLsl, spec.JudgeUsl),
                    missingPolicy: spec.JudgeMissingPolicy,
                    fallbackValue: spec.JudgeFallbackValue,
                    combineMode: spec.JudgeCombineMode);

                return new ScalarSpecJudgeNode(
                    nid,
                    spec.Name,
                    in limit,
                    inPrimary,
                    boolOutSlots[(nid, ScalarSpecJudgeNode.PortOutIsOk)],
                    scalarOutSlots[(nid, ScalarSpecJudgeNode.PortOutEvaluatedValue)],
                    inSecondary);
            }

            case VisionToolKind.FinalVerdictAggregator:
            {
                var gateList = new List<VerdictGateBinding>(8);
                for (int i = 0; i < links.Count; i++)
                {
                    EditablePortLink l = links[i];
                    if (l.TargetNodeId == nid &&
                        l.DataKind == TerminalDataKind.Boolean &&
                        boolOutSlots.TryGetValue((l.SourceNodeId, l.SourceOutputPort), out TerminalHandle<bool> boolSlot))
                    {
                        gateList.Add(new VerdictGateBinding(l.SourceNodeId, l.TargetInputPort, boolSlot));
                    }
                }

                if (gateList.Count == 0)
                {
                    foreach (KeyValuePair<(int NodeId, string Port), TerminalHandle<bool>> kvp in boolOutSlots)
                    {
                        if (kvp.Key.NodeId != nid)
                        {
                            gateList.Add(new VerdictGateBinding(kvp.Key.NodeId, $"Gate_{kvp.Key.NodeId}_{kvp.Key.Port}", kvp.Value));
                        }
                    }
                }

                TerminalHandle<double> inPrimary = ResolveInputScalarHandle(nid, FinalVerdictAggregatorNode.PortInPrimaryMetric, links, scalarOutSlots);
                TerminalHandle<double> inSecondary = ResolveInputScalarHandle(nid, FinalVerdictAggregatorNode.PortInSecondaryMetric, links, scalarOutSlots);
                TerminalHandle<double> inDx = ResolveInputScalarHandle(nid, FinalVerdictAggregatorNode.PortInAlignDeltaX, links, scalarOutSlots);
                TerminalHandle<double> inDy = ResolveInputScalarHandle(nid, FinalVerdictAggregatorNode.PortInAlignDeltaY, links, scalarOutSlots);
                TerminalHandle<double> inDTheta = ResolveInputScalarHandle(nid, FinalVerdictAggregatorNode.PortInAlignDeltaThetaDeg, links, scalarOutSlots);
                TerminalHandle<double> inU = ResolveInputScalarHandle(nid, FinalVerdictAggregatorNode.PortInStrokeU, links, scalarOutSlots);
                TerminalHandle<double> inV = ResolveInputScalarHandle(nid, FinalVerdictAggregatorNode.PortInStrokeV, links, scalarOutSlots);
                TerminalHandle<double> inW = ResolveInputScalarHandle(nid, FinalVerdictAggregatorNode.PortInStrokeW, links, scalarOutSlots);

                return new FinalVerdictAggregatorNode(
                    nid,
                    spec.Name,
                    spec.VerdictGate,
                    gateList.ToArray(),
                    boolOutSlots[(nid, FinalVerdictAggregatorNode.PortOutFinalOk)],
                    inPrimary,
                    inSecondary,
                    inDx,
                    inDy,
                    inDTheta,
                    inU,
                    inV,
                    inW);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(spec));
        }
    }

    public static (byte MinThreshold, byte MaxThreshold) ResolveEffectiveThresholdRange(
        ReadOnlyImageView2D<byte> image,
        (int X, int Y, int Width, int Height) roiRect,
        ImageJAutoThresholdMethod autoMethod,
        bool invertPolarity,
        byte manualMin,
        byte manualMax)
    {
        byte resolvedT = manualMin;
        if (autoMethod != ImageJAutoThresholdMethod.Manual)
        {
            RoiHistogramAnalysis hist = RoiHistogramAnalyzer.AnalyzeRect(
                image,
                roiRect.X,
                roiRect.Y,
                roiRect.Width,
                roiRect.Height);
            resolvedT = RoiImageProcessor.ResolveThreshold(in hist, autoMethod, manualMin);
        }

        if (invertPolarity)
        {
            return (byte.MinValue, resolvedT);
        }

        byte high = autoMethod == ImageJAutoThresholdMethod.Manual
            ? Math.Max(resolvedT, manualMax)
            : byte.MaxValue;
        return (resolvedT, high);
    }

    private static IToolBlockNode UnwrapNode(IToolBlockNode node)
        => node is PreFilteredToolNodeDecorator dec ? dec.InnerNode : node;

    private static (List<AlgorithmResultSummaryRow> Rows, List<VisionInspectionEnvelope> Envelopes) CollectStandardizedEnvelopesAndRows(
        ToolBlockRunner runner,
        ToolBlockContext context,
        IReadOnlyDictionary<int, EditableToolNodeSpec> specById)
    {
        ReadOnlySpan<IToolBlockNode> orderedNodes = runner.OrderedNodes;
        var rows = new List<AlgorithmResultSummaryRow>(orderedNodes.Length);
        var envelopes = new List<VisionInspectionEnvelope>(orderedNodes.Length);

        for (int i = 0; i < orderedNodes.Length; i++)
        {
            IToolBlockNode rawNode = orderedNodes[i];
            IToolBlockNode node = UnwrapNode(rawNode);
            EditableToolNodeSpec? spec = specById.GetValueOrDefault(node.NodeId);
            Guid roiId = spec?.BoundRoiId ?? Guid.Empty;

            VisionInspectionEnvelope? maybeEnv = node switch
            {
                PatMaxToolNode pat => ((IVisionToolResult)pat.LastResult).ToInspectionEnvelope(node.NodeId, roiId, spec?.PatMaxMinScore ?? 0.5, 1.0),
                CornerAlignmentToolNode cor => ((IVisionToolResult)cor.LastResult).ToInspectionEnvelope(node.NodeId, roiId, 85.0, 95.0),
                LargestBlobToolNode blob => ((IVisionToolResult)blob.LastResult).ToInspectionEnvelope(node.NodeId, roiId, spec?.BlobMinAreaPixels ?? 40, 100000.0),
                BinaryPixelCountToolNode bin => ((IVisionToolResult)bin.LastResult).ToInspectionEnvelope(node.NodeId, roiId, spec?.BinaryMinPassPercent ?? 10.0, spec?.BinaryMaxPassPercent ?? 95.0),
                CaliperLineCircleToolNode cal => ExtractCaliperEnvelope(cal, node.NodeId, roiId),
                VariationModelToolNode varNode => ((IVisionToolResult)varNode.LastResult).ToInspectionEnvelope(node.NodeId, roiId, 0.0, spec?.VariationMinDefectAreaPx ?? 8.0),
                _ => null
            };

            string preFilterTag = spec is not null && spec.PreFilter != RoiPreFilterKind.None
                ? $" [Filter:{spec.PreFilter}]"
                : string.Empty;

            if (maybeEnv is { } env)
            {
                envelopes.Add(env);
                VisionSpcRecord spc = env.Spc;
                string verdictHex = spc.IsPass ? "#10B981" : "#EF4444";
                string modelTxt = spc.MatchedModelIndex >= 0
                    ? $"Model #{spc.MatchedModelIndex} (ID:{spc.MatchedModelId})"
                    : "-";
                string poseTxt = $"({spc.WorldX:F1}, {spc.WorldY:F1}) {spc.WorldAngleDeg:+0.0;-0.0;0.0}°";
                string primaryTxt = $"{spc.PrimaryMetric:F3} (Q={spc.QualityScore:F2})";
                string auxTxt = FormatSpcAuxSummary(in spc) + preFilterTag;

                rows.Add(new AlgorithmResultSummaryRow(
                    NodeId: node.NodeId,
                    NodeName: node.NodeName,
                    CategoryBadge: spc.Category.ToString(),
                    StatusBadge: spc.Status.ToString(),
                    VerdictBadge: spc.IsPass ? "PASS" : "FAIL",
                    VerdictHex: verdictHex,
                    MatchedModelText: modelTxt,
                    WorldPoseText: poseTxt,
                    PrimaryMetricText: primaryTxt,
                    AuxDiagnosticsText: auxTxt));
            }
            else
            {
                (bool ok, string metricSummary) = EvaluateNodeLiveStatus(node, context);
                rows.Add(new AlgorithmResultSummaryRow(
                    NodeId: node.NodeId,
                    NodeName: node.NodeName,
                    CategoryBadge: spec is not null ? FormatToolShortBadge(spec.Kind) : "Combiner",
                    StatusBadge: ok ? "Succeeded" : "Failed",
                    VerdictBadge: ok ? "PASS" : "FAIL",
                    VerdictHex: ok ? "#10B981" : "#EF4444",
                    MatchedModelText: "-",
                    WorldPoseText: "(Virtual Link)",
                    PrimaryMetricText: metricSummary,
                    AuxDiagnosticsText: spec is { Kind: VisionToolKind.ToleranceSpecJudge }
                        ? $"Spec [{spec.JudgeLsl:F2} .. {spec.JudgeUsl:F2}]"
                        : "Combiner / Verdict Logic"));
            }
        }

        return (rows, envelopes);
    }

    private static VisionInspectionEnvelope ExtractCaliperEnvelope(CaliperLineCircleToolNode cal, int nodeId, Guid roiId) =>
        cal.Spec.Mode switch
        {
            CaliperMetroMode.EdgePairWidth => ((IVisionToolResult)cal.LastPairResult).ToInspectionEnvelope(nodeId, roiId),
            CaliperMetroMode.RakeLineFit => ((IVisionToolResult)cal.LastLineResult).ToInspectionEnvelope(nodeId, roiId),
            _ => ((IVisionToolResult)cal.LastCircleResult).ToInspectionEnvelope(nodeId, roiId)
        };

    private static string FormatSpcAuxSummary(in VisionSpcRecord spc) => spc.Category switch
    {
        VisionToolCategory.PatternMatching =>
            $"Cov={spc.AuxMetric1:F2}, Clut={spc.AuxMetric2:F2}, RMS={spc.RmsFitErrorPixels:F2}px, Contrast={spc.ContrastOrStdDev:F1}, Dipoles={spc.HitCount}/{spc.TotalSampleCount}",
        VisionToolCategory.BlobAnalysis =>
            $"Blobs={spc.TotalSampleCount}, Ecc={spc.AuxMetric1:F2}, Fill={spc.AuxMetric2:F2}",
        VisionToolCategory.BinaryPixelCount =>
            $"ValidPx={spc.HitCount}/{spc.TotalSampleCount} ({spc.PrimaryMetric:F1}%), MeanGV={spc.MeanGrayValue:F1}±{spc.ContrastOrStdDev:F1}",
        VisionToolCategory.SubpixelMetrology =>
            $"Inliers={spc.HitCount}/{spc.TotalSampleCount}, RMS={spc.RmsFitErrorPixels:F3}px, Contrast={spc.ContrastOrStdDev:F1}",
        VisionToolCategory.CornerAlignment =>
            $"InlierRatio={spc.AuxMetric1:P0}, RMS1={spc.AuxMetric2:F2}px, RMS2={spc.AuxMetric3:F2}px",
        VisionToolCategory.AnomalyVariation =>
            $"DefectPx={spc.HitCount}, MaxDiff={spc.AuxMetric1:F1}, MaxZ={spc.AuxMetric2:F2}",
        _ => $"Hits={spc.HitCount}/{spc.TotalSampleCount}, Margin={spc.SpecMarginRatio:F2}"
    };

    private static bool TryRegisterBuilderLink(
        ToolBlockBuilder builder,
        EditablePortLink link,
        IReadOnlyDictionary<(int NodeId, string Port), TerminalHandle<FixturePose2D>> poseOutSlots,
        IReadOnlyDictionary<(int NodeId, string Port), TerminalHandle<Point2D>> pointOutSlots,
        IReadOnlyDictionary<(int NodeId, string Port), TerminalHandle<Line2D>> lineOutSlots,
        IReadOnlyDictionary<(int NodeId, string Port), TerminalHandle<Circle2D>> circleOutSlots,
        IReadOnlyDictionary<(int NodeId, string Port), TerminalHandle<double>> scalarOutSlots,
        IReadOnlyDictionary<(int NodeId, string Port), TerminalHandle<bool>> boolOutSlots)
    {
        var key = (link.SourceNodeId, link.SourceOutputPort);
        switch (link.DataKind)
        {
            case TerminalDataKind.Pose when poseOutSlots.TryGetValue(key, out TerminalHandle<FixturePose2D> hPose):
                builder.AddLink(link.SourceNodeId, link.SourceOutputPort, link.TargetNodeId, link.TargetInputPort, hPose);
                return true;

            case TerminalDataKind.Point when pointOutSlots.TryGetValue(key, out TerminalHandle<Point2D> hPt):
                builder.AddLink(link.SourceNodeId, link.SourceOutputPort, link.TargetNodeId, link.TargetInputPort, hPt);
                return true;

            case TerminalDataKind.Line when lineOutSlots.TryGetValue(key, out TerminalHandle<Line2D> hLn):
                builder.AddLink(link.SourceNodeId, link.SourceOutputPort, link.TargetNodeId, link.TargetInputPort, hLn);
                return true;

            case TerminalDataKind.Circle when circleOutSlots.TryGetValue(key, out TerminalHandle<Circle2D> hCir):
                builder.AddLink(link.SourceNodeId, link.SourceOutputPort, link.TargetNodeId, link.TargetInputPort, hCir);
                return true;

            case TerminalDataKind.Scalar when scalarOutSlots.TryGetValue(key, out TerminalHandle<double> hSc):
                builder.AddLink(link.SourceNodeId, link.SourceOutputPort, link.TargetNodeId, link.TargetInputPort, hSc);
                return true;

            case TerminalDataKind.Boolean when boolOutSlots.TryGetValue(key, out TerminalHandle<bool> hBl):
                builder.AddLink(link.SourceNodeId, link.SourceOutputPort, link.TargetNodeId, link.TargetInputPort, hBl);
                return true;

            default:
                return false;
        }
    }

    private static ResultOverlay BuildCanvasOverlay(
        ToolBlockRunner runner,
        ToolBlockContext context,
        IReadOnlyDictionary<int, EditableToolNodeSpec> specById,
        IReadOnlyDictionary<int, (int X, int Y, int Width, int Height)> worldRoiById,
        IReadOnlyDictionary<int, FixturePose2D> localPoseById,
        ToolBlockInspectionResult verdict,
        double elapsedUs)
    {
        ReadOnlySpan<IToolBlockNode> orderedNodes = runner.OrderedNodes;
        var markers = ImmutableArray.CreateBuilder<MatchMarker>(orderedNodes.Length);

        for (int i = 0; i < orderedNodes.Length; i++)
        {
            IToolBlockNode node = UnwrapNode(orderedNodes[i]);
            if (!specById.ContainsKey(node.NodeId))
            {
                continue;
            }

            (int rx, int ry, int rw, int rh) = worldRoiById[node.NodeId];
            FixturePose2D localOffset = localPoseById[node.NodeId];

            switch (node)
            {
                case PatMaxToolNode patNode:
                {
                    SlotValue<bool> found = context.ReadBoolSlot(patNode.OutFound);
                    SlotValue<FixturePose2D> pose = context.ReadPoseSlot(patNode.OutPose);
                    SlotValue<double> score = context.ReadScalarSlot(patNode.OutScore);
                    short tplIdx = patNode.LastResult.MatchedTemplateIndex;

                    PointD center = found.IsValid && found.Value
                        ? new PointD(pose.Value.OriginX, pose.Value.OriginY)
                        : new PointD(rx + (rw * 0.5), ry + (rh * 0.5));
                    double angleRad = found.IsValid && found.Value ? pose.Value.AngleRad : 0.0;
                    Vector4 color = found.IsValid && found.Value ? ColorFixture : ColorFail;

                    ImmutableArray<PointD> box = BuildOrientedBox(center, rw, rh, angleRad);
                    ImmutableArray<ImmutableArray<PointD>> axes = BuildFixtureAxesContours(center, angleRad, 26.0);

                    markers.Add(new MatchMarker(
                        Bounds: box,
                        ReferencePoint: center,
                        AngleRadians: angleRad,
                        Label: $"{node.NodeName} [Tpl#{Math.Max((short)0, tplIdx)}] · Score={score.Value:F2} · θ={angleRad * RadiansToDegrees:+0.0;-0.0;0.0}°",
                        Color: color,
                        Features: [center])
                    {
                        AdditionalContours = axes,
                        BoundsColor = color,
                        BoundsThickness = 2.4
                    });
                    break;
                }

                case CornerAlignmentToolNode cornerNode:
                {
                    SlotValue<bool> found = context.ReadBoolSlot(cornerNode.OutFound);
                    SlotValue<Point2D> cornerPt = context.ReadPointSlot(cornerNode.OutCorner);
                    SlotValue<Line2D> line1 = context.ReadLineSlot(cornerNode.OutLine1);
                    SlotValue<Line2D> line2 = context.ReadLineSlot(cornerNode.OutLine2);
                    SlotValue<double> incAngle = context.ReadScalarSlot(cornerNode.OutIncludedAngleDeg);

                    PointD refPt = found.IsValid && found.Value
                        ? new PointD(cornerPt.Value.X, cornerPt.Value.Y)
                        : new PointD(rx + (rw * 0.5), ry + (rh * 0.5));
                    Vector4 color = found.IsValid && found.Value ? ColorFixture : ColorFail;

                    var contours = ImmutableArray.CreateBuilder<ImmutableArray<PointD>>();
                    if (line1.IsValid)
                    {
                        contours.Add([
                            new PointD(line1.Value.P1.X, line1.Value.P1.Y),
                            new PointD(line1.Value.P2.X, line1.Value.P2.Y)
                        ]);
                    }
                    if (line2.IsValid)
                    {
                        contours.Add([
                            new PointD(line2.Value.P1.X, line2.Value.P1.Y),
                            new PointD(line2.Value.P2.X, line2.Value.P2.Y)
                        ]);
                    }

                    ImmutableArray<PointD> box = BuildOrientedBox(new PointD(rx + (rw * 0.5), ry + (rh * 0.5)), rw, rh, 0.0);
                    markers.Add(new MatchMarker(
                        Bounds: box,
                        ReferencePoint: refPt,
                        AngleRadians: 0.0,
                        Label: $"{node.NodeName} · ∠={incAngle.Value:F2}°",
                        Color: color,
                        Features: [refPt])
                    {
                        AdditionalContours = contours.ToImmutable(),
                        BoundsColor = color,
                        BoundsThickness = 2.2
                    });
                    break;
                }

                case LargestBlobToolNode blobNode:
                {
                    SlotValue<bool> found = context.ReadBoolSlot(blobNode.OutFound);
                    SlotValue<Point2D> center = context.ReadPointSlot(blobNode.OutMaxBlobCenter);
                    SlotValue<double> area = context.ReadScalarSlot(blobNode.OutMaxBlobArea);
                    context.TryResolveInputFixture(blobNode.InFixture, out FixturePose2D baseFixture);
                    FixturePose2D worldRoiPose = baseFixture.Compose(localOffset);

                    PointD roiWorldCenter = new(worldRoiPose.OriginX, worldRoiPose.OriginY);
                    ImmutableArray<PointD> box = BuildOrientedBox(roiWorldCenter, rw, rh, worldRoiPose.AngleRad);
                    PointD blobPt = found.IsValid && found.Value
                        ? new PointD(center.Value.X, center.Value.Y)
                        : roiWorldCenter;
                    Vector4 color = found.IsValid && found.Value ? ColorPass : ColorFail;

                    markers.Add(new MatchMarker(
                        Bounds: box,
                        ReferencePoint: blobPt,
                        AngleRadians: worldRoiPose.AngleRad,
                        Label: $"{node.NodeName} · Area={area.Value:F0}px²",
                        Color: color,
                        Features: [blobPt])
                    {
                        BoundsColor = color,
                        BoundsThickness = 2.0
                    });
                    break;
                }

                case BinaryPixelCountToolNode binNode:
                {
                    SlotValue<bool> isPass = context.ReadBoolSlot(binNode.OutIsPass);
                    SlotValue<Point2D> com = context.ReadPointSlot(binNode.OutCenterOfMass);
                    SlotValue<double> ratio = context.ReadScalarSlot(binNode.OutAreaRatioPercent);
                    SlotValue<double> validPx = context.ReadScalarSlot(binNode.OutValidPixelCount);
                    context.TryResolveInputFixture(binNode.InFixture, out FixturePose2D baseFixture);
                    FixturePose2D worldRoiPose = baseFixture.Compose(localOffset);

                    PointD roiWorldCenter = new(worldRoiPose.OriginX, worldRoiPose.OriginY);
                    ImmutableArray<PointD> box = BuildOrientedBox(roiWorldCenter, rw, rh, worldRoiPose.AngleRad);
                    PointD comPt = com.IsValid ? new PointD(com.Value.X, com.Value.Y) : roiWorldCenter;
                    bool passed = isPass.IsValid && isPass.Value;
                    Vector4 color = passed ? ColorPass : ColorFail;

                    markers.Add(new MatchMarker(
                        Bounds: box,
                        ReferencePoint: comPt,
                        AngleRadians: worldRoiPose.AngleRad,
                        Label: $"{node.NodeName} · {ratio.Value:F1}% ({validPx.Value:F0}px)",
                        Color: color,
                        Features: [comPt])
                    {
                        BoundsColor = color,
                        BoundsThickness = 2.0
                    });
                    break;
                }

                case CaliperLineCircleToolNode calNode:
                {
                    SlotValue<bool> found = context.ReadBoolSlot(calNode.OutFound);
                    SlotValue<Point2D> edgeOrCenter = context.ReadPointSlot(calNode.OutEdgeOrCenter);
                    SlotValue<Line2D> line = context.ReadLineSlot(calNode.OutFittedLine);
                    SlotValue<Circle2D> circle = context.ReadCircleSlot(calNode.OutFittedCircle);
                    SlotValue<double> widthOrRadius = context.ReadScalarSlot(calNode.OutMeasuredWidthOrRadius);
                    context.TryResolveInputFixture(calNode.InFixture, out FixturePose2D baseFixture);
                    FixturePose2D worldRoiPose = baseFixture.Compose(localOffset);

                    PointD roiWorldCenter = new(worldRoiPose.OriginX, worldRoiPose.OriginY);
                    ImmutableArray<PointD> box = BuildOrientedBox(roiWorldCenter, rw, rh, worldRoiPose.AngleRad);
                    PointD refPt = found.IsValid && found.Value
                        ? new PointD(edgeOrCenter.Value.X, edgeOrCenter.Value.Y)
                        : roiWorldCenter;
                    Vector4 color = found.IsValid && found.Value ? ColorPass : ColorFail;

                    var contours = ImmutableArray.CreateBuilder<ImmutableArray<PointD>>();
                    if (circle.IsValid && circle.Value.Radius > 1.0)
                    {
                        contours.Add(BuildCircleContour(
                            new PointD(circle.Value.Center.X, circle.Value.Center.Y),
                            circle.Value.Radius));
                    }
                    if (line.IsValid)
                    {
                        contours.Add([
                            new PointD(line.Value.P1.X, line.Value.P1.Y),
                            new PointD(line.Value.P2.X, line.Value.P2.Y)
                        ]);
                    }

                    markers.Add(new MatchMarker(
                        Bounds: box,
                        ReferencePoint: refPt,
                        AngleRadians: worldRoiPose.AngleRad,
                        Label: $"{node.NodeName} · Val={widthOrRadius.Value:F2}px",
                        Color: color,
                        Features: [refPt])
                    {
                        AdditionalContours = contours.ToImmutable(),
                        BoundsColor = color,
                        BoundsThickness = 2.0
                    });
                    break;
                }

                case VariationModelToolNode varNode:
                {
                    ReadOnlySpan<ToolBlockTerminalDescriptor> outs = varNode.GetOutputTerminals();
                    SlotValue<bool> isOk = context.ReadBoolSlot(new TerminalHandle<bool>(outs[0].SlotIndex));
                    SlotValue<double> defectPx = context.ReadScalarSlot(new TerminalHandle<double>(outs[1].SlotIndex));

                    ReadOnlySpan<ToolBlockTerminalDescriptor> ins = varNode.GetInputTerminals();
                    FixturePose2D baseFixture = FixturePose2D.Identity;
                    if (ins.Length > 0)
                    {
                        context.TryResolveInputFixture(new TerminalHandle<FixturePose2D>(ins[0].SlotIndex), out baseFixture);
                    }

                    FixturePose2D worldRoiPose = baseFixture.Compose(localOffset);
                    PointD roiWorldCenter = new(worldRoiPose.OriginX, worldRoiPose.OriginY);
                    ImmutableArray<PointD> box = BuildOrientedBox(roiWorldCenter, rw, rh, worldRoiPose.AngleRad);
                    bool passed = isOk.IsValid && isOk.Value;
                    Vector4 color = passed ? ColorPass : ColorFail;

                    markers.Add(new MatchMarker(
                        Bounds: box,
                        ReferencePoint: roiWorldCenter,
                        AngleRadians: worldRoiPose.AngleRad,
                        Label: $"{node.NodeName} · {(passed ? "OK" : "DEFECT")} ({defectPx.Value:F0}px)",
                        Color: color,
                        Features: passed ? [] : [roiWorldCenter])
                    {
                        BoundsColor = color,
                        BoundsThickness = 2.1
                    });
                    break;
                }

                case GeometryCombinerNode geomNode:
                {
                    ReadOnlySpan<ToolBlockTerminalDescriptor> ins = geomNode.GetInputTerminals();
                    ReadOnlySpan<ToolBlockTerminalDescriptor> outs = geomNode.GetOutputTerminals();

                    SlotValue<double> dist = context.ReadScalarSlot(new TerminalHandle<double>(outs[1].SlotIndex));
                    SlotValue<Point2D> midPt = context.ReadPointSlot(new TerminalHandle<Point2D>(outs[5].SlotIndex));
                    if (!dist.IsValid && !midPt.IsValid)
                    {
                        break;
                    }

                    var contours = ImmutableArray.CreateBuilder<ImmutableArray<PointD>>();
                    PointD anchor = new(midPt.Value.X, midPt.Value.Y);

                    if (ins.Length >= 2 && ins[0].DataKind == TerminalDataKind.Point && ins[1].DataKind == TerminalDataKind.Point)
                    {
                        SlotValue<Point2D> ptA = context.ReadPointSlot(new TerminalHandle<Point2D>(ins[0].SlotIndex));
                        SlotValue<Point2D> ptB = context.ReadPointSlot(new TerminalHandle<Point2D>(ins[1].SlotIndex));
                        if (ptA.IsValid && ptB.IsValid)
                        {
                            contours.Add([new PointD(ptA.Value.X, ptA.Value.Y), new PointD(ptB.Value.X, ptB.Value.Y)]);
                        }
                    }

                    ImmutableArray<PointD> tinyBox = BuildOrientedBox(anchor, 8.0, 8.0, 0.0);
                    markers.Add(new MatchMarker(
                        Bounds: tinyBox,
                        ReferencePoint: anchor,
                        AngleRadians: 0.0,
                        Label: $"{node.NodeName} · D={dist.Value:F2}",
                        Color: ColorCombiner,
                        Features: [anchor])
                    {
                        AdditionalContours = contours.ToImmutable(),
                        BoundsColor = ColorCombiner,
                        BoundsThickness = 1.6
                    });
                    break;
                }
            }
        }

        bool isOverallOk = verdict.Decision == FinalDecision.Ok;
        string summary = $"[Vision ToolBlock] Final Verdict: {verdict.Decision} ({verdict.PassedGateCount}/{verdict.EvaluatedGateCount} Gates) · Metric={verdict.PrimaryMetricValue:F2} · {elapsedUs:F1} µs";
        return new ResultOverlay(
            Summary: summary,
            Color: isOverallOk ? ColorPass : ColorFail,
            SearchBoundary: [],
            Matches: markers.ToImmutable(),
            ShowFeatures: true);
    }

    private static IReadOnlyList<DagNodeDisplayItem> BuildOrderedDagItems(
        ToolBlockRunner runner,
        ToolBlockContext context,
        IReadOnlyDictionary<int, EditableToolNodeSpec> specById)
    {
        ReadOnlySpan<IToolBlockNode> orderedNodes = runner.OrderedNodes;
        var list = new List<DagNodeDisplayItem>(orderedNodes.Length);

        for (int i = 0; i < orderedNodes.Length; i++)
        {
            IToolBlockNode node = UnwrapNode(orderedNodes[i]);
            EditableToolNodeSpec? spec = specById.GetValueOrDefault(node.NodeId);
            VisionToolKind kind = spec?.Kind ?? VisionToolKind.ShapePatternMatch;
            string roiLabel = spec?.RequiresCanvasRoi == true ? spec.BoundRoiName : "Virtual (Port Link)";

            (bool isOk, string metricText) = EvaluateNodeLiveStatus(node, context);
            string statusBadge = isOk ? "OK" : "NG / MISS";
            string statusHex = isOk ? "#10B981" : "#EF4444";

            ReadOnlySpan<ToolBlockTerminalDescriptor> ins = node.GetInputTerminals();
            ReadOnlySpan<ToolBlockTerminalDescriptor> outs = node.GetOutputTerminals();

            list.Add(new DagNodeDisplayItem(
                ExecutionStep: i + 1,
                NodeId: node.NodeId,
                Name: node.NodeName,
                Kind: kind,
                ToolTypeBadge: FormatToolShortBadge(kind),
                BoundRoiId: spec?.BoundRoiId,
                BoundRoiLabel: roiLabel,
                InPortSummary: $"In: {ins.Length} ports",
                OutPortSummary: $"Out: {outs.Length} ports",
                LiveMetricSummary: metricText,
                StatusBadge: statusBadge,
                StatusHex: statusHex));
        }

        return list;
    }

    private static (bool IsOk, string MetricText) EvaluateNodeLiveStatus(IToolBlockNode rawNode, ToolBlockContext context)
    {
        IToolBlockNode node = UnwrapNode(rawNode);
        if (node is BinaryPixelCountToolNode binNode)
        {
            SlotValue<bool> pass = context.ReadBoolSlot(binNode.OutIsPass);
            SlotValue<double> ratio = context.ReadScalarSlot(binNode.OutAreaRatioPercent);
            return (pass.IsValid && pass.Value, $"AreaRatio = {ratio.Value:F1}%");
        }

        ReadOnlySpan<ToolBlockTerminalDescriptor> outs = node.GetOutputTerminals();
        if (outs.IsEmpty)
        {
            return (true, "Ready");
        }

        SlotValue<bool> firstBool = outs[0].DataKind == TerminalDataKind.Boolean
            ? context.ReadBoolSlot(new TerminalHandle<bool>(outs[0].SlotIndex))
            : new SlotValue<bool>(true, true);

        bool ok = firstBool.IsValid && firstBool.Value;
        int summaryIdx = outs.Length > 1 ? 1 : 0;
        string summary = FormatDescriptorValue(outs[summaryIdx], context);
        return (ok, $"{outs[summaryIdx].PortName} = {summary}");
    }

    private static IReadOnlyList<TerminalLinkRowItem> BuildLinkRows(
        IReadOnlyList<EditablePortLink> validLinks,
        IReadOnlyDictionary<int, IToolBlockNode> concreteNodes,
        ToolBlockContext context,
        IReadOnlyDictionary<int, EditableToolNodeSpec> specById)
    {
        var rows = new List<TerminalLinkRowItem>(validLinks.Count);
        for (int i = 0; i < validLinks.Count; i++)
        {
            EditablePortLink link = validLinks[i];
            string srcName = specById.TryGetValue(link.SourceNodeId, out EditableToolNodeSpec? s) ? s.Name : $"Node#{link.SourceNodeId}";
            string dstName = specById.TryGetValue(link.TargetNodeId, out EditableToolNodeSpec? t) ? t.Name : $"Node#{link.TargetNodeId}";

            IToolBlockNode srcNode = concreteNodes[link.SourceNodeId];
            (string handleBadge, string liveVal, bool isValid) = ReadNodeOutputPort(srcNode, link.SourceOutputPort, context);

            rows.Add(new TerminalLinkRowItem(
                LinkIndex: i,
                SourceNodeId: link.SourceNodeId,
                SourceNodeName: srcName,
                SourcePortName: link.SourceOutputPort,
                TargetNodeId: link.TargetNodeId,
                TargetNodeName: dstName,
                TargetPortName: link.TargetInputPort,
                DataKindBadge: link.DataKind.ToString(),
                SlotHandleBadge: handleBadge,
                LiveValueSummary: liveVal,
                IsValid: isValid,
                AccentHex: isValid ? "#38BDF8" : "#F87171"));
        }

        return rows;
    }

    private static IReadOnlyList<TerminalSlotDisplayItem> BuildSlotDisplayItems(
        ToolBlockRunner runner,
        ToolBlockContext context,
        IReadOnlyList<EditablePortLink> validLinks,
        IReadOnlyDictionary<int, EditableToolNodeSpec> specById)
    {
        ReadOnlySpan<IToolBlockNode> orderedNodes = runner.OrderedNodes;
        var items = new List<TerminalSlotDisplayItem>(orderedNodes.Length * 3);

        for (int i = 0; i < orderedNodes.Length; i++)
        {
            IToolBlockNode node = orderedNodes[i];
            ReadOnlySpan<ToolBlockTerminalDescriptor> outs = node.GetOutputTerminals();
            for (int p = 0; p < outs.Length; p++)
            {
                ToolBlockTerminalDescriptor desc = outs[p];
                string valText = FormatDescriptorValue(desc, context);
                bool isValid = IsDescriptorSlotValid(desc, context);

                var consumers = new List<string>(2);
                for (int l = 0; l < validLinks.Count; l++)
                {
                    if (validLinks[l].SourceNodeId == node.NodeId &&
                        string.Equals(validLinks[l].SourceOutputPort, desc.PortName, StringComparison.Ordinal))
                    {
                        string dstName = specById.TryGetValue(validLinks[l].TargetNodeId, out EditableToolNodeSpec? t)
                            ? t.Name
                            : $"Node#{validLinks[l].TargetNodeId}";
                        consumers.Add($"{dstName}.{validLinks[l].TargetInputPort}");
                    }
                }

                items.Add(new TerminalSlotDisplayItem(
                    SlotHandleLabel: $"{desc.DataKind}[{desc.SlotIndex}]",
                    DataKind: desc.DataKind.ToString(),
                    ProducerPort: $"{node.NodeName}.{desc.PortName}",
                    ConsumerPorts: consumers.Count > 0 ? string.Join(", ", consumers) : "(Unlinked)",
                    FormattedValue: valText,
                    IsValid: isValid,
                    StatusBadge: isValid ? "VALID" : "INVALID"));
            }
        }

        return items;
    }

    private static (string HandleBadge, string LiveValue, bool IsValid) ReadNodeOutputPort(
        IToolBlockNode node,
        string portName,
        ToolBlockContext context)
    {
        ReadOnlySpan<ToolBlockTerminalDescriptor> outs = node.GetOutputTerminals();
        for (int i = 0; i < outs.Length; i++)
        {
            if (string.Equals(outs[i].PortName, portName, StringComparison.Ordinal))
            {
                string val = FormatDescriptorValue(outs[i], context);
                bool valid = IsDescriptorSlotValid(outs[i], context);
                return ($"{outs[i].DataKind}[{outs[i].SlotIndex}]", val, valid);
            }
        }

        return ("Unbound", "N/A", false);
    }

    private static string FormatDescriptorValue(ToolBlockTerminalDescriptor desc, ToolBlockContext context)
    {
        switch (desc.DataKind)
        {
            case TerminalDataKind.Pose:
            {
                SlotValue<FixturePose2D> s = context.ReadPoseSlot(new TerminalHandle<FixturePose2D>(desc.SlotIndex));
                return s.IsValid
                    ? $"({s.Value.OriginX:F1}, {s.Value.OriginY:F1}, {s.Value.AngleRad * RadiansToDegrees:+0.0;-0.0;0.0}°)"
                    : "Invalid";
            }
            case TerminalDataKind.Point:
            {
                SlotValue<Point2D> s = context.ReadPointSlot(new TerminalHandle<Point2D>(desc.SlotIndex));
                return s.IsValid ? $"({s.Value.X:F2}, {s.Value.Y:F2})" : "Invalid";
            }
            case TerminalDataKind.Line:
            {
                SlotValue<Line2D> s = context.ReadLineSlot(new TerminalHandle<Line2D>(desc.SlotIndex));
                return s.IsValid ? $"N=({s.Value.CosTheta:F2},{s.Value.SinTheta:F2}) ρ={s.Value.Rho:F1}" : "Invalid";
            }
            case TerminalDataKind.Circle:
            {
                SlotValue<Circle2D> s = context.ReadCircleSlot(new TerminalHandle<Circle2D>(desc.SlotIndex));
                return s.IsValid ? $"C=({s.Value.Center.X:F1},{s.Value.Center.Y:F1}) R={s.Value.Radius:F2}" : "Invalid";
            }
            case TerminalDataKind.Scalar:
            {
                SlotValue<double> s = context.ReadScalarSlot(new TerminalHandle<double>(desc.SlotIndex));
                return s.IsValid ? $"{s.Value:F3}" : "Invalid";
            }
            case TerminalDataKind.Boolean:
            {
                SlotValue<bool> s = context.ReadBoolSlot(new TerminalHandle<bool>(desc.SlotIndex));
                return s.IsValid ? (s.Value ? "True (OK)" : "False (NG)") : "Invalid";
            }
            default:
                return "None";
        }
    }

    private static bool IsDescriptorSlotValid(ToolBlockTerminalDescriptor desc, ToolBlockContext context) => desc.DataKind switch
    {
        TerminalDataKind.Pose => context.ReadPoseSlot(new TerminalHandle<FixturePose2D>(desc.SlotIndex)).IsValid,
        TerminalDataKind.Point => context.ReadPointSlot(new TerminalHandle<Point2D>(desc.SlotIndex)).IsValid,
        TerminalDataKind.Line => context.ReadLineSlot(new TerminalHandle<Line2D>(desc.SlotIndex)).IsValid,
        TerminalDataKind.Circle => context.ReadCircleSlot(new TerminalHandle<Circle2D>(desc.SlotIndex)).IsValid,
        TerminalDataKind.Scalar => context.ReadScalarSlot(new TerminalHandle<double>(desc.SlotIndex)).IsValid,
        TerminalDataKind.Boolean => context.ReadBoolSlot(new TerminalHandle<bool>(desc.SlotIndex)).IsValid,
        _ => false
    };

    private static (List<TerminalPortOption> OutPorts, List<TerminalPortOption> InPorts) CollectPortOptions(
        IReadOnlyList<EditableToolNodeSpec> nodeSpecs,
        IReadOnlyDictionary<int, IToolBlockNode> concreteNodes)
    {
        var outPorts = new List<TerminalPortOption>(32);
        var inPorts = new List<TerminalPortOption>(32);

        for (int i = 0; i < nodeSpecs.Count; i++)
        {
            EditableToolNodeSpec spec = nodeSpecs[i];
            IToolBlockNode node = concreteNodes[spec.NodeId];

            ReadOnlySpan<ToolBlockTerminalDescriptor> outs = node.GetOutputTerminals();
            for (int p = 0; p < outs.Length; p++)
            {
                outPorts.Add(new TerminalPortOption(
                    spec.NodeId,
                    spec.Name,
                    outs[p].PortName,
                    TerminalDirection.Output,
                    outs[p].DataKind,
                    $"[{spec.Name}].{outs[p].PortName} ({outs[p].DataKind})"));
            }

            AddConnectableInputPorts(spec, inPorts);
        }

        return (outPorts, inPorts);
    }

    private static void AddConnectableInputPorts(EditableToolNodeSpec spec, List<TerminalPortOption> inPorts)
    {
        void AddIn(string port, TerminalDataKind kind)
            => inPorts.Add(new TerminalPortOption(spec.NodeId, spec.Name, port, TerminalDirection.Input, kind, $"[{spec.Name}].{port} ({kind})"));

        switch (spec.Kind)
        {
            case VisionToolKind.RleBlobAnalyzer:
            case VisionToolKind.BinaryPixelCounter:
            case VisionToolKind.SubpixelCaliperMetrology:
            case VisionToolKind.GoldenSurfaceInspector:
                AddIn("InFixture", TerminalDataKind.Pose);
                break;

            case VisionToolKind.GeometryMathCombiner:
                AddIn(GeometryCombinerNode.PortInPointA, TerminalDataKind.Point);
                AddIn(GeometryCombinerNode.PortInPointB, TerminalDataKind.Point);
                AddIn(GeometryCombinerNode.PortInLineA, TerminalDataKind.Line);
                AddIn(GeometryCombinerNode.PortInLineB, TerminalDataKind.Line);
                break;

            case VisionToolKind.RelativeOffsetKinematics:
                AddIn(RelativeOffsetCombinerNode.PortInReferencePose, TerminalDataKind.Pose);
                AddIn(RelativeOffsetCombinerNode.PortInTargetPoint, TerminalDataKind.Point);
                break;

            case VisionToolKind.ToleranceSpecJudge:
                AddIn(ScalarSpecJudgeNode.PortInPrimaryValue, TerminalDataKind.Scalar);
                AddIn(ScalarSpecJudgeNode.PortInSecondaryValue, TerminalDataKind.Scalar);
                break;

            case VisionToolKind.FinalVerdictAggregator:
                AddIn("Gate_0", TerminalDataKind.Boolean);
                AddIn("Gate_1", TerminalDataKind.Boolean);
                AddIn("Gate_2", TerminalDataKind.Boolean);
                AddIn("Gate_3", TerminalDataKind.Boolean);
                AddIn(FinalVerdictAggregatorNode.PortInPrimaryMetric, TerminalDataKind.Scalar);
                AddIn(FinalVerdictAggregatorNode.PortInSecondaryMetric, TerminalDataKind.Scalar);
                AddIn(FinalVerdictAggregatorNode.PortInAlignDeltaX, TerminalDataKind.Scalar);
                AddIn(FinalVerdictAggregatorNode.PortInAlignDeltaY, TerminalDataKind.Scalar);
                AddIn(FinalVerdictAggregatorNode.PortInAlignDeltaThetaDeg, TerminalDataKind.Scalar);
                AddIn(FinalVerdictAggregatorNode.PortInStrokeU, TerminalDataKind.Scalar);
                AddIn(FinalVerdictAggregatorNode.PortInStrokeV, TerminalDataKind.Scalar);
                AddIn(FinalVerdictAggregatorNode.PortInStrokeW, TerminalDataKind.Scalar);
                break;
        }
    }

    private static TerminalHandle<FixturePose2D> ResolveInputPoseHandle(
        int targetNodeId,
        string targetPort,
        IReadOnlyList<EditablePortLink> links,
        IReadOnlyDictionary<(int NodeId, string Port), TerminalHandle<FixturePose2D>> poseOutSlots)
    {
        for (int i = 0; i < links.Count; i++)
        {
            EditablePortLink l = links[i];
            if (l.TargetNodeId == targetNodeId &&
                string.Equals(l.TargetInputPort, targetPort, StringComparison.Ordinal) &&
                poseOutSlots.TryGetValue((l.SourceNodeId, l.SourceOutputPort), out TerminalHandle<FixturePose2D> h))
            {
                return h;
            }
        }

        return TerminalHandle<FixturePose2D>.Unbound;
    }

    private static TerminalHandle<Point2D> ResolveInputPointHandle(
        int targetNodeId,
        string targetPort,
        IReadOnlyList<EditablePortLink> links,
        IReadOnlyDictionary<(int NodeId, string Port), TerminalHandle<Point2D>> pointOutSlots)
    {
        for (int i = 0; i < links.Count; i++)
        {
            EditablePortLink l = links[i];
            if (l.TargetNodeId == targetNodeId &&
                string.Equals(l.TargetInputPort, targetPort, StringComparison.Ordinal) &&
                pointOutSlots.TryGetValue((l.SourceNodeId, l.SourceOutputPort), out TerminalHandle<Point2D> h))
            {
                return h;
            }
        }

        return TerminalHandle<Point2D>.Unbound;
    }

    private static TerminalHandle<Line2D> ResolveInputLineHandle(
        int targetNodeId,
        string targetPort,
        IReadOnlyList<EditablePortLink> links,
        IReadOnlyDictionary<(int NodeId, string Port), TerminalHandle<Line2D>> lineOutSlots)
    {
        for (int i = 0; i < links.Count; i++)
        {
            EditablePortLink l = links[i];
            if (l.TargetNodeId == targetNodeId &&
                string.Equals(l.TargetInputPort, targetPort, StringComparison.Ordinal) &&
                lineOutSlots.TryGetValue((l.SourceNodeId, l.SourceOutputPort), out TerminalHandle<Line2D> h))
            {
                return h;
            }
        }

        return TerminalHandle<Line2D>.Unbound;
    }

    private static TerminalHandle<double> ResolveInputScalarHandle(
        int targetNodeId,
        string targetPort,
        IReadOnlyList<EditablePortLink> links,
        IReadOnlyDictionary<(int NodeId, string Port), TerminalHandle<double>> scalarOutSlots)
    {
        for (int i = 0; i < links.Count; i++)
        {
            EditablePortLink l = links[i];
            if (l.TargetNodeId == targetNodeId &&
                string.Equals(l.TargetInputPort, targetPort, StringComparison.Ordinal) &&
                scalarOutSlots.TryGetValue((l.SourceNodeId, l.SourceOutputPort), out TerminalHandle<double> h))
            {
                return h;
            }
        }

        return TerminalHandle<double>.Unbound;
    }

    private static FixturePose2D FindLinkedFixtureMasterPose(
        int targetNodeId,
        IReadOnlyList<EditablePortLink> links,
        IReadOnlyDictionary<int, FixturePose2D> masterPosesByNodeId)
    {
        for (int i = 0; i < links.Count; i++)
        {
            EditablePortLink link = links[i];
            if (link.TargetNodeId == targetNodeId &&
                string.Equals(link.TargetInputPort, "InFixture", StringComparison.Ordinal) &&
                masterPosesByNodeId.TryGetValue(link.SourceNodeId, out FixturePose2D refPose))
            {
                return refPose;
            }
        }

        return FixturePose2D.Identity;
    }

    private static FixturePose2D ComputeLocalOffsetPose(
        int rx,
        int ry,
        int rw,
        int rh,
        FixturePose2D refPose)
    {
        float worldCx = rx + (rw * 0.5f);
        float worldCy = ry + (rh * 0.5f);
        if (refPose == FixturePose2D.Identity)
        {
            return new FixturePose2D(worldCx, worldCy, 0.0f, 1.0f);
        }

        Vector2 localPt = refPose.WorldToLocal(worldCx, worldCy);
        return new FixturePose2D(localPt.X, localPt.Y, 0.0f, 1.0f);
    }

    public static (int X, int Y, int Width, int Height) ResolveCanvasRoiRect(
        DocumentState state,
        Guid? roiId,
        int imageWidth,
        int imageHeight,
        int fallbackIndex)
    {
        if (roiId is { } id && state.Rois.TryGetValue(id, out Roi? roi))
        {
            int w = Math.Max(MinCropDimension, (int)Math.Round(roi.Geometry.Width));
            int h = Math.Max(MinCropDimension, (int)Math.Round(roi.Geometry.Height));
            int x = (int)Math.Round(roi.Geometry.Center.X - (w * 0.5));
            int y = (int)Math.Round(roi.Geometry.Center.Y - (h * 0.5));
            return ClampRect(x, y, w, h, imageWidth, imageHeight);
        }

        int fallbackX = Math.Clamp(60 + (fallbackIndex * 45), 8, Math.Max(16, imageWidth - 72));
        int fallbackY = Math.Clamp(60 + (fallbackIndex * 35), 8, Math.Max(16, imageHeight - 72));
        return ClampRect(fallbackX, fallbackY, 64, 64, imageWidth, imageHeight);
    }

    private static (int X, int Y, int Width, int Height) ClampRect(int x, int y, int width, int height, int imageWidth, int imageHeight)
    {
        int w = Math.Clamp(width, MinCropDimension, Math.Max(MinCropDimension, imageWidth - 4));
        int h = Math.Clamp(height, MinCropDimension, Math.Max(MinCropDimension, imageHeight - 4));
        int cx = Math.Clamp(x, 1, Math.Max(1, imageWidth - w - 1));
        int cy = Math.Clamp(y, 1, Math.Max(1, imageHeight - h - 1));
        return (cx, cy, w, h);
    }

    private static byte[] ExtractCropBytes(ReadOnlyImageView2D<byte> image, int rx, int ry, int rw, int rh)
    {
        var dst = new byte[rw * rh];
        ReadOnlySpan<byte> src = image.Pixels;
        for (int y = 0; y < rh; y++)
        {
            src.Slice(((ry + y) * image.Stride) + rx, rw)
                .CopyTo(dst.AsSpan(y * rw, rw));
        }

        return dst;
    }

    private static ImmutableArray<PointD> BuildOrientedBox(PointD center, double width, double height, double angleRad)
    {
        double hw = width * 0.5;
        double hh = height * 0.5;
        double cos = Math.Cos(angleRad);
        double sin = Math.Sin(angleRad);

        PointD Rotate(double lx, double ly)
            => new(center.X + (lx * cos) - (ly * sin), center.Y + (lx * sin) + (ly * cos));

        return
        [
            Rotate(-hw, -hh),
            Rotate(hw, -hh),
            Rotate(hw, hh),
            Rotate(-hw, hh)
        ];
    }

    private static ImmutableArray<ImmutableArray<PointD>> BuildFixtureAxesContours(PointD origin, double angleRad, double axisLen)
    {
        double cos = Math.Cos(angleRad);
        double sin = Math.Sin(angleRad);
        PointD xAxisEnd = new(origin.X + (axisLen * cos), origin.Y + (axisLen * sin));
        PointD yAxisEnd = new(origin.X - (axisLen * sin), origin.Y + (axisLen * cos));
        return [[origin, xAxisEnd], [origin, yAxisEnd]];
    }

    private static ImmutableArray<PointD> BuildCircleContour(PointD center, double radius)
    {
        var pts = ImmutableArray.CreateBuilder<PointD>(CirclePolygonSegments + 1);
        for (int i = 0; i <= CirclePolygonSegments; i++)
        {
            double ang = (2.0 * Math.PI * i) / CirclePolygonSegments;
            pts.Add(new PointD(center.X + (radius * Math.Cos(ang)), center.Y + (radius * Math.Sin(ang))));
        }

        return pts.MoveToImmutable();
    }

    public static string FormatToolShortBadge(VisionToolKind kind) => kind switch
    {
        VisionToolKind.ShapePatternMatch => "PatternMatch",
        VisionToolKind.DualRakeCornerAlign => "Corner2Rake",
        VisionToolKind.RleBlobAnalyzer => "BlobRle",
        VisionToolKind.BinaryPixelCounter => "BinaryCount",
        VisionToolKind.SubpixelCaliperMetrology => "CaliperMetro",
        VisionToolKind.GoldenSurfaceInspector => "GoldenDiff",
        VisionToolKind.GeometryMathCombiner => "GeometryMath",
        VisionToolKind.RelativeOffsetKinematics => "OffsetKin",
        VisionToolKind.ToleranceSpecJudge => "SpecJudge",
        VisionToolKind.FinalVerdictAggregator => "FinalVerdict",
        _ => "VisionTool"
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _patMaxEngine.Dispose();
        _particleWorkspace.Dispose();
        _workspace.Dispose();
    }
}
