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
            unwiredVm.SelectedAlgorithm = InspectionSuiteAlgorithmKind.IndustrialOcrOcv;
            Assert.Equal(InspectionSuiteAlgorithmKind.PpOcrOnnx, unwiredVm.SelectedAlgorithm);
            unwiredVm.SelectedAlgorithm = InspectionSuiteAlgorithmKind.LightweightOcr;
            Assert.Equal(InspectionSuiteAlgorithmKind.PpOcrOnnx, unwiredVm.SelectedAlgorithm);
            unwiredVm.SelectedAlgorithm = InspectionSuiteAlgorithmKind.CpuDeepOcr;
            Assert.Equal(InspectionSuiteAlgorithmKind.PpOcrOnnx, unwiredVm.SelectedAlgorithm);

            // 3) Wired PP-OCRv4 + OCV: 1-click ApplyPpOcrTextToOcvExpectedText copies recognized text to SelectedRoiOcvExpectedText
            string modelDir = Path.Combine(repoRootPath, "models", "ppocr");
            using var ppOcrService = new PpOcrInferenceService(PpOcrModelOptions.FromDirectory(modelDir, PpOcrExecutionDevice.Cpu));
            var ocvService = new TaughtFontOcvService(loader, () => null);
            using var wiredVm = new InspectionSuiteViewModel(
                new InspectionSuiteService(loader, () => null, ppOcrService),
                ocvService);

            wiredVm.SelectOcrCategory.Execute(null);
            Assert.Single(wiredVm.CategoryAlgorithms);
            Assert.True(wiredVm.IsPpOcrWired);
            Assert.True(wiredVm.IsPpOcrOnnxSelected);

            string synthImgPath = Path.Combine(tempDir, "copy_test.png");
            (byte[] px, int w, int h) = RenderSyntheticGrayLine("LOT2026A", darkOnLight: true);
            using (var mat = new Mat(h, w, MatType.CV_8UC1))
            {
                Marshal.Copy(px, 0, mat.Data, px.Length);
                Cv2.ImWrite(synthImgPath, mat);
            }

            var session = new EditorSession();
            var imageRef = new ImageReference(synthImgPath, "copy_test.png", w, h);
            session.InitializeImage(imageRef, new ViewportTransform(1.0, default));
            Guid roiId = session.Add(new RoiGeometry(new PointD(w * 0.5, h * 0.5), w - 8, h - 4, 0.0));
            session.Select([roiId]);

            wiredVm.Update(new InspectionUpdate(session.State, Generation: 1, Live: false, CanExecute: true, ToolAvailable: true, TransformPreview: null));
            await wiredVm.ExecuteSelected.ExecuteAsync();

            Assert.True(wiredVm.ApplyPpOcrTextToOcvExpectedText.CanExecute(null));
            wiredVm.ApplyPpOcrTextToOcvExpectedText.Execute(null);
            Assert.Equal("LOT2026A", wiredVm.SelectedRoiOcvExpectedText);
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
