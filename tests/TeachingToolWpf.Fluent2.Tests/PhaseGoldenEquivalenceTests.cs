using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using TeachingToolWpf.Imaging.Matching;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.DataCode2D;
using VisionAlgorithm.Verification;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionGeometry;
using VisionImaging;
using VisionImaging.Abstractions;
using VisionOcr.PpOcr;
using Xunit;
using Xunit.Abstractions;

namespace TeachingToolWpf.Fluent2.Tests;

public sealed class PhaseGoldenEquivalenceTests(ITestOutputHelper output)
{
    private const string ExpectedPreFilterSha256 = "F31D6C14E49BA6AFDF3E72999C6E500248D4A021458F8B164C0FD63F1E9F4C3B";
    private const string ExpectedCanvasUndoRedoSha256 = "42478A1D0830D2C5F228CB82C75A911001D9F66C96C61F2FE4AB0FB520E05862";
    private const string ExpectedSuiteSixAlgorithmsSha256 = "5B62A686B23D18640D1359D00F49C2382B5405552D457D240F4DE949C710D264";
    private const string ExpectedBarcodePixelsSha256 = "0C0D8EDFBE808A8B975DE0D65C17CD8DA932BB5655800989912019D75F422A80";
    private const string ExpectedDataMatrixPixelsSha256 = "A4B88CCB9C2C14C037A53A65F1F066E68044C1662F149A61C030FD23DEC2B6DF";
    private const string ExpectedSymbolDecodeSha256 = "B8EB438F88E7517A9DC4BCF4B2517A96150BD9F13D3EB26D0CA07A02169505B4";
    private const string ExpectedPhase2All18SuiteAlgorithmsSha256 = "B21E97252E4998E6DAB706E790C782FD5C2ECB6F786BFD2139D04530A110D1E8";
    private const double BaselinePhase2All18SuiteMedianMs = 914.413;
    private const double BaselinePreFilterMedianMs = 0.6876;

    [Fact]
    public void CaptureOrVerifyPhase1SymbolRendererGoldenHashes()
    {
        byte[] barcodeBuf = new byte[320 * 160];
        Array.Fill(barcodeBuf, (byte)240);
        Code128SymbolRenderer.RenderCode128Symbol(barcodeBuf, 320, 160, 320, "LOT26A-99", 24, 30, 96, moduleWidthPx: 2);
        Barcode1DDecodeResult barcodeRes = Barcode1DDecoder.DecodeMultiScanline(barcodeBuf, 320, 160, 320);

        byte[] dmBuf = new byte[220 * 220];
        Array.Fill(dmBuf, (byte)235);
        Ecc200SymbolRenderer.RenderEcc200Symbol("SN-2026-OK", dmBuf, 220, 220, 220, 36, 36, moduleSizePixels: 6);
        DataMatrixDecodeResult dmRes = DataMatrixEcc200Decoder.Decode(new ReadOnlyImageView2D<byte>(dmBuf, 220, 220, 220));

        string barcodeSha = Convert.ToHexString(SHA256.HashData(barcodeBuf));
        string dmSha = Convert.ToHexString(SHA256.HashData(dmBuf));
        string summarySha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{barcodeRes.IsDecoded}|{barcodeRes.Payload}|{barcodeRes.ChecksumValue}|{barcodeRes.OverallGradeScore:R}||" +
            $"{dmRes.IsDecoded}|{dmRes.DecodedText}|{dmRes.CenterWorld.X:R},{dmRes.CenterWorld.Y:R}|{dmRes.CorrectedByteErrors}")));

        output.WriteLine($"GOLDEN_BARCODE_PIXELS_SHA256={barcodeSha}");
        output.WriteLine($"GOLDEN_DATAMATRIX_PIXELS_SHA256={dmSha}");
        output.WriteLine($"GOLDEN_SYMBOL_DECODE_SHA256={summarySha}");

        Assert.True(barcodeRes.IsDecoded);
        Assert.Equal("LOT26A-99", barcodeRes.Payload);
        Assert.True(dmRes.IsDecoded);
        Assert.Equal("SN-2026-OK", dmRes.DecodedText);
        Assert.Equal(ExpectedBarcodePixelsSha256, barcodeSha);
        Assert.Equal(ExpectedDataMatrixPixelsSha256, dmSha);
        Assert.Equal(ExpectedSymbolDecodeSha256, summarySha);
    }

    [Fact]
    public async Task CaptureOrVerifyPhase0GoldenHashesAndLatency()
    {
        var steps = ImmutableArray.Create(
            new PreInspectionFilterStep(PreInspectionFilterKind.Clahe, true, 128.0, 40.0, 120.0, 2, 2.0),
            new PreInspectionFilterStep(PreInspectionFilterKind.Gaussian, true, 128.0, 40.0, 120.0, 2, 1.5),
            new PreInspectionFilterStep(PreInspectionFilterKind.Binarize, true, 120.0, 40.0, 120.0, 1, 1.0));

        var pipeline = new PreInspectionFilterPipeline();
        pipeline.SetActivePipeline(steps);

        const int width = 256;
        const int height = 256;
        byte[] seed = new byte[width * height];
        for (int i = 0; i < seed.Length; i++)
        {
            seed[i] = (byte)(((i * 73) + ((i >> 8) * 31) + 19) & 0xFF);
        }

        byte[] work = (byte[])seed.Clone();
        int applied = pipeline.ApplyInPlaceIfConfigured(work.AsSpan(), width, height);
        Assert.Equal(3, applied);
        string filterSha = Convert.ToHexString(SHA256.HashData(work));

        for (int k = 0; k < 5; k++)
        {
            byte[] warm = (byte[])seed.Clone();
            pipeline.ApplyInPlaceIfConfigured(warm.AsSpan(), width, height);
        }

        var times = new List<double>(21);
        for (int k = 0; k < 21; k++)
        {
            byte[] buf = (byte[])seed.Clone();
            long t0 = Stopwatch.GetTimestamp();
            pipeline.ApplyInPlaceIfConfigured(buf.AsSpan(), width, height);
            times.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
        }

        times.Sort();
        double medianMs = times[times.Count / 2];

        var session = new EditorSession();
        var imageRef = new ImageReference("sample:1024:768", "sample", 1024, 768);
        session.InitializeImage(imageRef, session.State.Viewport);
        Guid id1 = session.Add(new RoiGeometry(new PointD(200, 180), 120, 80, 0.25));
        Guid id2 = session.Add(new RoiGeometry(new PointD(520, 360), 90, 90, 0.0));
        session.Select([id1, id2]);
        session.TranslateSelection(new PointD(15.5, -8.25));
        string afterEdit = SnapshotString(session.State);
        session.Undo();
        string afterUndo = SnapshotString(session.State);
        session.Redo();
        string afterRedo = SnapshotString(session.State);
        string canvasSha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{afterEdit}||{afterUndo}||{afterRedo}")));

        var suiteLoader = new DeterministicTileLoader(320, 240);
        var suiteService = new InspectionSuiteService(suiteLoader, () => null);
        var suiteImgRef = new ImageReference("mem:320x240", "mem", 320, 240);
        var roiInput = new InspectionSuiteRoiInput(
            id1,
            "TraceRoi",
            new PointD(160, 120),
            120,
            80,
            0.0,
            [new PointD(100, 80), new PointD(220, 80), new PointD(220, 160), new PointD(100, 160)]);
        var suiteParameters = new InspectionSuiteParameters();
        var sb = new StringBuilder();
        foreach (var kind in new[]
        {
            InspectionSuiteAlgorithmKind.Metrology,
            InspectionSuiteAlgorithmKind.BlobRle,
            InspectionSuiteAlgorithmKind.Curvilinear,
            InspectionSuiteAlgorithmKind.Calibration,
            InspectionSuiteAlgorithmKind.CornerAlignment,
            InspectionSuiteAlgorithmKind.PreprocessingShadingAndRecon
        })
        {
            InspectionSuiteExecutionOutput outRes = await suiteService.ExecuteRoiInspectionAsync(
                suiteImgRef, false, kind, [roiInput], [], suiteParameters, CancellationToken.None);
            foreach (var item in outRes.Items)
            {
                sb.Append(kind).Append('|').Append(item.IsPass).Append('|').Append(item.BadgeLabel).Append('|').Append(item.DetailLine)
                  .Append('|').Append(item.ReferencePoint.X.ToString("R")).Append(',').Append(item.ReferencePoint.Y.ToString("R"))
                  .Append('|').Append(item.AdditionalContours.Length).Append('|').Append(item.PrimaryPoints.Length).AppendLine();
            }
        }

        string suiteSha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));

        output.WriteLine($"GOLDEN_PREFILTER_SHA256={filterSha}");
        output.WriteLine($"GOLDEN_PREFILTER_MEDIAN_MS={medianMs:F4} (Baseline={BaselinePreFilterMedianMs:F4} ms)");
        output.WriteLine($"GOLDEN_CANVAS_SHA256={canvasSha}");
        output.WriteLine($"GOLDEN_SUITE6_SHA256={suiteSha}");

        Assert.Equal(ExpectedPreFilterSha256, filterSha);
        Assert.Equal(ExpectedCanvasUndoRedoSha256, canvasSha);
        Assert.Equal(ExpectedSuiteSixAlgorithmsSha256, suiteSha);
    }

    [Fact]
    public async Task ParallelPreInspectionFilterPipelines_NeverInterfereAcrossInstances()
    {
        var pipelineBinarizeLow = new PreInspectionFilterPipeline();
        pipelineBinarizeLow.SetActivePipeline(
        [
            new PreInspectionFilterStep(PreInspectionFilterKind.Binarize, true, 40.0, 0.0, 0.0, 1, 1.0)
        ]);

        var pipelineBinarizeHigh = new PreInspectionFilterPipeline();
        pipelineBinarizeHigh.SetActivePipeline(
        [
            new PreInspectionFilterStep(PreInspectionFilterKind.Binarize, true, 220.0, 0.0, 0.0, 1, 1.0)
        ]);

        const int width = 64;
        const int height = 64;
        byte[] uniformMidGray = new byte[width * height];
        Array.Fill(uniformMidGray, (byte)128);

        Task[] workers = Enumerable.Range(0, 64).Select(workerIndex => Task.Run(() =>
        {
            for (int iter = 0; iter < 30; iter++)
            {
                byte[] buffer = (byte[])uniformMidGray.Clone();
                if ((workerIndex & 1) == 0)
                {
                    int steps = pipelineBinarizeLow.ApplyInPlaceIfConfigured(buffer, width, height);
                    Assert.Equal(1, steps);
                    Assert.All(buffer, static b => Assert.Equal(255, b));
                }
                else
                {
                    int steps = pipelineBinarizeHigh.ApplyInPlaceIfConfigured(buffer, width, height);
                    Assert.Equal(1, steps);
                    Assert.All(buffer, static b => Assert.Equal(0, b));
                }
            }
        })).ToArray();

        await Task.WhenAll(workers);
    }

    [Fact]
    public async Task RunMaster8kSuiteAsync_RealPath_ProducesAll27MarkersWithoutMoveToImmutableException()
    {
        var loader = new DeterministicTileLoader(320, 240);
        var service = new InspectionSuiteService(loader, () => null, master8kSuiteRunner: new Master8kSuiteVerificationRunner());

        InspectionSuiteExecutionOutput result = await service.RunMaster8kSuiteCoreAsync(
            CancellationToken.None,
            unifiedSheetWidth: 5120,
            unifiedSheetHeight: 1152);

        Assert.Equal(27, result.Items.Length);
        Assert.All(result.Items, static item => Assert.True(item.IsPass, $"Failed item: {item.Name} -> {item.DetailLine}"));
        Assert.DoesNotContain("경량OCR", result.SummaryTitle, StringComparison.Ordinal);
        Assert.Contains("티칭OCV", result.SummaryTitle, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAutoTeaching_BlankSceneWithoutInstances_DoesNotThrowMoveToImmutableException()
    {
        var loader = new UniformTileLoader(320, 240, 128);
        var service = new InspectionSuiteService(loader, () => null);
        var imgRef = new ImageReference("blank:320x240", "blank", 320, 240);
        var roi = new InspectionSuiteRoiInput(
            Guid.NewGuid(),
            "EmptyRoi",
            new PointD(160, 120),
            120,
            80,
            0.0,
            [new PointD(100, 80), new PointD(220, 80), new PointD(220, 160), new PointD(100, 160)]);

        InspectionSuiteExecutionOutput outputResult = await service.ExecuteRoiInspectionAsync(
            imgRef,
            live: false,
            InspectionSuiteAlgorithmKind.AutoTeaching,
            targetRois: [roi],
            referenceRois: [],
            new InspectionSuiteParameters(),
            CancellationToken.None);

        Assert.NotEmpty(outputResult.Items);
    }

    [Fact]
    public async Task CaptureOrVerifyPhase2All18SuiteAlgorithmsGoldenHashesAndLatency()
    {
        var loader = new SuiteSampleTileLoader();
        using var stubPpOcr = new DeterministicStubPpOcrService();
        var service = new InspectionSuiteService(loader, () => null, ppOcrService: stubPpOcr);
        var parameters = new InspectionSuiteParameters();
        var sb = new StringBuilder(65536);

        InspectionSuiteAlgorithmKind[] activeKinds =
        [
            InspectionSuiteAlgorithmKind.Metrology,
            InspectionSuiteAlgorithmKind.GoldenVariation,
            InspectionSuiteAlgorithmKind.BlobRle,
            InspectionSuiteAlgorithmKind.Curvilinear,
            InspectionSuiteAlgorithmKind.FewShot,
            InspectionSuiteAlgorithmKind.PatchAnomaly,
            InspectionSuiteAlgorithmKind.Calibration,
            InspectionSuiteAlgorithmKind.CornerAlignment,
            InspectionSuiteAlgorithmKind.AutoTeaching,
            InspectionSuiteAlgorithmKind.PreprocessingShadingAndRecon,
            InspectionSuiteAlgorithmKind.AdvancedMetrologyEllipse,
            InspectionSuiteAlgorithmKind.AdvancedBlobWatershed,
            InspectionSuiteAlgorithmKind.DataMatrix2DReader,
            InspectionSuiteAlgorithmKind.PreprocessingFftAndGabor,
            InspectionSuiteAlgorithmKind.Barcode1DReader,
            InspectionSuiteAlgorithmKind.PhotometricStereo,
            InspectionSuiteAlgorithmKind.GeometricTolerancing,
            InspectionSuiteAlgorithmKind.PpOcrOnnx
        ];

        long totalElapsedTicks = 0;
        foreach (InspectionSuiteAlgorithmKind kind in activeKinds)
        {
            (string slug, List<InspectionSuiteRoiInput> targets, List<InspectionSuiteRoiInput> references) = BuildPresetSuiteCase(kind);
            var imgRef = new ImageReference(
                $"{InspectionSuiteSampleTileSource.KeyPrefix}{slug}",
                $"sample-{slug}",
                InspectionSuiteSampleTileSource.DefaultWidth,
                InspectionSuiteSampleTileSource.DefaultHeight);

            long t0 = Stopwatch.GetTimestamp();
            InspectionSuiteExecutionOutput execOut = await service.ExecuteRoiInspectionAsync(
                imgRef,
                live: false,
                kind,
                targets,
                references,
                parameters,
                CancellationToken.None);
            totalElapsedTicks += Stopwatch.GetTimestamp() - t0;

            int segStart = sb.Length;
            AppendExecutionCanonical(sb, service.Registry, kind, execOut, targets, parameters);
            string kindSha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString(segStart, sb.Length - segStart))));
            output.WriteLine($"  KIND_SHA[{kind}]={kindSha[..16]} | SHORT=\"{service.Registry.GetShortName(kind)}\"");
        }

        double totalMs = totalElapsedTicks * 1000.0 / Stopwatch.Frequency;
        string all18Sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
        output.WriteLine($"GOLDEN_PHASE2_ALL18_SHA256={all18Sha}");
        output.WriteLine($"GOLDEN_PHASE2_ALL18_TOTAL_MS={totalMs:F3} (Baseline={BaselinePhase2All18SuiteMedianMs:F3} ms)");

        Assert.Equal(ExpectedPhase2All18SuiteAlgorithmsSha256, all18Sha);
    }

    [Fact]
    public void LegacyFlatAndNestedSuiteParametersJsonRoundTripEquivalence()
    {
        const string legacyFlatJson = """
            {
              "GaussianSigma": 1.65,
              "MinimumContrast": 24.5,
              "ThresholdIntensity": 142,
              "MorphologyRadius": 2,
              "MetrologyMode": "SpokeCircleFit",
              "PolarityMode": "DarkSlot",
              "CaliperRakeCount": 21,
              "CaliperProjectionWidth": 13.0,
              "BlobMinimumAreaPixels": 19,
              "BarcodeScanlineCount": 13,
              "BarcodeMinSymbolContrast": 0.31,
              "PpOcrMinConfidence": 0.82,
              "PpOcrPreset": "HighSpeedProduction"
            }
            """;

        InspectionSuiteParameters fromLegacy = InspectionSuiteParameters.FromJson(legacyFlatJson);
        Assert.Equal(1.65f, fromLegacy.Metrology.GaussianSigma);
        Assert.Equal(24.5f, fromLegacy.Metrology.MinimumContrast);
        Assert.Equal(MetrologyMeasurementMode.SpokeCircleFit, fromLegacy.Metrology.Mode);
        Assert.Equal(MetrologyPolarityMode.DarkSlot, fromLegacy.Metrology.PolarityMode);
        Assert.Equal(21, fromLegacy.Metrology.CaliperRakeCount);
        Assert.Equal(142, fromLegacy.Blob.ThresholdIntensity);
        Assert.Equal(2, fromLegacy.Blob.MorphologyRadius);
        Assert.Equal(19, fromLegacy.Blob.MinimumAreaPixels);
        Assert.Equal(13, fromLegacy.Barcode.ScanlineCount);
        Assert.Equal(0.31f, fromLegacy.Barcode.MinSymbolContrast);
        Assert.Equal(0.82f, fromLegacy.PpOcr.MinConfidence);
        Assert.Equal(PpOcrSpeedPreset.HighSpeedProduction, fromLegacy.PpOcr.Preset);

        string modernNestedJson = fromLegacy.ToJson();
        InspectionSuiteParameters fromModern = InspectionSuiteParameters.FromJson(modernNestedJson);
        Assert.Equal(fromLegacy, fromModern);
    }

    [Fact]
    public async Task LegacyRoiModeMigrationPreservesExecutionWhenRoisAreRenamed()
    {
        var loader = new SuiteSampleTileLoader();
        using var ppOcr = new DeterministicStubPpOcrService();
        var service = new InspectionSuiteService(loader, () => null, ppOcrService: ppOcr);
        var parameters = new InspectionSuiteParameters();

        InspectionSuiteAlgorithmKind[] modeSensitiveKinds =
        [
            InspectionSuiteAlgorithmKind.Metrology,
            InspectionSuiteAlgorithmKind.PreprocessingShadingAndRecon,
            InspectionSuiteAlgorithmKind.AdvancedMetrologyEllipse,
            InspectionSuiteAlgorithmKind.AdvancedBlobWatershed,
            InspectionSuiteAlgorithmKind.PreprocessingFftAndGabor
        ];

        foreach (InspectionSuiteAlgorithmKind kind in modeSensitiveKinds)
        {
            (string slug, List<InspectionSuiteRoiInput> legacyNamedTargets, List<InspectionSuiteRoiInput> references) = BuildPresetSuiteCase(kind);
            var migratedRenamedTargets = legacyNamedTargets
                .Select((roi, idx) => new InspectionSuiteRoiInput(
                    roi.RoiId,
                    $"AnonRoi_{idx + 1}",
                    roi.Center,
                    roi.Width,
                    roi.Height,
                    roi.AngleRadians,
                    roi.Outline,
                    roi.ClassLabel,
                    ExplicitExecutionMode: LegacyRoiModeMigration.FromRoiName(roi.Name)))
                .ToList();

            var imgRef = new ImageReference(
                $"{InspectionSuiteSampleTileSource.KeyPrefix}{slug}",
                $"sample-{slug}",
                InspectionSuiteSampleTileSource.DefaultWidth,
                InspectionSuiteSampleTileSource.DefaultHeight);

            InspectionSuiteExecutionOutput legacyOut = await service.ExecuteRoiInspectionAsync(
                imgRef,
                live: false,
                kind,
                legacyNamedTargets,
                references,
                parameters,
                CancellationToken.None);
            InspectionSuiteExecutionOutput renamedOut = await service.ExecuteRoiInspectionAsync(
                imgRef,
                live: false,
                kind,
                migratedRenamedTargets,
                references,
                parameters,
                CancellationToken.None);

            Assert.Equal(legacyOut.Items.Length, renamedOut.Items.Length);
            for (int i = 0; i < legacyOut.Items.Length; i++)
            {
                Assert.Equal(legacyOut.Items[i].IsPass, renamedOut.Items[i].IsPass);
                Assert.Equal(legacyOut.Items[i].BadgeLabel, renamedOut.Items[i].BadgeLabel);
                Assert.Equal(legacyOut.Items[i].DetailLine, renamedOut.Items[i].DetailLine);
                Assert.Equal(legacyOut.Items[i].ReferencePoint, renamedOut.Items[i].ReferencePoint);
                Assert.Equal(legacyOut.Items[i].AngleRadians, renamedOut.Items[i].AngleRadians);
                Assert.Equal(legacyOut.Items[i].PrimaryPoints.AsEnumerable(), renamedOut.Items[i].PrimaryPoints.AsEnumerable());
                Assert.Equal(legacyOut.Items[i].SecondaryPoints.AsEnumerable(), renamedOut.Items[i].SecondaryPoints.AsEnumerable());
                Assert.Equal(legacyOut.Items[i].DefectOrOutlierPoints.AsEnumerable(), renamedOut.Items[i].DefectOrOutlierPoints.AsEnumerable());
            }
        }
    }

    private static void AppendExecutionCanonical(
        StringBuilder sb,
        InspectionAlgorithmRegistry registry,
        InspectionSuiteAlgorithmKind kind,
        InspectionSuiteExecutionOutput execOut,
        IReadOnlyList<InspectionSuiteRoiInput> targets,
        InspectionSuiteParameters parameters)
    {
        sb.Append("KIND=").Append(kind)
          .Append("|SHORT=").Append(registry.GetShortName(kind))
          .Append("|COUNT=").Append(execOut.Items.Length).AppendLine();

        for (int i = 0; i < execOut.Items.Length; i++)
        {
            InspectionSuiteItemResult item = execOut.Items[i];
            sb.Append("  ITEM[").Append(i).Append("]=")
              .Append(item.Name).Append('|')
              .Append(item.IsPass).Append('|')
              .Append(item.BadgeLabel).Append('|')
              .Append(item.DetailLine).Append('|')
              .Append(item.ReferencePoint.X.ToString("R")).Append(',').Append(item.ReferencePoint.Y.ToString("R")).Append('|')
              .Append(item.AngleRadians.ToString("R")).Append('|');
            AppendPoints(sb, item.Bounds);
            sb.Append('|');
            AppendNestedContours(sb, item.AdditionalContours);
            sb.Append('|');
            AppendPoints(sb, item.PrimaryPoints);
            sb.Append('|');
            AppendPoints(sb, item.SecondaryPoints);
            sb.Append('|');
            AppendPoints(sb, item.DefectOrOutlierPoints);
            sb.Append('|');
            AppendNestedContours(sb, item.GuideContours.IsDefault ? [] : item.GuideContours);
            sb.Append('|').Append(item.OcrDiagnostic is not null ? item.OcrDiagnostic.RecognizedText : "NULL")
              .AppendLine();
        }

        for (int i = 0; i < targets.Count; i++)
        {
            ImmutableArray<ImmutableArray<PointD>> guides = registry.BuildGeometricGuides(targets[i], kind, parameters);
            sb.Append("  GUIDE[").Append(i).Append("]=");
            AppendNestedContours(sb, guides);
            sb.AppendLine();
        }
    }

    private static void AppendPoints(StringBuilder sb, ImmutableArray<PointD> points)
    {
        sb.Append(points.Length).Append(':');
        for (int i = 0; i < points.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(';');
            }

            sb.Append(points[i].X.ToString("R")).Append(',').Append(points[i].Y.ToString("R"));
        }
    }

    private static void AppendNestedContours(StringBuilder sb, ImmutableArray<ImmutableArray<PointD>> contours)
    {
        sb.Append(contours.Length).Append('{');
        for (int i = 0; i < contours.Length; i++)
        {
            if (i > 0)
            {
                sb.Append('/');
            }

            AppendPoints(sb, contours[i]);
        }

        sb.Append('}');
    }

    private static InspectionSuiteRoiInput MakePresetRoi(int index, double cx, double cy, double w, double h, double angleDeg, string name, int classLabel = 0)
    {
        var id = new Guid(index, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1);
        double rad = angleDeg * Math.PI / 180.0;
        double cos = Math.Cos(rad);
        double sin = Math.Sin(rad);
        double hw = w * 0.5;
        double hh = h * 0.5;
        PointD Corner(double lx, double ly) => new(cx + (cos * lx) - (sin * ly), cy + (sin * lx) + (cos * ly));
        ImmutableArray<PointD> outline =
        [
            Corner(-hw, -hh),
            Corner(hw, -hh),
            Corner(hw, hh),
            Corner(-hw, hh)
        ];
        return new InspectionSuiteRoiInput(id, name, new PointD(cx, cy), w, h, rad, outline, classLabel);
    }

    private static (string Slug, List<InspectionSuiteRoiInput> Targets, List<InspectionSuiteRoiInput> References) BuildPresetSuiteCase(InspectionSuiteAlgorithmKind kind)
    {
        var targets = new List<InspectionSuiteRoiInput>();
        var references = new List<InspectionSuiteRoiInput>();
        int seq = 1;

        string slug = kind switch
        {
            InspectionSuiteAlgorithmKind.Metrology => "metrology",
            InspectionSuiteAlgorithmKind.GoldenVariation => "golden",
            InspectionSuiteAlgorithmKind.BlobRle => "blob",
            InspectionSuiteAlgorithmKind.Curvilinear => "curvilinear",
            InspectionSuiteAlgorithmKind.FewShot => "fewshot",
            InspectionSuiteAlgorithmKind.PatchAnomaly => "padim",
            InspectionSuiteAlgorithmKind.Calibration => "calibration",
            InspectionSuiteAlgorithmKind.CornerAlignment => "corner",
            InspectionSuiteAlgorithmKind.AutoTeaching => "autoteach",
            InspectionSuiteAlgorithmKind.PpOcrOnnx => "ocrocv",
            InspectionSuiteAlgorithmKind.PreprocessingShadingAndRecon => "preprocessing",
            InspectionSuiteAlgorithmKind.AdvancedMetrologyEllipse => "ellipse",
            InspectionSuiteAlgorithmKind.AdvancedBlobWatershed => "watershed",
            InspectionSuiteAlgorithmKind.DataMatrix2DReader => "datamatrix",
            InspectionSuiteAlgorithmKind.PreprocessingFftAndGabor => "fftgabor",
            InspectionSuiteAlgorithmKind.Barcode1DReader => "barcode1d",
            InspectionSuiteAlgorithmKind.PhotometricStereo => "photometric",
            InspectionSuiteAlgorithmKind.GeometricTolerancing => "geomtolerance",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        switch (kind)
        {
            case InspectionSuiteAlgorithmKind.Metrology:
                targets.Add(MakePresetRoi(seq++, 320.35, 400.25, 130, 220, 10.0, "트레이스 폭·직선 계측"));
                targets.Add(MakePresetRoi(seq++, 820.40, 400.60, 230, 230, 0.0, "원형 패드 외경·진원도"));
                break;

            case InspectionSuiteAlgorithmKind.GoldenVariation:
                foreach (double gx in new[] { 200.2, 460.4, 720.1, 980.3 })
                {
                    references.Add(MakePresetRoi(seq++, gx, 230.25, 136, 136, 1.15, $"기준-정상다이-{seq}"));
                }

                targets.Add(MakePresetRoi(seq++, 250.25, 570.30, 136, 136, 1.15, "검사-정상다이"));
                targets.Add(MakePresetRoi(seq++, 600.30, 570.25, 136, 136, 1.15, "검사-스크래치불량"));
                targets.Add(MakePresetRoi(seq++, 950.20, 570.35, 136, 136, 1.15, "검사-솔더브릿지불량"));
                break;

            case InspectionSuiteAlgorithmKind.BlobRle:
                targets.Add(MakePresetRoi(seq++, 260.5, 400.5, 210, 210, 0.0, "원형 패드 블롭(+노이즈)"));
                targets.Add(MakePresetRoi(seq++, 600.4, 400.6, 230, 210, 0.0, "타원 캡슐 블롭(+노이즈)"));
                targets.Add(MakePresetRoi(seq++, 940.3, 400.4, 230, 210, 0.0, "사각 패키지 블롭(+노이즈)"));
                break;

            case InspectionSuiteAlgorithmKind.Curvilinear:
                targets.Add(MakePresetRoi(seq++, 588.0, 250.0, 780, 110, 0.0, "실링 비드 1 (정상 연속)"));
                targets.Add(MakePresetRoi(seq++, 588.0, 550.0, 780, 110, 0.0, "실링 비드 2 (단선·협소 불량)"));
                break;

            case InspectionSuiteAlgorithmKind.FewShot:
                double[] fsX = [200.0, 460.0, 720.0, 980.0];
                for (int i = 0; i < fsX.Length; i++)
                {
                    references.Add(MakePresetRoi(seq++, fsX[i], 180.0, 110, 90, (i - 1.5) * 1.4, $"학습-정상-{i + 1}", classLabel: 0));
                }

                for (int i = 0; i < fsX.Length; i++)
                {
                    references.Add(MakePresetRoi(seq++, fsX[i], 400.0, 110, 90, (1.5 - i) * 1.4, $"학습-불량-{i + 1}", classLabel: 1));
                }

                targets.Add(MakePresetRoi(seq++, 250.0, 630.0, 110, 90, 1.7, "테스트-정상(Good)"));
                targets.Add(MakePresetRoi(seq++, 600.0, 630.0, 110, 90, -1.1, "테스트-등록불량(Defect)"));
                targets.Add(MakePresetRoi(seq++, 950.0, 630.0, 110, 90, 0.6, "테스트-미등록이상치(OOD)"));
                break;

            case InspectionSuiteAlgorithmKind.PatchAnomaly:
                foreach (double px in new[] { 200.2, 460.3, 720.1, 980.4 })
                {
                    references.Add(MakePresetRoi(seq++, px, 230.2, 132, 132, 0.0, $"기준-정상BGA-{seq}"));
                }

                targets.Add(MakePresetRoi(seq++, 250.2, 570.2, 132, 132, 0.0, "검사-정상BGA"));
                targets.Add(MakePresetRoi(seq++, 600.2, 570.2, 132, 132, 0.0, "검사-볼누락불량"));
                targets.Add(MakePresetRoi(seq++, 950.2, 570.2, 132, 132, 0.0, "검사-솔더이물불량"));
                break;

            case InspectionSuiteAlgorithmKind.Calibration:
                targets.Add(MakePresetRoi(seq++, 600.0, 400.0, 760, 560, 0.0, "9-Point 핸드아이 교정 플레이트"));
                break;

            case InspectionSuiteAlgorithmKind.CornerAlignment:
                targets.Add(MakePresetRoi(seq++, 328.0, 228.0, 220, 220, 0.0, "코너 1 · 좌상단(TL) 모따기 가상코너"));
                targets.Add(MakePresetRoi(seq++, 888.0, 252.0, 220, 220, 90.0, "코너 2 · 우상단(TR) 모따기 가상코너"));
                targets.Add(MakePresetRoi(seq++, 872.0, 572.0, 220, 220, 180.0, "코너 3 · 우하단(BR) 모따기 가상코너"));
                targets.Add(MakePresetRoi(seq++, 312.0, 548.0, 220, 220, -90.0, "코너 4 · 좌하단(BL) 모따기 가상코너"));
                break;

            case InspectionSuiteAlgorithmKind.PpOcrOnnx:
                targets.Add(MakePresetRoi(seq++, 600.0, 260.0, 420, 64, 0.0, "레이저 연속 각인"));
                targets.Add(MakePresetRoi(seq++, 600.0, 520.0, 460, 64, 0.0, "도트 매트릭스 잉크젯"));
                break;

            case InspectionSuiteAlgorithmKind.AutoTeaching:
            {
                var p1 = MakePresetRoi(seq++, 280.4, 319.8, 200, 200, 23.7, "Part#1 브래킷 (+하위 ROI 2개 자동생성)");
                var p2 = MakePresetRoi(seq++, 660.4, 340.2, 200, 200, -41.3, "Part#2 커넥터 (+하위 ROI 2개 자동생성)");
                var p3 = MakePresetRoi(seq++, 979.6, 540.3, 200, 200, 112.4, "Part#3 실드캔 (+하위 ROI 1개 자동생성)");
                references.AddRange([p1, p2, p3]);
                targets.AddRange([p1, p2, p3]);
                break;
            }

            case InspectionSuiteAlgorithmKind.PreprocessingShadingAndRecon:
                targets.Add(MakePresetRoi(seq++, 250.0, 400.0, 240, 320, 0.0, "전처리 1 · 2D 다항식 조명 평탄화"));
                targets.Add(MakePresetRoi(seq++, 600.0, 400.0, 220, 220, 0.0, "전처리 2 · 비등방성 확산(Diffusion)"));
                targets.Add(MakePresetRoi(seq++, 950.0, 400.0, 240, 240, 0.0, "전처리 3 · 형태학적 재구성(Recon)"));
                break;

            case InspectionSuiteAlgorithmKind.AdvancedMetrologyEllipse:
                targets.Add(MakePresetRoi(seq++, 300.35, 400.25, 250, 220, 0.0, "복합 캘리퍼 (외곽 간섭에지 억제)"));
                targets.Add(MakePresetRoi(seq++, 640.40, 400.30, 250, 190, 22.5, "Fitzgibbon 타원 1 (22.5° 회전)"));
                targets.Add(MakePresetRoi(seq++, 980.25, 400.60, 220, 170, -31.0, "Fitzgibbon 타원 2 (-31.0° 회전)"));
                break;

            case InspectionSuiteAlgorithmKind.AdvancedBlobWatershed:
                targets.Add(MakePresetRoi(seq++, 260.0, 400.0, 220, 190, 0.0, "고급형상 (Euler 홀2개·MinAreaBox)"));
                targets.Add(MakePresetRoi(seq++, 610.0, 400.0, 220, 190, 0.0, "워터셰드 분리 1 (2접촉 솔더볼)"));
                targets.Add(MakePresetRoi(seq++, 950.0, 400.0, 210, 190, 0.0, "워터셰드 분리 2 (3접촉 입자군집)"));
                break;

            case InspectionSuiteAlgorithmKind.DataMatrix2DReader:
                targets.Add(MakePresetRoi(seq++, 324.0, 384.0, 210, 210, 0.0, "DataMatrix 1 (0°)"));
                targets.Add(MakePresetRoi(seq++, 844.0, 384.0, 210, 210, 0.0, "DataMatrix 2 (0°)"));
                targets.Add(MakePresetRoi(seq++, 584.0, 656.0, 196, 196, 0.0, "DataMatrix 3 (+22.5° 회전)"));
                break;

            case InspectionSuiteAlgorithmKind.PreprocessingFftAndGabor:
                targets.Add(MakePresetRoi(seq++, 210.0, 405.0, 240, 280, 0.0, "전처리 1 · 2D FFT 모아레 노치 감쇠"));
                targets.Add(MakePresetRoi(seq++, 610.0, 400.0, 240, 240, 0.0, "전처리 2 · 호모모픽(Homomorphic) 조명 분리"));
                targets.Add(MakePresetRoi(seq++, 980.0, 400.0, 240, 240, 30.0, "전처리 3 · 2D Gabor 방향성 텍스처"));
                break;

            case InspectionSuiteAlgorithmKind.Barcode1DReader:
                targets.Add(MakePresetRoi(seq++, 310.0, 385.0, 460, 220, 0.0, "1D 바코드 1 (0°)"));
                targets.Add(MakePresetRoi(seq++, 855.0, 385.0, 390, 220, 0.0, "1D 바코드 2 (0°)"));
                targets.Add(MakePresetRoi(seq++, 600.0, 648.0, 360, 210, 0.0, "1D 바코드 3 (-18.0° 회전)"));
                break;

            case InspectionSuiteAlgorithmKind.PhotometricStereo:
                targets.Add(MakePresetRoi(seq++, 330.0, 400.0, 320, 320, 0.0, "포토메트릭 1 · 2D얼룩만 있는 정상표면(OK)"));
                targets.Add(MakePresetRoi(seq++, 870.0, 400.0, 320, 320, 0.0, "포토메트릭 2 · 3D 미세찍힘 요철결함(NG)"));
                break;

            case InspectionSuiteAlgorithmKind.GeometricTolerancing:
                targets.Add(MakePresetRoi(seq++, 320.0, 380.0, 350, 350, 0.0, "GD&T 부품 1 · 직교90° & 동심보어 정상(OK)"));
                targets.Add(MakePresetRoi(seq++, 840.0, 380.0, 350, 350, 0.0, "GD&T 부품 2 · 경사데이텀 & 편심보어 불량(NG)"));
                break;
        }

        return (slug, targets, references);
    }

    private sealed class SuiteSampleTileLoader : IImageLoader
    {
        public Task<ITileSource> LoadAsync(ImageReference reference, IProgress<string> progress, CancellationToken cancellationToken)
            => Task.FromResult<ITileSource>(new InspectionSuiteSampleTileSource(reference));
    }

    private sealed class DeterministicStubPpOcrService : IPpOcrInferenceService
    {
        public PpOcrModelStatus Status => PpOcrModelStatus.Ready;
        public string StatusMessage => "Deterministic Stub Ready";
        public bool IsModelInstalled => true;
        public bool HasKoreanModel => true;
        public PpOcrExecutionDevice RequestedDevice => PpOcrExecutionDevice.Cpu;
        public PpOcrExecutionDevice ActiveDevice => PpOcrExecutionDevice.Cpu;
        public string? CudaFallbackReason => null;
        public int SessionCreationCount => 1;

        public PpOcrRecognitionResult RecognizeRoi(ReadOnlyImageView2D<byte> roiView, PpOcrRunOptions? options = null)
        {
            long sum = 0;
            for (int y = 0; y < roiView.Height; y++)
            {
                ReadOnlySpan<byte> row = roiView.GetRowSpan(y);
                for (int x = 0; x < roiView.Width; x++)
                {
                    sum += row[x];
                }
            }

            string code = $"STUB-{sum % 10000:D4}";
            var chars = new List<PpOcrCharacterBox>(code.Length);
            for (int i = 0; i < code.Length; i++)
            {
                int sc = 4 + (i * 12);
                chars.Add(new PpOcrCharacterBox(code[i], 0.985f, 0, sc, sc + 9, 6, Math.Max(12, roiView.Height - 6), false));
            }

            var poly = ImmutableArray.Create(
                new PointD(2, 4),
                new PointD(roiView.Width - 2, 4),
                new PointD(roiView.Width - 2, roiView.Height - 4),
                new PointD(2, roiView.Height - 4));

            var region = new PpOcrRegionResult(code, [code], chars, 0.985f, 0.985f, DarkOnLight: true, IsPass: true);
            var box = new PpOcrBoxResult(poly, code, 0.985f, 0.985f, 0.95f, UsedInvertedPolarity: false, IsDetectedQuad: true);
            return new PpOcrRecognitionResult(PpOcrModelStatus.Ready, StatusMessage, region, [box], PpOcrExecutionDevice.Cpu, UsedDetectionStage: true, UsedInvertedPolarity: false);
        }

        public void Dispose()
        {
        }
    }

    private static string SnapshotString(DocumentState state)
        => string.Join("|", state.Rois.Values.OrderBy(r => r.Order).Select(r =>
            $"{r.Order}:{r.Name}:{r.Geometry.Center.X:R},{r.Geometry.Center.Y:R}:{r.Geometry.Width:R}x{r.Geometry.Height:R}@{r.Geometry.Angle:R}"));

    private sealed class DeterministicTileLoader(int width, int height) : IImageLoader
    {
        public Task<ITileSource> LoadAsync(ImageReference reference, IProgress<string> progress, CancellationToken cancellationToken)
            => Task.FromResult<ITileSource>(new DeterministicTileSource(width, height));
    }

    private sealed class DeterministicTileSource(int width, int height) : ITileSource
    {
        public ImageInfo Info { get; } = new("mem:320x240", "mem", width, height, PixelKind.Gray8, 1);

        public PixelBuffer Read(TileKey key, CancellationToken cancellationToken)
        {
            var buf = new PixelBuffer(width, height, PixelKind.Gray8);
            var span = buf.Span;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    bool bar = x >= 130 && x <= 190 && y >= 70 && y <= 170;
                    span[(y * buf.Stride) + x] = bar ? (byte)215 : (byte)(40 + ((x + y) & 7));
                }
            }

            return buf;
        }

        public void Dispose()
        {
        }
    }

    private sealed class UniformTileLoader(int width, int height, byte intensity) : IImageLoader
    {
        public Task<ITileSource> LoadAsync(ImageReference reference, IProgress<string> progress, CancellationToken cancellationToken)
            => Task.FromResult<ITileSource>(new UniformTileSource(width, height, intensity));
    }

    private sealed class UniformTileSource(int width, int height, byte intensity) : ITileSource
    {
        public ImageInfo Info { get; } = new("blank:320x240", "blank", width, height, PixelKind.Gray8, 1);

        public PixelBuffer Read(TileKey key, CancellationToken cancellationToken)
        {
            var buf = new PixelBuffer(width, height, PixelKind.Gray8);
            buf.Span.Fill(intensity);
            return buf;
        }

        public void Dispose()
        {
        }
    }
}
