using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using OpenCvSharp;
using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Services;
using TeachingToolWpf.ViewModels;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.IndustrialOcrOcv;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionGeometry;
using VisionImaging;
using VisionImaging.Abstractions;
using VisionOcr.PpOcr;
using Xunit;
using Xunit.Abstractions;

namespace TeachingToolWpf.Fluent2.Tests.OcrBenchmark;

public sealed class PpOcrInferenceServiceTests(ITestOutputHelper output)
{
    [Fact]
    public void MissingModelDirectory_ReportsMissingModelStatusAndZeroFakeOutput_WithoutThrowing()
    {
        string emptyDir = Path.Combine(Path.GetTempPath(), $"PpOcr_Empty_{Guid.NewGuid():N}");
        Directory.CreateDirectory(emptyDir);
        try
        {
            using var service = new PpOcrInferenceService(PpOcrModelOptions.FromDirectory(emptyDir, PpOcrExecutionDevice.Cpu));
            Assert.Equal(PpOcrModelStatus.MissingModel, service.Status);
            Assert.False(service.IsModelInstalled);
            Assert.Contains("모델 미설치", service.StatusMessage, StringComparison.Ordinal);

            byte[] dummy = new byte[64 * 24];
            Array.Fill(dummy, (byte)200);
            PpOcrRecognitionResult res = service.RecognizeRoi(new ReadOnlyImageView2D<byte>(dummy, 64, 24, 64));
            Assert.Equal(PpOcrModelStatus.MissingModel, res.ModelStatus);
            Assert.Equal(string.Empty, res.Region.FullText);
            Assert.False(res.Region.IsPass);
        }
        finally
        {
            if (Directory.Exists(emptyDir))
            {
                Directory.Delete(emptyDir, recursive: true);
            }
        }
    }

    [Fact]
    public void CorruptModelFile_ThrowsInvalidDataExceptionLoudly_NoSilentCatch()
    {
        string corruptDir = Path.Combine(Path.GetTempPath(), $"PpOcr_Corrupt_{Guid.NewGuid():N}");
        Directory.CreateDirectory(corruptDir);
        try
        {
            File.WriteAllBytes(Path.Combine(corruptDir, "ch_PP-OCRv4_det_mobile.onnx"), [0xDE, 0xAD, 0xBE, 0xEF]);
            File.WriteAllBytes(Path.Combine(corruptDir, "en_PP-OCRv4_rec_mobile.onnx"), [0xDE, 0xAD, 0xBE, 0xEF]);
            File.WriteAllText(Path.Combine(corruptDir, "en_dict.txt"), "0\n1\nA\n", Encoding.UTF8);

            InvalidDataException ex = Assert.Throws<InvalidDataException>(
                () => new PpOcrInferenceService(PpOcrModelOptions.FromDirectory(corruptDir, PpOcrExecutionDevice.Cpu)));
            Assert.Contains("모델 로드 실패", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(corruptDir))
            {
                Directory.Delete(corruptDir, recursive: true);
            }
        }
    }

    [Fact]
    public void SessionLifetime_CreatesSessionsOnceOnInit_ReusesAcrossCalls_AndDisposesCleanly()
    {
        (_, string repoRootPath, _) = OcrBenchmarkDataset.LoadFromDefaultLocation();
        string modelDir = Path.Combine(repoRootPath, "models", "ppocr");

        var service = new PpOcrInferenceService(PpOcrModelOptions.FromDirectory(modelDir, PpOcrExecutionDevice.Cpu));
        try
        {
            Assert.Equal(PpOcrModelStatus.Ready, service.Status);
            Assert.Equal(3, service.SessionCreationCount);

            (byte[] px, int w, int h) = RenderSyntheticGrayLine("BATCH2026", darkOnLight: true);
            var view = new ReadOnlyImageView2D<byte>(px, w, h, w);

            for (int i = 0; i < 4; i++)
            {
                PpOcrRecognitionResult r = service.RecognizeRoi(view);
                Assert.Equal("BATCH2026", r.Region.FullText);
            }

            // ZERO per-call new InferenceSession!
            Assert.Equal(3, service.SessionCreationCount);
        }
        finally
        {
            service.Dispose();
        }

        byte[] afterDisposePx = new byte[32 * 16];
        Assert.Throws<ObjectDisposedException>(
            () => service.RecognizeRoi(new ReadOnlyImageView2D<byte>(afterDisposePx, 32, 16, 32)));
    }

    [Fact]
    public void OfficialPreprocessing_MatchesPaddleOcrShapesNormalizationAndPadding()
    {
        // 1) Rec input: height=48, width=max(320, ceil(48 * W / H)), (x/255 - 0.5)/0.5, right-pad = 0.0f
        int srcW = 48;
        int srcH = 24; // aspect = 2.0 -> resizedContentWidth = 96, targetWidth = 320
        byte[] raw = new byte[srcW * srcH];
        for (int y = 0; y < srcH; y++)
        {
            for (int x = 0; x < srcW; x++)
            {
                raw[(y * srcW) + x] = x < 24 ? (byte)0 : (byte)255;
            }
        }

        float[] recTensor = PpOcrPreprocessor.BuildRecInputTensor(
            new ReadOnlyImageView2D<byte>(raw, srcW, srcH, srcW),
            invertPolarity: false,
            out int targetWidth,
            out int resizedContentWidth);

        Assert.Equal(96, resizedContentWidth);
        Assert.Equal(320, targetWidth);
        Assert.Equal(3 * 48 * 320, recTensor.Length);

        int planeLen = 48 * 320;
        // Left active pixel (0 -> -1.0f) across all 3 replicated channels
        Assert.Equal(-1.0f, recTensor[0], precision: 5);
        Assert.Equal(-1.0f, recTensor[planeLen], precision: 5);
        Assert.Equal(-1.0f, recTensor[2 * planeLen], precision: 5);

        // Right active pixel (255 -> +1.0f)
        Assert.Equal(1.0f, recTensor[90], precision: 5);

        // Right padding region [x >= 96] must be exact 0.0f across all 3 channels
        for (int c = 0; c < 3; c++)
        {
            for (int x = resizedContentWidth; x < targetWidth; x++)
            {
                Assert.Equal(0.0f, recTensor[(c * planeLen) + (10 * targetWidth) + x], precision: 6);
            }
        }

        // 2) Det input: 32-multiple spatial dims + ImageNet (unit - mean) / std
        float[] detTensor = PpOcrPreprocessor.BuildDetInputTensor(
            new ReadOnlyImageView2D<byte>(raw, srcW, srcH, srcW),
            invertPolarity: false,
            DbDetOptions.Default,
            out int detW,
            out int detH,
            out _,
            out _,
            out _);

        Assert.Equal(0, detW % 32);
        Assert.Equal(0, detH % 32);
        Assert.Equal(3 * detW * detH, detTensor.Length);
    }

    [Fact]
    public void AllowedCharacterSet_MasksCtcVocabularyWithoutPostHocStringReplacement()
    {
        (_, string repoRootPath, _) = OcrBenchmarkDataset.LoadFromDefaultLocation();
        string modelDir = Path.Combine(repoRootPath, "models", "ppocr");
        using var service = new PpOcrInferenceService(PpOcrModelOptions.FromDirectory(modelDir, PpOcrExecutionDevice.Cpu));

        (byte[] px, int w, int h) = RenderSyntheticGrayLine("MixedCase 2026", darkOnLight: true);
        var view = new ReadOnlyImageView2D<byte>(px, w, h, w);

        PpOcrRecognitionResult allRes = service.RecognizeRoi(view, new PpOcrRunOptions(AllowedCharacterSet: OcrAllowedCharacterSet.All));
        Assert.Equal("MixedCase 2026", allRes.Region.FullText);

        PpOcrRecognitionResult digitsOnlyRes = service.RecognizeRoi(view, new PpOcrRunOptions(AllowedCharacterSet: OcrAllowedCharacterSet.DigitsOnly));
        Assert.DoesNotContain(digitsOnlyRes.Region.FullText, c => c != ' ' && !char.IsAsciiDigit(c));
        Assert.Contains("2026", digitsOnlyRes.Region.FullText, StringComparison.Ordinal);
    }

    [Fact]
    public void InvertedPolarityAndPerspectiveSkewedRoi_AreRecognizedViaPolarityAndDbNetPerspectiveCrop()
    {
        (_, string repoRootPath, _) = OcrBenchmarkDataset.LoadFromDefaultLocation();
        string modelDir = Path.Combine(repoRootPath, "models", "ppocr");
        using var service = new PpOcrInferenceService(PpOcrModelOptions.FromDirectory(modelDir, PpOcrExecutionDevice.Cpu));

        // B3.6: White-on-black (inverted polarity) marking
        (byte[] invPx, int invW, int invH) = RenderSyntheticGrayLine("LOT 9081", darkOnLight: false);
        PpOcrRecognitionResult invRes = service.RecognizeRoi(
            new ReadOnlyImageView2D<byte>(invPx, invW, invH, invW),
            new PpOcrRunOptions(PolarityMode: PpOcrPolarityMode.Auto));
        output.WriteLine($"[B3.6 Inverted] Text='{invRes.Region.FullText}', MeanConf={invRes.Region.MeanConfidence:F4}, UsedInv={invRes.UsedInvertedPolarity}");
        Assert.Equal("LOT 9081", invRes.Region.FullText);

        // B3.8: Tilted + slight perspective-skewed ROI via DBNet Det+Rec quad perspective crop
        (byte[] skewPx, int skewW, int skewH) = RenderPerspectiveSkewedGrayLine("LOT2026", angleDeg: -11.0);
        PpOcrRecognitionResult skewRes = service.RecognizeRoi(
            new ReadOnlyImageView2D<byte>(skewPx, skewW, skewH, skewW),
            new PpOcrRunOptions(PipelineMode: PpOcrPipelineMode.DetAndRec));
        output.WriteLine($"[B3.8 Perspective+Tilt] Text='{skewRes.Region.FullText}', MeanConf={skewRes.Region.MeanConfidence:F4}, Boxes={skewRes.Boxes.Length}");
        Assert.Equal("LOT2026", skewRes.Region.FullText);
        Assert.True(skewRes.UsedDetectionStage);
    }

    [Fact]
    public async Task UnifiedOcrOcvUiExposure_ConsolidatesToSingleEntry_NormalizesLegacyKinds_AndCopiesPpOcrTextToOcv()
    {
        (_, string repoRootPath, _) = OcrBenchmarkDataset.LoadFromDefaultLocation();
        string tempDir = Path.Combine(Path.GetTempPath(), $"PpOcr_UiTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            IImageLoader loader = ImageLoaderHost.Create(Path.Combine(tempDir, "cache"));

            // 1) Even when unwired, unified OCR / OCV entry is present and reports honest status badge ("모델 미연결")
            using var unwiredVm = new InspectionSuiteViewModel(new InspectionSuiteService(loader, () => null));
            unwiredVm.SelectOcrCategory.Execute(null);
            InspectionSuiteAlgorithmOption unwiredOption = Assert.Single(unwiredVm.CategoryAlgorithms);
            Assert.Equal(InspectionSuiteAlgorithmKind.PpOcrOnnx, unwiredOption.Kind);
            Assert.Equal(InspectionSuiteAlgorithmKind.PpOcrOnnx, unwiredVm.SelectedAlgorithm);
            Assert.False(unwiredVm.IsPpOcrWired);

            // 2) Legacy classical OCR enum values normalize cleanly to unified PpOcrOnnx
#pragma warning disable CS0618
            unwiredVm.SelectedAlgorithm = InspectionSuiteAlgorithmKind.IndustrialOcrOcv;
            Assert.Equal(InspectionSuiteAlgorithmKind.PpOcrOnnx, unwiredVm.SelectedAlgorithm);
            unwiredVm.SelectedAlgorithm = InspectionSuiteAlgorithmKind.LightweightOcr;
            Assert.Equal(InspectionSuiteAlgorithmKind.PpOcrOnnx, unwiredVm.SelectedAlgorithm);
            unwiredVm.SelectedAlgorithm = InspectionSuiteAlgorithmKind.CpuDeepOcr;
            Assert.Equal(InspectionSuiteAlgorithmKind.PpOcrOnnx, unwiredVm.SelectedAlgorithm);
#pragma warning restore CS0618

            // 3) Wired PP-OCRv4 + OCV: 1-click ApplyPpOcrTextToOcvExpectedText copies recognized text to SelectedRoiOcvExpectedText
            string modelDir = Path.Combine(repoRootPath, "models", "ppocr");
            using var ppOcrService = new PpOcrInferenceService(PpOcrModelOptions.FromDirectory(modelDir, PpOcrExecutionDevice.Cpu));
            var ocvService = new TaughtFontOcvService(loader, () => null);
            bool confirmInvoked = false;
            using var wiredVm = new InspectionSuiteViewModel(
                new InspectionSuiteService(loader, () => null, ppOcrService),
                ocvService,
                confirmApplyPpOcrToOcv: (_, _, _) =>
                {
                    confirmInvoked = true;
                    return true;
                });

            wiredVm.SelectOcrCategory.Execute(null);
            Assert.Single(wiredVm.CategoryAlgorithms);
            Assert.True(wiredVm.IsPpOcrWired);
            Assert.True(wiredVm.IsPpOcrOnnxSelected);

            string synthImgPath = Path.Combine(tempDir, "copy_test.png");
            (byte[] px, int w, int h) = RenderSyntheticGrayLine("12345678", darkOnLight: true);
            using (var mat = new Mat(h, w, MatType.CV_8UC1))
            {
                Marshal.Copy(px, 0, mat.Data, px.Length);
                Cv2.ImWrite(synthImgPath, mat);
            }

            var session = new EditorSession();
            var imageRef = new ImageReference(synthImgPath, "copy_test.png", w, h);
            session.InitializeImage(imageRef, new ViewportTransform(1.0, default));
            Guid roiId = session.Add(new RoiGeometry(new PointD(w * 0.5, h * 0.5), w - 4, h - 2, 0.0));
            session.Select([roiId]);

            wiredVm.Update(new InspectionUpdate(session.State, Generation: 1, Live: false, CanExecute: true, ToolAvailable: true, TransformPreview: null));
            await wiredVm.ExecuteSelected.ExecuteAsync();

            Assert.NotNull(wiredVm.LastOutput);
            Assert.True(
                wiredVm.LastOutput.Items[0].IsPass,
                $"Expected synthetic UI copy sample to pass default PP-OCR gate (badge='{wiredVm.LastOutput.Items[0].BadgeLabel}', detail='{wiredVm.LastOutput.Items[0].DetailLine}')");
            Assert.True(wiredVm.ApplyPpOcrTextToOcvExpectedText.CanExecute(null));
            wiredVm.ApplyPpOcrTextToOcvExpectedText.Execute(null);
            Assert.True(confirmInvoked);
            Assert.Equal("12345678", wiredVm.SelectedRoiOcvExpectedText);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try
                {
                    Directory.Delete(tempDir, recursive: true);
                }
                catch
                {
                    // Ignore transient handle delay.
                }
            }
        }
    }

    [Fact]
    public void BatchRecognitionAndHighSpeedPreset_PreserveAccuracy_AndMeasureIndustrialTactTime()
    {
        (OcrBenchmarkDataset dataset, string repoRootPath, _) = OcrBenchmarkDataset.LoadFromDefaultLocation();
        string modelDir = Path.Combine(repoRootPath, "models", "ppocr");
        using var service = new PpOcrInferenceService(PpOcrModelOptions.FromDirectory(modelDir, PpOcrExecutionDevice.Cpu));

        // 1) Load real kensa.png and extract the 10 real chip marking ROIs (Chips 2..6: 5 x "K9X8G08U0M" + 5 x "SAMSUNG")
        string kensaPath = Path.Combine(repoRootPath, "kensa.png");
        using Mat kensaGray = Cv2.ImRead(kensaPath, ImreadModes.Grayscale);
        Assert.False(kensaGray.Empty());

        List<OcrBenchmarkEntry> kensaEntries = dataset.Entries
            .Where(e => e.Id.StartsWith("Kensa_", StringComparison.Ordinal))
            .OrderBy(e => e.Id, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(10, kensaEntries.Count);

        var patches10 = new PpOcrRoiPatch[kensaEntries.Count];
        for (int i = 0; i < kensaEntries.Count; i++)
        {
            patches10[i] = CropPatchFromMat(kensaGray, kensaEntries[i]);
        }

        // Build 32-ROI industrial tray from the real kensa chip ROIs
        var patches32 = new PpOcrRoiPatch[32];
        for (int i = 0; i < patches32.Length; i++)
        {
            patches32[i] = patches10[i % patches10.Length];
        }

        var autoOpts = new PpOcrRunOptions(
            Language: PpOcrLanguage.MultilingualConfidenceSelect,
            PolarityMode: PpOcrPolarityMode.Auto,
            PipelineMode: PpOcrPipelineMode.Auto);
        PpOcrRunOptions highSpeedOpts = PpOcrRunOptions.CreateHighSpeedProduction(
            language: PpOcrLanguage.English,
            polarityMode: PpOcrPolarityMode.InvertedOnly,
            allowedCharacterSet: OcrAllowedCharacterSet.UpperAlphaNumericAndSymbols);

        // Warm-up passes
        _ = service.RecognizeRoi(patches10[1].AsView(), autoOpts);
        _ = service.RecognizeRoi(patches10[1].AsView(), highSpeedOpts);
        _ = service.RecognizeBatch(patches10, autoOpts);
        _ = service.RecognizeBatch(patches10, highSpeedOpts);

        // Batch vs Sequential equivalence check: Batch(UniversalAuto) must produce 100% identical text & verdict to Sequential RecognizeRoi,
        // and all 5 "SAMSUNG" vendor ROIs must match ground truth across Sequential, Batch(UniversalAuto), and Batch(HighSpeedProduction).
        IReadOnlyList<PpOcrRecognitionResult> batchAuto10 = service.RecognizeBatch(patches10, autoOpts);
        IReadOnlyList<PpOcrRecognitionResult> batchFast10 = service.RecognizeBatch(patches10, highSpeedOpts);
        for (int i = 0; i < kensaEntries.Count; i++)
        {
            string gt = kensaEntries[i].GroundTruthJoined;
            PpOcrRecognitionResult seqRes = service.RecognizeRoi(patches10[i].AsView(), autoOpts);
            Assert.Equal(seqRes.Region.FullText, batchAuto10[i].Region.FullText);
            Assert.Equal(seqRes.Region.IsPass, batchAuto10[i].Region.IsPass);
            if (kensaEntries[i].Id.EndsWith("_Vendor", StringComparison.Ordinal))
            {
                Assert.Equal(gt, seqRes.Region.FullText);
                Assert.Equal(gt, batchAuto10[i].Region.FullText);
                Assert.Equal(gt, batchFast10[i].Region.FullText);
            }
        }

        // Measure Single ROI ("SAMSUNG", Kensa_Chip2_Vendor)
        PpOcrRoiPatch singleSamsung = patches10.Where((_, idx) => kensaEntries[idx].Id == "Kensa_Chip2_Vendor").Single();
        double singleAutoMs = MeasureMedianMs(5, () => _ = service.RecognizeRoi(singleSamsung.AsView(), autoOpts));
        double singleFastMs = MeasureMedianMs(5, () => _ = service.RecognizeRoi(singleSamsung.AsView(), highSpeedOpts));

        // Measure 10-ROI kensa tray: Sequential vs Batch(UniversalAuto) vs Batch(HighSpeedProduction)
        double seq10AutoMs = MeasureMedianMs(4, () =>
        {
            for (int i = 0; i < patches10.Length; i++)
            {
                _ = service.RecognizeRoi(patches10[i].AsView(), autoOpts);
            }
        });
        double batch10AutoMs = MeasureMedianMs(4, () => _ = service.RecognizeBatch(patches10, autoOpts));
        double batch10FastMs = MeasureMedianMs(5, () => _ = service.RecognizeBatch(patches10, highSpeedOpts));

        // Measure 32-ROI tray: Sequential(UniversalAuto) vs Batch(UniversalAuto) vs Batch(HighSpeedProduction)
        double seq32AutoMs = MeasureMedianMs(2, () =>
        {
            for (int i = 0; i < patches32.Length; i++)
            {
                _ = service.RecognizeRoi(patches32[i].AsView(), autoOpts);
            }
        });
        double batch32AutoMs = MeasureMedianMs(3, () => _ = service.RecognizeBatch(patches32, autoOpts));
        double batch32FastMs = MeasureMedianMs(4, () => _ = service.RecognizeBatch(patches32, highSpeedOpts));

        // Measure Multi-word Receipt (ocr6.jpg Det+Rec batched quad recognition)
        string ocr6Path = Path.Combine(repoRootPath, "ocr6.jpg");
        using Mat ocr6Gray = Cv2.ImRead(ocr6Path, ImreadModes.Grayscale);
        OcrBenchmarkEntry ocr6Full = dataset.Entries.First(e => e.ImageFile.Equals("ocr6.jpg", StringComparison.OrdinalIgnoreCase));
        PpOcrRoiPatch ocr6Patch = CropPatchFromMat(ocr6Gray, ocr6Full);
        _ = service.RecognizeRoi(ocr6Patch.AsView(), autoOpts);
        double ocr6DetRecMs = MeasureMedianMs(3, () => _ = service.RecognizeRoi(ocr6Patch.AsView(), autoOpts));

        // Measure TaughtFontOcvVerifier tact time on real kensa.png chips (Teach Chip 1 ocvSpecimens -> Verify disjoint Chip 2..6 ROIs)
        OcrPrintGroupSpec kensaGroup = dataset.PrintGroups.Single(g => g.GroupId == "pg_kensa_chip_laser");
        var charSamples = new List<GoldenCharSample>();
        using var workspace = new VisionWorkspace();
        foreach (OcrOcvSpecimenEntry spec in kensaGroup.OcvSpecimens!)
        {
            PpOcrRoiPatch tp = CropRectFromMat(kensaGray, spec.CenterX, spec.CenterY, spec.Width, spec.Height);
            ReadOnlyImageView2D<byte> tpView = tp.AsView();
            var tpFixture = new FixturePose2D(tpView.Width * 0.5f, tpView.Height * 0.5f, 0f);
            OcvVerificationOptions teachOpts = BuildKensaOcvOptions(tpView.Width, tpView.Height, spec.GroundTruth.Length, kensaGroup.DarkOnLight);
            IReadOnlyList<GoldenCharSample> extracted = TaughtFontOcvVerifier.ExtractTeachingSamplesFromLine(
                tpView,
                in tpFixture,
                spec.GroundTruth,
                teachOpts,
                workspace);
            Assert.Equal(spec.GroundTruth.Length, extracted.Count);
            charSamples.AddRange(extracted);
        }

        IndustrialOcrFontModel goldenModel = IndustrialOcrTrainer.Train(charSamples.ToArray());
        var verifier = new TaughtFontOcvVerifier(goldenModel);
        OcrOcvSpecimenEntry chip1VendorSpec = kensaGroup.OcvSpecimens!.Single(s => s.GroundTruth == "SAMSUNG");
        PpOcrRoiPatch chip1VendorPatch = CropRectFromMat(kensaGray, chip1VendorSpec.CenterX, chip1VendorSpec.CenterY, chip1VendorSpec.Width, chip1VendorSpec.Height);
        ReadOnlyImageView2D<byte> chip1View = chip1VendorPatch.AsView();
        var chip1Fixture = new FixturePose2D(chip1View.Width * 0.5f, chip1View.Height * 0.5f, 0f);
        var samsungSpec = new OcrFieldFormatSpec(FormatMask: null, ExpectedText: "SAMSUNG", StrictLengthMatch: true);
        OcvVerificationOptions chip1OcvOpts = BuildKensaOcvOptions(chip1View.Width, chip1View.Height, "SAMSUNG".Length, kensaGroup.DarkOnLight);
        OcvVerificationResult warmChip1Ocv = verifier.VerifyLine(chip1View, in chip1Fixture, samsungSpec, chip1OcvOpts, workspace);
        Assert.True(warmChip1Ocv.IsOk);
        Assert.Equal("SAMSUNG", warmChip1Ocv.DecodedText.Replace(" ", string.Empty, StringComparison.Ordinal));

        ReadOnlyImageView2D<byte> singleView = singleSamsung.AsView();
        var singleFixture = new FixturePose2D(singleView.Width * 0.5f, singleView.Height * 0.5f, 0f);
        OcvVerificationOptions singleOcvOpts = BuildKensaOcvOptions(singleView.Width, singleView.Height, "SAMSUNG".Length, kensaGroup.DarkOnLight);
        _ = verifier.VerifyLine(singleView, in singleFixture, samsungSpec, singleOcvOpts, workspace);

        var specs32 = new OcrFieldFormatSpec[patches32.Length];
        var opts32 = new OcvVerificationOptions[patches32.Length];
        for (int i = 0; i < patches32.Length; i++)
        {
            string expected = kensaEntries[i % kensaEntries.Count].GroundTruthJoined;
            specs32[i] = new OcrFieldFormatSpec(FormatMask: null, ExpectedText: expected, StrictLengthMatch: true);
            opts32[i] = BuildKensaOcvOptions(patches32[i].Width, patches32[i].Height, expected.Length, kensaGroup.DarkOnLight);
        }

        double ocvSingleMs = MeasureMedianMs(25, () =>
            _ = verifier.VerifyLine(singleSamsung.AsView(), in singleFixture, samsungSpec, singleOcvOpts, workspace));
        double ocv32Ms = MeasureMedianMs(15, () =>
        {
            for (int i = 0; i < patches32.Length; i++)
            {
                ReadOnlyImageView2D<byte> v = patches32[i].AsView();
                var fx = new FixturePose2D(v.Width * 0.5f, v.Height * 0.5f, 0f);
                _ = verifier.VerifyLine(v, in fx, specs32[i], opts32[i], workspace);
            }
        });

        var sb = new StringBuilder();
        sb.AppendLine("=== INDUSTRIAL TACT-TIME BENCHMARK (CPU, RELEASE, MEDIAN WARM LATENCY) ===");
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"1) Single ROI ('SAMSUNG', Kensa_Chip2_Vendor): UniversalAuto = {singleAutoMs:F2} ms | HighSpeedProduction = {singleFastMs:F2} ms"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"2) 10-ROI Real Kensa Chip Tray (5 chips x 2 lines): Sequential(Auto) = {seq10AutoMs:F2} ms ({seq10AutoMs / 10.0:F2} ms/ROI) | Batch(Auto) = {batch10AutoMs:F2} ms ({batch10AutoMs / 10.0:F2} ms/ROI) | Batch(HighSpeed) = {batch10FastMs:F2} ms ({batch10FastMs / 10.0:F2} ms/ROI)"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"3) 32-ROI Industrial Tray: Sequential(Auto) = {seq32AutoMs:F2} ms ({seq32AutoMs / 32.0:F2} ms/ROI) | Batch(Auto) = {batch32AutoMs:F2} ms ({batch32AutoMs / 32.0:F2} ms/ROI) | Batch(HighSpeed) = {batch32FastMs:F2} ms ({batch32FastMs / 32.0:F2} ms/ROI)"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"4) Multi-word Receipt (ocr6.jpg Det+Rec batched quads): {ocr6DetRecMs:F2} ms"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"5) TaughtFontOcvVerifier (Real kensa.png, pixel-disjoint Chip1->Chip2..6): Single ROI = {ocvSingleMs:F3} ms | 32-ROI Tray = {ocv32Ms:F2} ms ({ocv32Ms / 32.0:F3} ms/ROI)"));
        output.WriteLine(sb.ToString());

        Assert.True(batch32FastMs < seq32AutoMs, $"Expected 32-ROI HighSpeed batch ({batch32FastMs:F2} ms) < sequential Auto ({seq32AutoMs:F2} ms).");
    }

    [Fact]
    public void P1_CtcDecoder_UsesFirstTimestepProbability_AndIncludesEmittedSpaceTokenConfidence()
    {
        // Vocabulary: class 0 = blank, class 1 = 'A', class 2 = 'B', class 3 = ' '
        ReadOnlySpan<char> vocab = ['A', 'B', ' '];
        ReadOnlySpan<bool> allowed = [true, true, true, true];
        const int timeSteps = 5;
        const int numClasses = 4;

        // Softmax rows summing to 1.0:
        // t=0: 'A' run starts with 0.60
        // t=1: 'A' run continues with 0.98 (official BaseRecLabelDecode MUST use t=0 prob 0.60, NOT max 0.98!)
        // t=2: ' ' space token with 0.50
        // t=3: blank (0.97)
        // t=4: 'B' token with 0.90
        float[] matrix =
        [
            0.30f, 0.60f, 0.05f, 0.05f,
            0.01f, 0.98f, 0.005f, 0.005f,
            0.40f, 0.05f, 0.05f, 0.50f,
            0.97f, 0.01f, 0.01f, 0.01f,
            0.05f, 0.03f, 0.90f, 0.02f,
        ];

        PpOcrCtcDecoder.DecodedCropLine decoded = PpOcrCtcDecoder.DecodeContiguousMatrix(
            vocab,
            matrix,
            timeSteps,
            numClasses,
            targetWidth: 320,
            resizedContentWidth: 320,
            orientedCropWidth: 160,
            orientedCropHeight: 48,
            allowed);

        Assert.Equal("A B", decoded.Text);
        Assert.Equal(2, decoded.Characters.Count);
        Assert.Equal('A', decoded.Characters[0].Character);
        Assert.Equal(0.60f, decoded.Characters[0].Confidence, precision: 4); // First timestep (0.60), NOT run max (0.98)!
        Assert.Equal('B', decoded.Characters[1].Character);
        Assert.True(decoded.Characters[1].PrecededBySpace);
        Assert.Equal(0.90f, decoded.Characters[1].Confidence, precision: 4);

        // Space token prob (0.50) must be included in MeanConfidence = (0.60 + 0.50 + 0.90) / 3 = 0.6667 and MinConfidence = 0.50!
        Assert.Equal((0.60f + 0.50f + 0.90f) / 3.0f, decoded.MeanConfidence, precision: 4);
        Assert.Equal(0.50f, decoded.MinConfidence, precision: 4);
    }

    [Fact]
    public void P2_RecognizeBatch_ShuffledHeterogeneousWidths_MatchesSingleRoiExactly()
    {
        (_, string repoRootPath, _) = OcrBenchmarkDataset.LoadFromDefaultLocation();
        string modelDir = Path.Combine(repoRootPath, "models", "ppocr");
        using var service = new PpOcrInferenceService(PpOcrModelOptions.FromDirectory(modelDir, PpOcrExecutionDevice.Cpu));

        (string Text, int Width, int Height)[] specs =
        [
            ("OK", 90, 48),
            ("A19", 180, 48),
            ("LOT2026", 310, 52),
            ("BATCH-8841", 470, 54),
            ("SN-2026-9901A", 640, 56),
            ("MFG 2026.11.11 REV02", 820, 58),
        ];

        var patches = new PpOcrRoiPatch[specs.Length];
        var singleResults = new PpOcrRecognitionResult[specs.Length];
        var opts = new PpOcrRunOptions(
            Language: PpOcrLanguage.English,
            PipelineMode: PpOcrPipelineMode.RecOnly,
            PolarityMode: PpOcrPolarityMode.NormalOnly);

        for (int i = 0; i < specs.Length; i++)
        {
            using var mat = new Mat(specs[i].Height, specs[i].Width, MatType.CV_8UC1, Scalar.All(240));
            Cv2.PutText(mat, specs[i].Text, new Point(12, specs[i].Height - 14), HersheyFonts.HersheySimplex, 0.9, Scalar.All(20), thickness: 2, LineTypes.AntiAlias);
            byte[] buf = new byte[specs[i].Width * specs[i].Height];
            Marshal.Copy(mat.Data, buf, 0, buf.Length);
            patches[i] = new PpOcrRoiPatch(buf, specs[i].Width, specs[i].Height, specs[i].Width);
            singleResults[i] = service.RecognizeRoi(patches[i].AsView(), opts);
        }

        // Verify forward, reverse, and interleaved permutations all match single-ROI RecognizeRoi identically
        int[][] permutations =
        [
            [0, 1, 2, 3, 4, 5],
            [5, 4, 3, 2, 1, 0],
            [2, 5, 0, 4, 1, 3],
        ];

        foreach (int[] perm in permutations)
        {
            var permutedPatches = perm.Select(idx => patches[idx]).ToArray();
            IReadOnlyList<PpOcrRecognitionResult> batchRes = service.RecognizeBatch(permutedPatches, opts);
            Assert.Equal(perm.Length, batchRes.Count);

            for (int k = 0; k < perm.Length; k++)
            {
                int origIdx = perm[k];
                Assert.Equal(singleResults[origIdx].Region.FullText, batchRes[k].Region.FullText);
                Assert.Equal(singleResults[origIdx].Region.IsPass, batchRes[k].Region.IsPass);
                Assert.Equal(singleResults[origIdx].Region.MeanConfidence, batchRes[k].Region.MeanConfidence, precision: 5);
                Assert.Equal(singleResults[origIdx].Region.MinConfidence, batchRes[k].Region.MinConfidence, precision: 5);
            }
        }
    }

    [Fact]
    public void P3_DbPostProcessor_MinRectUnclipDistance_UsesMinAreaRectDimensions()
    {
        float dist = PpOcrDbPostProcessor.ComputeMinRectUnclipDistance(new Size2f(100f, 20f), unclipRatio: 1.5f);
        // Official PaddleOCR formula: (100 * 20 * 1.5) / (2 * (100 + 20)) = 3000 / 240 = 12.5
        Assert.Equal(12.5f, dist, precision: 5);
    }

    [Fact]
    public void P4_And_P5_SessionLifecycle_DisposedStatus_PartialInitCleanup_AndVocabularyValidation()
    {
        (_, string repoRootPath, _) = OcrBenchmarkDataset.LoadFromDefaultLocation();
        string modelDir = Path.Combine(repoRootPath, "models", "ppocr");

        // P4(a): Dispose transitions Status to PpOcrModelStatus.Disposed
        var service = new PpOcrInferenceService(PpOcrModelOptions.FromDirectory(modelDir, PpOcrExecutionDevice.Cpu));
        Assert.Equal(PpOcrModelStatus.Ready, service.Status);
        service.Dispose();
        Assert.Equal(PpOcrModelStatus.Disposed, service.Status);

        // P4(b) & P5: Valid det model + valid en model + mismatched/corrupt dict throws InvalidDataException cleanly without leaking det/en sessions
        string tempDir = Path.Combine(Path.GetTempPath(), $"PpOcr_P4P5_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string badDictPath = Path.Combine(tempDir, "bad_mid_empty_dict.txt");
            File.WriteAllText(badDictPath, "0\n\n1\n2\n", Encoding.UTF8);

            InvalidDataException midEmptyEx = Assert.Throws<InvalidDataException>(
                () => PpOcrSessionHost.LoadAndValidateCtcVocabulary(badDictPath, modelOutputClasses: 0));
            Assert.Contains("비어 있어", midEmptyEx.Message, StringComparison.Ordinal);

            string shortDictPath = Path.Combine(tempDir, "short_dict.txt");
            File.WriteAllText(shortDictPath, "0\n1\n2\n", Encoding.UTF8);
            InvalidDataException mismatchEx = Assert.Throws<InvalidDataException>(
                () => PpOcrSessionHost.LoadAndValidateCtcVocabulary(shortDictPath, modelOutputClasses: 97));
            Assert.Contains("일치하지 않습니다", mismatchEx.Message, StringComparison.Ordinal);

            // Partial init failure: valid Det model + corrupt English model throws InvalidDataException after disposing Det session
            string corruptEnPath = Path.Combine(tempDir, "corrupt_en.onnx");
            File.WriteAllBytes(corruptEnPath, [0x00, 0x01, 0x02, 0x03]);
            var partialFailOpts = new PpOcrModelOptions(
                DetectionModelPath: Path.Combine(modelDir, "ch_PP-OCRv4_det_mobile.onnx"),
                EnglishRecModelPath: corruptEnPath,
                EnglishDictionaryPath: Path.Combine(modelDir, "en_dict.txt"),
                KoreanRecModelPath: null,
                KoreanDictionaryPath: null,
                Device: PpOcrExecutionDevice.Cpu);
            Assert.Throws<InvalidDataException>(() => new PpOcrInferenceService(partialFailOpts));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void PpOcrMinors_OrientationInverseMapping_RecOnlyDetScoreZero_AndHighSpeedKoreanPreserved()
    {
        // Minor 3: CreateHighSpeedProduction preserves Korean instead of forcing English
        PpOcrRunOptions koFast = PpOcrRunOptions.CreateHighSpeedProduction(language: PpOcrLanguage.Korean);
        Assert.Equal(PpOcrLanguage.Korean, koFast.Language);
        PpOcrRunOptions multiFast = PpOcrRunOptions.CreateHighSpeedProduction(language: PpOcrLanguage.MultilingualConfidenceSelect);
        Assert.Equal(PpOcrLanguage.English, multiFast.Language);

        // Minor 1: Inverse coordinate mapping for Rotate180, Rotate90CCW, Rotate90CW stays inside [0..origW-1] x [0..origH-1]
        const int origW = 40;
        const int origH = 120;
        // For Rotate90CCW, rotated crop has width = origH (120), height = origW (40)
        var rotLine = new PpOcrCtcDecoder.DecodedCropLine(
            "V",
            [new PpOcrCharacterBox('V', 0.99f, 0, StartCol: 10, EndCol: 30, TopRow: 0, BottomRow: 39, PrecededBySpace: false)],
            0.99f,
            0.99f);

        PpOcrCtcDecoder.DecodedCropLine ccwMapped = PpOcrCtcDecoder.RemapOrientationToOriginalFrame(
            rotLine,
            PpOcrCtcDecoder.CropOrientation.Rotate90Counterclockwise,
            origW,
            origH);
        PpOcrCharacterBox ccwChar = Assert.Single(ccwMapped.Characters);
        Assert.InRange(ccwChar.StartCol, 0, origW - 1);
        Assert.InRange(ccwChar.EndCol, ccwChar.StartCol, origW - 1);
        Assert.Equal(10, ccwChar.TopRow);
        Assert.Equal(30, ccwChar.BottomRow);

        PpOcrCtcDecoder.DecodedCropLine cwMapped = PpOcrCtcDecoder.RemapOrientationToOriginalFrame(
            rotLine,
            PpOcrCtcDecoder.CropOrientation.Rotate90Clockwise,
            origW,
            origH);
        PpOcrCharacterBox cwChar = Assert.Single(cwMapped.Characters);
        Assert.InRange(cwChar.StartCol, 0, origW - 1);
        Assert.InRange(cwChar.EndCol, cwChar.StartCol, origW - 1);
        Assert.Equal((origH - 1) - 30, cwChar.TopRow);
        Assert.Equal((origH - 1) - 10, cwChar.BottomRow);

        var rot180Input = new PpOcrCtcDecoder.DecodedCropLine(
            "R",
            [new PpOcrCharacterBox('R', 0.99f, 0, StartCol: 5, EndCol: 15, TopRow: 2, BottomRow: 18, PrecededBySpace: false)],
            0.99f,
            0.99f);
        PpOcrCtcDecoder.DecodedCropLine r180Mapped = PpOcrCtcDecoder.RemapOrientationToOriginalFrame(
            rot180Input,
            PpOcrCtcDecoder.CropOrientation.Rotate180,
            originalWidth: 100,
            originalHeight: 30);
        PpOcrCharacterBox r180Char = Assert.Single(r180Mapped.Characters);
        Assert.Equal(99 - 15, r180Char.StartCol);
        Assert.Equal(99 - 5, r180Char.EndCol);
        Assert.Equal(29 - 18, r180Char.TopRow);
        Assert.Equal(29 - 2, r180Char.BottomRow);

        // Minor 2: RecOnly reports DetScore == 0f and IsDetectedQuad == false (never fake DetScore == 1.0f)
        (_, string repoRootPath, _) = OcrBenchmarkDataset.LoadFromDefaultLocation();
        string modelDir = Path.Combine(repoRootPath, "models", "ppocr");
        using var service = new PpOcrInferenceService(PpOcrModelOptions.FromDirectory(modelDir, PpOcrExecutionDevice.Cpu));
        (byte[] px, int w, int h) = RenderSyntheticGrayLine("LOT2026", darkOnLight: true);
        PpOcrRecognitionResult recOnlyRes = service.RecognizeRoi(
            new ReadOnlyImageView2D<byte>(px, w, h, w),
            new PpOcrRunOptions(PipelineMode: PpOcrPipelineMode.RecOnly));
        Assert.False(recOnlyRes.UsedDetectionStage);
        PpOcrBoxResult recBox = Assert.Single(recOnlyRes.Boxes);
        Assert.Equal(0f, recBox.DetScore);
        Assert.False(recBox.IsDetectedQuad);
    }

    private static double MeasureMedianMs(int iterations, Action action)
    {
        var samples = new double[iterations];
        for (int i = 0; i < iterations; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            action();
            samples[i] = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }

        Array.Sort(samples);
        return samples[iterations / 2];
    }

    private static OcvVerificationOptions BuildKensaOcvOptions(int width, int height, int expectedLen, bool darkOnLight)
    {
        int expectedPitch = Math.Clamp(width / Math.Max(1, expectedLen), 5, 56);
        int minCharWidth = Math.Clamp(height / 12, 2, 4);
        int maxCharWidth = Math.Max(8, (int)MathF.Ceiling(MathF.Max(height * 1.15f, expectedPitch * 1.45f)));
        return new OcvVerificationOptions(
            PrintMethod: PrintMethodType.ContinuousLaser,
            MinCharWidth: minCharWidth,
            MaxCharWidth: maxCharWidth,
            ExpectedPitch: expectedPitch,
            DotClosingRadiusX: 0,
            DotClosingRadiusY: 0,
            DarkOnLight: darkOnLight);
    }

    private static PpOcrRoiPatch CropPatchFromMat(Mat gray, OcrBenchmarkEntry entry)
        => CropRectFromMat(gray, entry.CenterX, entry.CenterY, entry.Width, entry.Height);

    private static PpOcrRoiPatch CropRectFromMat(Mat gray, double cx, double cy, double w, double h)
    {
        int imgW = gray.Width;
        int imgH = gray.Height;
        int x0 = Math.Clamp((int)Math.Round(cx - (w * 0.5)), 0, imgW - 1);
        int y0 = Math.Clamp((int)Math.Round(cy - (h * 0.5)), 0, imgH - 1);
        int x1 = Math.Clamp((int)Math.Round(cx + (w * 0.5)), x0 + 1, imgW);
        int y1 = Math.Clamp((int)Math.Round(cy + (h * 0.5)), y0 + 1, imgH);
        int rw = x1 - x0;
        int rh = y1 - y0;
        using var sub = new Mat(gray, new Rect(x0, y0, rw, rh));
        using Mat contig = sub.Clone();
        byte[] buf = new byte[rw * rh];
        Marshal.Copy(contig.Data, buf, 0, buf.Length);
        return new PpOcrRoiPatch(buf, rw, rh, rw);
    }

    private static (byte[] Pixels, int Width, int Height) RenderSyntheticGrayLine(string text, bool darkOnLight)
    {
        int w = 320;
        int h = 56;
        byte bg = darkOnLight ? (byte)240 : (byte)24;
        byte fg = darkOnLight ? (byte)20 : (byte)235;
        using var mat = new Mat(h, w, MatType.CV_8UC1, Scalar.All(bg));
        Cv2.PutText(mat, text, new Point(18, 40), HersheyFonts.HersheySimplex, 1.05, Scalar.All(fg), thickness: 2, LineTypes.AntiAlias);
        byte[] bytes = new byte[w * h];
        Marshal.Copy(mat.Data, bytes, 0, bytes.Length);
        return (bytes, w, h);
    }

    private static (byte[] Pixels, int Width, int Height) RenderPerspectiveSkewedGrayLine(string text, double angleDeg)
    {
        int w = 360;
        int h = 120;
        using var src = new Mat(h, w, MatType.CV_8UC1, Scalar.All(242));
        Cv2.PutText(src, text, new Point(55, 72), HersheyFonts.HersheySimplex, 1.25, Scalar.All(18), thickness: 3, LineTypes.AntiAlias);

        using Mat rot = Cv2.GetRotationMatrix2D(new Point2f(w * 0.5f, h * 0.5f), angleDeg, 1.0);
        using var rotated = new Mat();
        Cv2.WarpAffine(src, rotated, rot, new Size(w, h), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(242));

        Point2f[] srcQuad = [new(0, 0), new(w - 1, 0), new(w - 1, h - 1), new(0, h - 1)];
        Point2f[] dstQuad = [new(8, 6), new(w - 12, 2), new(w - 4, h - 5), new(2, h - 10)];
        using Mat persp = Cv2.GetPerspectiveTransform(srcQuad, dstQuad);
        using var warped = new Mat();
        Cv2.WarpPerspective(rotated, warped, persp, new Size(w, h), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(242));

        byte[] bytes = new byte[w * h];
        Marshal.Copy(warped.Data, bytes, 0, bytes.Length);
        return (bytes, w, h);
    }
}
