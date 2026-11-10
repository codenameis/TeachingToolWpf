// File: tests/VisionAlgorithm.Tests/ToolBlock/ToolBlockPipelineTests.cs
using System.Numerics;
using VisionAlgorithm.OpenSrc.BlobRle;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.CornerAlignment.Domain;
using VisionAlgorithm.OpenSrc.Curvilinear;
using VisionAlgorithm.OpenSrc.GoldenVariation;
using VisionAlgorithm.OpenSrc.IndustrialOcrOcv;
using VisionAlgorithm.OpenSrc.Metrology;
using VisionAlgorithm.OpenSrc.PatchAnomaly;
using VisionAlgorithm.OpenSrc.PatMax;
using VisionAlgorithm.OpenSrc.ToolBlock;

namespace VisionAlgorithm.Tests.ToolBlock;

public sealed class ToolBlockPipelineTests
{
    [Fact]
    public void ScenarioPipeline_EndToEnd_FindsPart_FixturedBlob_ComputesOffsets_AndJudgesOk()
    {
        using var pipeline = new ToolBlockScenarioPipeline(
            distanceLslMm: 2.75,
            distanceUslMm: 3.25,
            blobAreaLslPx: 600.0,
            blobAreaUslPx: 1050.0,
            maxRelativeOffsetMm: 0.25,
            mmPerPixel: 0.05);

        int width = ToolBlockScenarioPipeline.DefaultImageWidth;
        int height = ToolBlockScenarioPipeline.DefaultImageHeight;
        byte[] pixels = new byte[width * height];

        // Part rotated by +12 degrees at (150, 130), with a nominal solder pad blob
        float angleRad = 12.0f * (MathF.PI / 180.0f);
        ToolBlockScenarioPipeline.RenderSyntheticInspectionImage(
            pixels,
            width,
            height,
            partCenterX: 150.0f,
            partCenterY: 130.0f,
            partAngleRad: angleRad,
            blobShiftLocalX: 1.0f,
            blobShiftLocalY: -1.0f,
            blobRadiusPixels: 16.0f);

        var imageView = new ReadOnlyImageView2D<byte>(pixels, width, height);
        ToolBlockScenarioResult result = pipeline.Execute(imageView);

        Assert.True(result.PatMaxFound);
        Assert.True(result.BlobFound);
        Assert.InRange(result.MeasuredDistanceMm, 2.85, 3.20);
        Assert.True(result.IsDistanceSpecOk);
        Assert.True(result.IsBlobAreaSpecOk);
        Assert.True(result.IsOffsetSpecOk);
        Assert.Equal(FinalDecision.Ok, result.FinalVerdict.Decision);
        Assert.Equal(0, result.FinalVerdict.FirstFailedNodeId);
        Assert.True(result.FinalVerdict.HasValidAlignment);
    }

    [Fact]
    public void ScenarioPipeline_ExcessiveBlobShift_TriggersNgVerdict_AndReportsFailedJudgeNodeId()
    {
        using var pipeline = new ToolBlockScenarioPipeline(
            distanceLslMm: 2.85,
            distanceUslMm: 3.15,
            blobAreaLslPx: 600.0,
            blobAreaUslPx: 1050.0,
            maxRelativeOffsetMm: 0.15,
            mmPerPixel: 0.05);

        int width = ToolBlockScenarioPipeline.DefaultImageWidth;
        int height = ToolBlockScenarioPipeline.DefaultImageHeight;
        byte[] pixels = new byte[width * height];

        // Shift the blob by +12 pixels (+0.60 mm > 0.15 mm maxRelativeOffsetMm)
        ToolBlockScenarioPipeline.RenderSyntheticInspectionImage(
            pixels,
            width,
            height,
            partCenterX: 145.0f,
            partCenterY: 128.0f,
            partAngleRad: -8.0f * (MathF.PI / 180.0f),
            blobShiftLocalX: 12.0f,
            blobShiftLocalY: 0.0f,
            blobRadiusPixels: 16.0f);

        var imageView = new ReadOnlyImageView2D<byte>(pixels, width, height);
        ToolBlockScenarioResult result = pipeline.Execute(imageView);

        Assert.True(result.PatMaxFound);
        Assert.True(result.BlobFound);
        Assert.False(result.IsDistanceSpecOk);
        Assert.False(result.IsOffsetSpecOk);
        Assert.Equal(FinalDecision.Ng, result.FinalVerdict.Decision);
        Assert.Equal(ToolBlockScenarioPipeline.NodeIdDistanceJudge, result.FinalVerdict.FirstFailedNodeId);
    }

    [Fact]
    public void ToolBlockBuilder_DetectsCyclicDependency_AndCalipersFitLineAndCircle()
    {
        var builder = new ToolBlockBuilder();
        TerminalHandle<Point2D> hPtA = builder.AllocatePointSlot();
        TerminalHandle<Point2D> hPtB = builder.AllocatePointSlot();
        TerminalHandle<bool> hValidA = builder.AllocateBoolSlot();
        TerminalHandle<bool> hValidB = builder.AllocateBoolSlot();
        TerminalHandle<double> hDist = builder.AllocateScalarSlot();
        TerminalHandle<double> hDx = builder.AllocateScalarSlot();
        TerminalHandle<double> hDy = builder.AllocateScalarSlot();
        TerminalHandle<double> hAng = builder.AllocateScalarSlot();

        // Node 1 reads hPtA and writes hPtB; Node 2 reads hPtB and writes hPtA -> Cycle!
        var node1 = new GeometryCombinerNode(
            1, "Geo1", GeometryCombinerOperation.MidpointGenerator, 1.0, null,
            hPtA, hPtA, default, default, hValidA, hDist, hDx, hDy, hAng, hPtB);
        var node2 = new GeometryCombinerNode(
            2, "Geo2", GeometryCombinerOperation.MidpointGenerator, 1.0, null,
            hPtB, hPtB, default, default, hValidB, hDist, hDx, hDy, hAng, hPtA);

        builder.AddNode(node1).AddNode(node2);
        bool compiled = builder.TryBuildPlan(out _, out ToolBlockCompileStatus status, out _);

        Assert.False(compiled);
        Assert.Equal(ToolBlockCompileStatus.CycleDetected, status);

        // Verify CaliperLineCircleToolNode SpokeCircleFit in a valid 1-node ToolBlock
        var circleBuilder = new ToolBlockBuilder();
        TerminalHandle<bool> hFound = circleBuilder.AllocateBoolSlot();
        TerminalHandle<Point2D> hCenter = circleBuilder.AllocatePointSlot();
        TerminalHandle<Line2D> hLine = circleBuilder.AllocateLineSlot();
        TerminalHandle<Circle2D> hCircle = circleBuilder.AllocateCircleSlot();
        TerminalHandle<double> hRadius = circleBuilder.AllocateScalarSlot();

        var caliperSpec = new CaliperLineCircleToolSpec(
            mode: CaliperMetroMode.SpokeCircleFit,
            localRoiOffset: new FixturePose2D(64f, 64f, 0f, 1f),
            scanLengthPixels: 24f,
            projectionWidthPixels: 5f,
            firstPolarity: CaliperEdgePolarity.LightToDark,
            secondPolarity: CaliperEdgePolarity.Any,
            caliperSpec: CaliperMeasurementSpec.Default,
            sampleCount: 16,
            nominalRadiusOrSpanPixels: 22f);

        var circleNode = new CaliperLineCircleToolNode(
            100, "SpokeCircleTool", in caliperSpec,
            inFixture: TerminalHandle<FixturePose2D>.Unbound,
            outFound: hFound,
            outEdgeOrCenter: hCenter,
            outFittedLine: hLine,
            outFittedCircle: hCircle,
            outMeasuredWidthOrRadius: hRadius);

        circleBuilder.AddNode(circleNode);
        ToolBlockRunner circleRunner = circleBuilder.BuildPlan();
        ToolBlockContext circleCtx = circleBuilder.CreateContext();
        using var ws = new VisionWorkspace(128 * 128);

        byte[] diskImg = new byte[128 * 128];
        ToolBlockScenarioPipeline.RenderSyntheticInspectionImage(
            diskImg, 128, 128,
            partCenterX: 4f, partCenterY: 64f, partAngleRad: 0f,
            blobShiftLocalX: 0f, blobShiftLocalY: 0f, blobRadiusPixels: 22f,
            omitPartFiducial: true, omitBlob: false);

        circleRunner.Execute(new ReadOnlyImageView2D<byte>(diskImg, 128, 128), ws, circleCtx);

        Assert.True(circleCtx.ReadBoolSlot(hFound).Value);
        SlotValue<Circle2D> fittedCircle = circleCtx.ReadCircleSlot(hCircle);
        Assert.True(fittedCircle.IsValid);
        Assert.InRange(fittedCircle.Value.Radius, 20.5, 23.5);
    }

    [Fact]
    public void CornerAlignmentToolNode_FindsVirtualCorner_AndFeedsChildMetrologyPose()
    {
        int width = 160;
        int height = 160;
        byte[] pixels = new byte[width * height];

        // Render a bright rectangle body [40..120, 45..125] with dark background
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool insideRect = x >= 40 && x <= 120 && y >= 45 && y <= 125;
                pixels[y * width + x] = insideRect ? (byte)220 : (byte)25;
            }
        }

        var builder = new ToolBlockBuilder();
        TerminalHandle<bool> hCornerFound = builder.AllocateBoolSlot();
        TerminalHandle<FixturePose2D> hCornerPose = builder.AllocatePoseSlot();
        TerminalHandle<Point2D> hCornerPt = builder.AllocatePointSlot();
        TerminalHandle<Line2D> hLine1 = builder.AllocateLineSlot();
        TerminalHandle<Line2D> hLine2 = builder.AllocateLineSlot();
        TerminalHandle<double> hIncludedDeg = builder.AllocateScalarSlot();

        CornerAlignmentToolSpec cornerSpec = CornerAlignmentToolSpec.CreateFromRoiBounds(
            roiX: 28f,
            roiY: 32f,
            roiWidth: 72f,
            roiHeight: 72f,
            searchLengthPixels: 28f,
            caliperCountPerLeg: 12);

        var cornerNode = new CornerAlignmentToolNode(
            nodeId: 201,
            nodeName: "CogCornerAlign1",
            in cornerSpec,
            outFound: hCornerFound,
            outPose: hCornerPose,
            outCorner: hCornerPt,
            outLine1: hLine1,
            outLine2: hLine2,
            outIncludedAngleDeg: hIncludedDeg);

        builder.AddNode(cornerNode);
        ToolBlockRunner runner = builder.BuildPlan();
        ToolBlockContext ctx = builder.CreateContext();
        using var ws = new VisionWorkspace(width * height);

        runner.Execute(new ReadOnlyImageView2D<byte>(pixels, width, height), ws, ctx);

        Assert.True(ctx.ReadBoolSlot(hCornerFound).Value);
        SlotValue<Point2D> cornerPt = ctx.ReadPointSlot(hCornerPt);
        Assert.True(cornerPt.IsValid);
        Assert.InRange(cornerPt.Value.X, 38.5, 41.5);
        Assert.InRange(cornerPt.Value.Y, 43.5, 46.5);
        Assert.InRange(ctx.ReadScalarSlot(hIncludedDeg).Value, 88.0, 92.0);
    }

    [Fact]
    public void PatMaxEngine_FindBestAcrossModels_ReportsWinningTemplateIndex_RunnerUp_AndSpcReplayEnvelope()
    {
        int width = 160;
        int height = 160;
        byte[] tpl0Bytes = new byte[64 * 64]; // Cross fiducial
        byte[] tpl1Bytes = new byte[64 * 64]; // L-shaped fiducial

        // Template #0 (ID 101): Full hollow square frame (partially matches the notched square)
        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                bool outer = x >= 12 && x <= 52 && y >= 12 && y <= 52;
                bool inner = x >= 22 && x <= 42 && y >= 22 && y <= 42;
                tpl0Bytes[y * 64 + x] = (outer && !inner) ? (byte)235 : (byte)20;
            }
        }

        // Template #1 (ID 202): Hollow square with distinct top-left notch + center fiducial dot (exact match)
        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                bool outer = x >= 12 && x <= 52 && y >= 12 && y <= 52;
                bool inner = x >= 22 && x <= 42 && y >= 22 && y <= 42;
                bool notch = x >= 12 && x <= 26 && y >= 12 && y <= 26;
                bool centerDot = x >= 29 && x <= 35 && y >= 29 && y <= 35;
                tpl1Bytes[y * 64 + x] = ((outer && !inner && !notch) || centerDot) ? (byte)235 : (byte)20;
            }
        }

        using var engine = new PatMaxEngine();
        PatMaxTrainParams trainParams = PatMaxTrainParams.Default;

        using PatMaxModel model0 = PatMaxTrainer.Train(tpl0Bytes, 64, 64, 64, in trainParams);
        using PatMaxModel model1 = PatMaxTrainer.Train(tpl1Bytes, 64, 64, 64, in trainParams);

        // Runtime scene contains Template #1 placed at (84, 78)
        byte[] scene = new byte[width * height];
        Array.Fill(scene, (byte)20);
        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                int sx = (84 - 32) + x;
                int sy = (78 - 32) + y;
                scene[sy * width + sx] = tpl1Bytes[y * 64 + x];
            }
        }

        PatMaxModel[] models = [model0, model1];
        int[] modelIds = [101, 202];
        PatMaxSearchParams searchParams = PatMaxSearchParams.Create(
            acceptThreshold: 0.45f,
            angleStartRadians: -0.25f,
            angleExtentRadians: 0.50f);

        PatMaxResult match = engine.FindBestAcrossModels(
            models,
            modelIds,
            scene,
            width,
            height,
            width,
            in searchParams);

        Assert.True(match.IsValid);
        Assert.Equal(1, match.MatchedTemplateIndex);
        Assert.Equal(202, match.MatchedTemplateId);
        Assert.Equal(2, match.TotalTemplateCount);
        Assert.Equal(0, match.RunnerUpTemplateIndex);
        Assert.True(match.Score > match.RunnerUpScore);
        Assert.True(match.TemplateSeparationMargin > 0.01f);
        Assert.True(match.MeanEdgeContrast > 10.0f);
        Assert.True(match.ModelDipoleCount > 0);
        Assert.Equal(64, match.PatternWidth);
        Assert.Equal(64, match.PatternHeight);

        VisionInspectionEnvelope env = ((IVisionToolResult)match).ToInspectionEnvelope(nodeId: 10, specLsl: 0.50, specUsl: 1.00);
        Assert.Equal(VisionToolCategory.PatternMatching, env.Category);
        Assert.True(env.IsFound);
        Assert.True(env.IsPass);
        Assert.Equal(1, env.Spc.MatchedModelIndex);
        Assert.Equal(202, env.Spc.MatchedModelId);
        Assert.Equal(2, env.Spc.TotalModelCount);
        Assert.Equal(0, env.Spc.RunnerUpModelIndex);
        Assert.Equal(ReplayShapeKind.OrientedBox, env.Replay.ShapeKind);
        Assert.Contains("Tpl#1", env.Replay.SummaryLabel);
    }

    [Fact]
    public void BinaryPixelCountToolNode_AndAllAlgorithmResults_ExportUnifiedSpcAndReplayEnvelope()
    {
        int width = 128;
        int height = 128;
        byte[] pixels = new byte[width * height];
        Array.Fill(pixels, (byte)30);

        // Draw a 20x15 bright rectangular pad inside [50..69, 55..69] -> 300 bright pixels
        for (int y = 55; y < 70; y++)
        {
            for (int x = 50; x < 70; x++)
            {
                pixels[y * width + x] = 220;
            }
        }

        var builder = new ToolBlockBuilder();
        TerminalHandle<bool> hFound = builder.AllocateBoolSlot();
        TerminalHandle<bool> hPass = builder.AllocateBoolSlot();
        TerminalHandle<Point2D> hCom = builder.AllocatePointSlot();
        TerminalHandle<double> hCount = builder.AllocateScalarSlot();
        TerminalHandle<double> hRatio = builder.AllocateScalarSlot();
        TerminalHandle<double> hMeanGray = builder.AllocateScalarSlot();

        var binarySpec = new BinaryPixelCountSpec(
            localRoiOffset: new FixturePose2D(60f, 62f, 0f, 1f),
            roiWidth: 40,
            roiHeight: 30,
            minGrayThreshold: 128,
            maxGrayThreshold: 255,
            minPassAreaPercent: 20.0f,
            maxPassAreaPercent: 30.0f,
            conditionIndex: 0,
            conditionId: 501);

        var binaryNode = new BinaryPixelCountToolNode(
            nodeId: 301,
            nodeName: "CogBinaryPixelCount1",
            in binarySpec,
            inFixture: TerminalHandle<FixturePose2D>.Unbound,
            outFound: hFound,
            outIsPass: hPass,
            outCenterOfMass: hCom,
            outValidPixelCount: hCount,
            outAreaRatioPercent: hRatio,
            outMeanGray: hMeanGray);

        builder.AddNode(binaryNode);
        ToolBlockRunner runner = builder.BuildPlan();
        ToolBlockContext ctx = builder.CreateContext();
        using var ws = new VisionWorkspace(width * height);

        runner.Execute(new ReadOnlyImageView2D<byte>(pixels, width, height), ws, ctx);

        Assert.True(ctx.ReadBoolSlot(hFound).Value);
        Assert.True(ctx.ReadBoolSlot(hPass).Value);
        Assert.InRange(ctx.ReadScalarSlot(hCount).Value, 285.0, 315.0);
        Assert.InRange(ctx.ReadScalarSlot(hRatio).Value, 23.0, 27.0);

        VisionInspectionEnvelope binEnv = ((IVisionToolResult)binaryNode.LastResult).ToInspectionEnvelope(nodeId: 301, specLsl: 20.0, specUsl: 30.0);
        Assert.Equal(VisionToolCategory.BinaryPixelCount, binEnv.Category);
        Assert.True(binEnv.IsPass);
        Assert.Equal(501, binEnv.Spc.MatchedModelId);
        Assert.True(binEnv.Spc.MeanGrayValue > 30.0f);
        Assert.Equal(ReplayShapeKind.OrientedBox, binEnv.Replay.ShapeKind);

        // Verify IVisionToolResult contract across Blob, Caliper, Circle, Corner, Variation, PaDiM, Bead, and OCR
        var caliperPair = new CaliperPairResult(
            IsFound: true,
            FirstEdge: new CaliperEdgeHit(10f, new Vector2(40f, 60f), 45f, CaliperEdgePolarity.DarkToLight),
            SecondEdge: new CaliperEdgeHit(30f, new Vector2(60f, 60f), 48f, CaliperEdgePolarity.LightToDark),
            Value: new SubpixelWidthMeasurement(20.0f, new Vector2(50f, 60f)));
        VisionInspectionEnvelope calEnv = ((IVisionToolResult)caliperPair).ToInspectionEnvelope(nodeId: 302, specLsl: 18.0, specUsl: 22.0);
        Assert.Equal(VisionToolCategory.SubpixelMetrology, calEnv.Category);
        Assert.Equal(ReplayShapeKind.EdgePair, calEnv.Replay.ShapeKind);
        Assert.InRange(calEnv.Spc.SpecMarginRatio, 0.9f, 1.0f);

        var padimPass = new PadimAnomalyResult(
            IsAnomalous: false,
            ImageAnomalyScore: 1.8f,
            AnomalyThreshold: 3.5f,
            PeakAnomalyLocalPosition: new Vector2(64f, 64f),
            AnomalousPixelCount: 0);
        VisionInspectionEnvelope padimEnv = ((IVisionToolResult)padimPass).ToInspectionEnvelope(nodeId: 303);
        Assert.Equal(VisionToolCategory.AnomalyVariation, padimEnv.Category);
        Assert.True(padimEnv.IsPass); // Normalized polarity: !IsAnomalous => Pass

        var ocrRes = new OcrOcvLineResult(
            DecodedText: "A12",
            IsPass: true,
            DefectFlags: OcvDefectFlags.None,
            MinCharScore: 0.94f,
            MinConfusionMargin: 0.35f,
            MinOcvQualityScore: 0.89f,
            MinContrast: 110f,
            CharacterResults:
            [
                new CharVerifyResult(0, 'A', '4', 0.96f, 0.50f, 0.46f, 0.95f, 120f, 0.90f, 0.1f, -0.1f, OcvDefectFlags.None, true),
                new CharVerifyResult(1, '1', 'I', 0.94f, 0.55f, 0.39f, 0.92f, 115f, 0.88f, 0.0f, 0.1f, OcvDefectFlags.None, true),
                new CharVerifyResult(2, '2', 'Z', 0.94f, 0.59f, 0.35f, 0.89f, 110f, 0.86f, -0.1f, 0.0f, OcvDefectFlags.None, true)
            ])
        {
            LineWorldPose = new FixturePose2D(64f, 32f, 0f, 1f),
            MatchedFontModelIndex = 2
        };
        VisionInspectionEnvelope ocrEnv = ((IVisionToolResult)ocrRes).ToInspectionEnvelope(nodeId: 304);
        Assert.Equal(VisionToolCategory.OcrVerification, ocrEnv.Category);
        Assert.Equal("A12", ocrEnv.Spc.DecodedText);
        Assert.Equal(2, ocrEnv.Spc.MatchedModelIndex);
        Assert.Equal(3, ocrEnv.Replay.SubRegions.Length);
    }

    [Fact]
    public void RoiHistogramAnalyzer_ComputesImageJStatistics_OtsuThreshold_And1DProfiles_WithPaddedStride()
    {
        int width = 33;
        int height = 25;
        int stride = width + 7; // Padded stride (40 != 33)
        byte[] buffer = new byte[height * stride];

        // Fill background with dark gray (40 GV) and a bright rectangular object (210 GV) in [8..22, 5..18]
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool isForeground = x >= 8 && x <= 22 && y >= 5 && y <= 18;
                buffer[(y * stride) + x] = isForeground ? (byte)210 : (byte)40;
            }
        }

        var imageView = new ReadOnlyImageView2D<byte>(buffer, width, height, stride);
        RoiHistogramAnalysis hist = RoiHistogramAnalyzer.AnalyzeRect(
            imageView,
            roiX: 4,
            roiY: 2,
            roiWidth: 24,
            roiHeight: 20,
            minThreshold: 125,
            maxThreshold: byte.MaxValue);

        Assert.Equal(480, hist.TotalPixelCount);
        Assert.Equal(40, hist.MinGray);
        Assert.Equal(210, hist.MaxGray);
        Assert.InRange(hist.OtsuThreshold, (byte)40, (byte)209);
        Assert.InRange(hist.IsoDataThreshold, (byte)40, (byte)209);
        Assert.InRange(hist.TriangleThreshold, (byte)40, (byte)210);
        Assert.InRange(hist.MaxEntropyThreshold, (byte)40, (byte)209);
        Assert.InRange(hist.MeanThreshold, (byte)100, (byte)130);
        Assert.Equal(15 * 14, hist.ThresholdedPixelCount); // 210 foreground pixels inside ROI
        Assert.InRange(hist.ThresholdedRatioPercent, 43.5f, 44.0f);
        Assert.InRange(hist.EntropyBits, 0.90f, 1.05f);
        Assert.Equal(24, hist.HorizontalProfile.Length);
        Assert.Equal(20, hist.VerticalProfile.Length);
        Assert.Equal(24, hist.HorizontalGradientProfile.Length);
        Assert.Equal(20, hist.VerticalGradientProfile.Length);
        Assert.True(hist.MaxHorizontalGradient > 10.0f);
        Assert.True(hist.MaxVerticalGradient > 10.0f);
    }

    [Fact]
    public void RoiImageProcessor_And_PreFilteredToolNodeDecorator_SupportImageJSpatialFilters_And_PixelProbe()
    {
        int width = 33;
        int height = 33;
        int stride = width + 5; // Non-multiple-of-4 padded stride
        byte[] buffer = new byte[height * stride];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool inSquare = x >= 8 && x <= 24 && y >= 8 && y <= 24;
                buffer[(y * stride) + x] = inSquare ? (byte)200 : (byte)30;
            }
        }

        // Inject a single salt-and-pepper noise impulse at center (16, 16)
        buffer[(16 * stride) + 16] = 0;

        var rawView = new ReadOnlyImageView2D<byte>(buffer, width, height, stride);

        // 1. Verify Pixel Probe at boundary (x=8, y=16)
        bool probed = RoiImageProcessor.TryProbePixel(rawView, 8, 16, out byte centerGv, out float mean3x3, out byte min3x3, out byte max3x3, out float grad);
        Assert.True(probed);
        Assert.Equal(200, centerGv);
        Assert.Equal(30, min3x3);
        Assert.Equal(200, max3x3);
        Assert.True(grad > 50.0f);

        // 2. Verify Median3x3 removes the salt-and-pepper impulse at (16, 16)
        ReadOnlyImageView2D<byte> medianView = RoiImageProcessor.ApplyRoiFilterIfNeeded(rawView, 8, 8, 18, 18, RoiPreFilterKind.Median3x3);
        Assert.Equal(200, medianView.Pixels[(16 * stride) + 16]);

        // 3. Verify Invert filter inside PreFilteredToolNodeDecorator
        var builder = new ToolBlockBuilder();
        TerminalHandle<bool> outFound = builder.AllocateBoolSlot();
        TerminalHandle<bool> outPass = builder.AllocateBoolSlot();
        TerminalHandle<Point2D> outCom = builder.AllocatePointSlot();
        TerminalHandle<double> outCount = builder.AllocateScalarSlot();
        TerminalHandle<double> outRatio = builder.AllocateScalarSlot();
        TerminalHandle<double> outMean = builder.AllocateScalarSlot();

        var binSpec = new BinaryPixelCountSpec(
            localRoiOffset: new FixturePose2D(16.0f, 16.0f, 0.0f, 1.0f),
            roiWidth: 13,
            roiHeight: 13,
            minGrayThreshold: 180,
            maxGrayThreshold: 255,
            minPassAreaPercent: 80.0f,
            maxPassAreaPercent: 100.0f);

        var innerNode = new BinaryPixelCountToolNode(
            1,
            "MedianFilteredBinaryCount",
            in binSpec,
            TerminalHandle<FixturePose2D>.Unbound,
            outFound,
            outPass,
            outCom,
            outCount,
            outRatio,
            outMean);

        var decoratedNode = new PreFilteredToolNodeDecorator(
            innerNode,
            RoiPreFilterKind.Median3x3,
            width,
            height,
            stride,
            roiX: 8,
            roiY: 8,
            roiWidth: 18,
            roiHeight: 18);

        builder.AddNode(decoratedNode);
        Assert.True(builder.TryBuildPlan(out ToolBlockRunner? runner, out _, out _));

        ToolBlockContext ctx = builder.CreateContext();
        using var ws = new VisionWorkspace(64 * 1024);
        runner!.Execute(rawView, ws, ctx);

        // Because Median3x3 removed the 0-value impulse at (16,16), all 13x13 = 169 pixels are 200 GV (100% occupancy)!
        Assert.Equal(169.0, ctx.ReadScalarSlot(outCount).Value, precision: 3);
        Assert.Equal(100.0, ctx.ReadScalarSlot(outRatio).Value, precision: 3);
    }
}
