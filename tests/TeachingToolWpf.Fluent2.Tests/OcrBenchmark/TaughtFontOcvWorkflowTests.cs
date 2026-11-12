using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;
using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Models;
using TeachingToolWpf.Services;
using TeachingToolWpf.ViewModels;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.IndustrialOcrOcv;
using VisionAlgorithm.OpenSrc.Synthetic8kTestGen;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionCanvas.Imaging.Filters;
using VisionCanvas.Presentation;
using VisionGeometry;
using VisionImaging;
using VisionImaging.Abstractions;
using VisionOcr.PpOcr;
using Xunit;
using Xunit.Abstractions;

namespace TeachingToolWpf.Fluent2.Tests.OcrBenchmark;

public sealed class TaughtFontOcvWorkflowTests(ITestOutputHelper output)
{
    private const int ViewportWidth = 1280;
    private const int ViewportHeight = 800;

    [Fact]
    public async Task TeachAndVerify_OnSyntheticImage_ProducesDistinctOkReviewAndNgWithOverlayColors()
    {
        string tempDir = CreateTempDir();
        try
        {
            string imgPath = Path.Combine(tempDir, "synthetic_ocv_tri_state.png");
            CreateSyntheticOcvSheet(imgPath, out int imgW, out int imgH);

            IImageLoader loader = ImageLoaderHost.Create(Path.Combine(tempDir, "cache"));
            var ocvService = new TaughtFontOcvService(loader, () => null);
            var suiteVm = new InspectionSuiteViewModel(new InspectionSuiteService(loader, () => null), ocvService);
            var recordedLogs = new List<StructuredInspectionLogEntry>();
            suiteVm.StructuredLogRecorded += recordedLogs.Add;

            using var display = new NullImageDisplay();
            using MainWindowViewModel studio = CreateStudioWithSuite(display, loader, suiteVm);
            studio.Inspections.Selected = InspectionToolKind.InspectionSuite;

            var imageRef = new ImageReference(imgPath, "synthetic_ocv_tri_state.png", imgW, imgH);
            studio.Session.InitializeImage(imageRef, new ViewportTransform(1.0, default));

            // 1) Add reference ROI ("LOT2026", high contrast 240 bg / 20 fg) and teach golden font
            Guid refRoiId = studio.Session.Add(new RoiGeometry(new PointD(220, 70), 260, 44, 0));
            studio.Session.Select([refRoiId]);
            suiteVm.SelectedRoiOcvExpectedText = "LOT2026";

            Assert.True(suiteVm.TeachSelectedRoiOcvFont.CanExecute(null));
            suiteVm.TeachSelectedRoiOcvFont.Execute(null);
            await WaitUntilIdleAsync(suiteVm);

            Assert.True(suiteVm.HasGoldenOcvFont);
            Assert.Equal("TEACH OK", suiteVm.OcvLastVerdictBadge);
            Assert.Equal(7, ocvService.RegisteredCharacterCount); // L, O, T, 2, 0, 2, 6 (7 glyph templates)

            // 2) Add OK target ROI ("LOT2026" crisp), Review target ROI ("LOT2026" ~28 GV borderline contrast), NG target ROI ("LOT2826" wrong char)
            Guid okRoiId = studio.Session.Add(new RoiGeometry(new PointD(220, 150), 260, 44, 0));
            studio.Session.Select([okRoiId]);
            suiteVm.SelectedRoiOcvExpectedText = "LOT2026";
            suiteVm.SelectedRoiOcvFormatMask = "AAADDDD";

            Guid reviewRoiId = studio.Session.Add(new RoiGeometry(new PointD(220, 230), 260, 44, 0));
            studio.Session.Select([reviewRoiId]);
            suiteVm.SelectedRoiOcvExpectedText = "LOT2026";
            suiteVm.SelectedRoiOcvFormatMask = "AAADDDD";

            Guid ngRoiId = studio.Session.Add(new RoiGeometry(new PointD(220, 310), 260, 44, 0));
            studio.Session.Select([ngRoiId]);
            suiteVm.SelectedRoiOcvExpectedText = "LOT2026";
            suiteVm.SelectedRoiOcvFormatMask = "AAADDDD";

            // Lower foreground contrast offset slightly so the 28 GV borderline strip segments cleanly into all 7 chars
            suiteVm.OcvForegroundContrast = 8.0f;

            // Verify OK ROI
            studio.Session.Select([okRoiId]);
            suiteVm.VerifySelectedRoiOcv.Execute(null);
            await WaitUntilIdleAsync(suiteVm);
            OcvRoiInspectionItem okItem = Assert.Single(suiteVm.OcvVerificationItems);
            Assert.Equal(OcvVerdictKind.Ok, okItem.Verdict);
            Assert.Equal(TaughtFontOcvService.AccentOk, okItem.VerdictAccent);

            // Verify Review ROI (borderline contrast ~28 GV: >= MinContrastGv 22, < ReviewContrastGv 34)
            studio.Session.Select([reviewRoiId]);
            suiteVm.VerifySelectedRoiOcv.Execute(null);
            await WaitUntilIdleAsync(suiteVm);
            OcvRoiInspectionItem reviewItem = Assert.Single(suiteVm.OcvVerificationItems);
            Assert.Equal(OcvVerdictKind.Review, reviewItem.Verdict);
            Assert.Equal(TaughtFontOcvService.AccentReview, reviewItem.VerdictAccent);
            Assert.Contains("제품 NG", reviewItem.VerdictBadge, StringComparison.Ordinal);

            // Verify NG ROI ("LOT2826" vs expected "LOT2026")
            studio.Session.Select([ngRoiId]);
            suiteVm.VerifySelectedRoiOcv.Execute(null);
            await WaitUntilIdleAsync(suiteVm);
            OcvRoiInspectionItem ngItem = Assert.Single(suiteVm.OcvVerificationItems);
            Assert.Equal(OcvVerdictKind.Ng, ngItem.Verdict);
            Assert.Equal(TaughtFontOcvService.AccentNg, ngItem.VerdictAccent);
            Assert.NotEqual(reviewItem.VerdictAccent, ngItem.VerdictAccent);

            Assert.Contains(recordedLogs, l => l.RoiId == okRoiId && l.Verdict == InspectionVerdict.Pass);
            Assert.Contains(recordedLogs, l => l.RoiId == reviewRoiId && l.Verdict == InspectionVerdict.Review);
            Assert.Contains(recordedLogs, l => l.RoiId == ngRoiId && l.Verdict == InspectionVerdict.Fail);
        }
        finally
        {
            TryDeleteTempDir(tempDir);
        }
    }

    [Fact]
    public async Task TeachMismatch_WhenSegmentedCountDiffersFromExpected_RejectsLoudlyWithoutOverwritingGoldenFont()
    {
        string tempDir = CreateTempDir();
        try
        {
            string imgPath = Path.Combine(tempDir, "synthetic_ocv_mismatch.png");
            CreateSyntheticOcvSheet(imgPath, out int imgW, out int imgH);

            IImageLoader loader = ImageLoaderHost.Create(Path.Combine(tempDir, "cache"));
            var ocvService = new TaughtFontOcvService(loader, () => null);
            var suiteVm = new InspectionSuiteViewModel(new InspectionSuiteService(loader, () => null), ocvService);
            using var display = new NullImageDisplay();
            using MainWindowViewModel studio = CreateStudioWithSuite(display, loader, suiteVm);
            studio.Inspections.Selected = InspectionToolKind.InspectionSuite;

            var imageRef = new ImageReference(imgPath, "synthetic_ocv_mismatch.png", imgW, imgH);
            studio.Session.InitializeImage(imageRef, new ViewportTransform(1.0, default));

            Guid roiId = studio.Session.Add(new RoiGeometry(new PointD(220, 70), 260, 44, 0));
            studio.Session.Select([roiId]);

            // First teach valid 7-char "LOT2026"
            suiteVm.SelectedRoiOcvExpectedText = "LOT2026";
            suiteVm.TeachSelectedRoiOcvFont.Execute(null);
            await WaitUntilIdleAsync(suiteVm);
            Assert.True(ocvService.HasGoldenFont);
            Assert.Equal(7, ocvService.RegisteredCharacterCount);

            // Now attempt non-append teach with mismatched 2-char string "AB" on the 7-char ROI
            suiteVm.OcvAppendTeachMode = false;
            suiteVm.SelectedRoiOcvExpectedText = "AB";
            suiteVm.TeachSelectedRoiOcvFont.Execute(null);
            await WaitUntilIdleAsync(suiteVm);

            Assert.Equal("TEACH 거부", suiteVm.OcvLastVerdictBadge);
            Assert.Contains("분할 글자 수", suiteVm.Status, StringComparison.Ordinal);
            Assert.Contains("원인:", suiteVm.Results, StringComparison.Ordinal);
            // Existing golden font MUST remain intact and NOT be overwritten or cleared!
            Assert.True(ocvService.HasGoldenFont);
            Assert.Equal(7, ocvService.RegisteredCharacterCount);
        }
        finally
        {
            TryDeleteTempDir(tempDir);
        }
    }

    [Fact]
    public async Task FontModelSaveLoad_RoundTripsVersionedJson_AndRejectsCorruptOrWrongVersion()
    {
        string tempDir = CreateTempDir();
        try
        {
            string imgPath = Path.Combine(tempDir, "synthetic_ocv_saveload.png");
            string fontPath = Path.Combine(tempDir, "taught_font_v1.ocvfont.json");
            CreateSyntheticOcvSheet(imgPath, out int imgW, out int imgH);

            IImageLoader loader = ImageLoaderHost.Create(Path.Combine(tempDir, "cache"));
            var ocvService = new TaughtFontOcvService(loader, () => null);
            var suiteVm = new InspectionSuiteViewModel(
                new InspectionSuiteService(loader, () => null),
                ocvService,
                selectOcvFontOpenPath: () => fontPath,
                selectOcvFontSavePath: () => fontPath);
            using var display = new NullImageDisplay();
            using MainWindowViewModel studio = CreateStudioWithSuite(display, loader, suiteVm);
            studio.Inspections.Selected = InspectionToolKind.InspectionSuite;

            var imageRef = new ImageReference(imgPath, "synthetic_ocv_saveload.png", imgW, imgH);
            studio.Session.InitializeImage(imageRef, new ViewportTransform(1.0, default));

            Guid roiId = studio.Session.Add(new RoiGeometry(new PointD(220, 70), 260, 44, 0));
            studio.Session.Select([roiId]);
            suiteVm.SelectedRoiOcvExpectedText = "LOT2026";

            suiteVm.TeachSelectedRoiOcvFont.Execute(null);
            await WaitUntilIdleAsync(suiteVm);
            Assert.True(suiteVm.SaveOcvFontModel.CanExecute(null));
            suiteVm.SaveOcvFontModel.Execute(null);
            Assert.True(File.Exists(fontPath));

            suiteVm.VerifySelectedRoiOcv.Execute(null);
            await WaitUntilIdleAsync(suiteVm);
            float beforeMeanScore = Assert.Single(suiteVm.OcvVerificationItems).MeanScore;

            suiteVm.ClearOcvFontModel.Execute(null);
            Assert.False(ocvService.HasGoldenFont);

            suiteVm.LoadOcvFontModel.Execute(null);
            Assert.True(ocvService.HasGoldenFont);
            Assert.Equal(7, ocvService.RegisteredCharacterCount);

            suiteVm.VerifySelectedRoiOcv.Execute(null);
            await WaitUntilIdleAsync(suiteVm);
            float afterMeanScore = Assert.Single(suiteVm.OcvVerificationItems).MeanScore;
            Assert.Equal(beforeMeanScore, afterMeanScore, precision: 5);

            // Corrupt version & corrupt JSON must fail loudly with InvalidDataException
            string savedJson = File.ReadAllText(fontPath, Encoding.UTF8);
            string wrongVerJson = System.Text.RegularExpressions.Regex.Replace(
                savedJson,
                "\"formatVersion\"\\s*:\\s*\\d+",
                "\"formatVersion\": 999");
            using var wrongVerStream = new MemoryStream(Encoding.UTF8.GetBytes(wrongVerJson));
            Assert.Throws<InvalidDataException>(() => IndustrialOcrFontModelSerializer.Load(wrongVerStream));

            using var corruptStream = new MemoryStream(Encoding.UTF8.GetBytes("{corrupt_json"));
            Assert.Throws<InvalidDataException>(() => IndustrialOcrFontModelSerializer.Load(corruptStream));
        }
        finally
        {
            TryDeleteTempDir(tempDir);
        }
    }

    [Fact]
    public void PerRoiOcvSettings_SupportDocumentUndoAndRedo_ThroughMainWindowViewModel()
    {
        string tempDir = CreateTempDir();
        try
        {
            IImageLoader loader = ImageLoaderHost.Create(Path.Combine(tempDir, "cache"));
            var ocvService = new TaughtFontOcvService(loader, () => null);
            var suiteVm = new InspectionSuiteViewModel(new InspectionSuiteService(loader, () => null), ocvService);
            using var display = new NullImageDisplay();
            using MainWindowViewModel studio = CreateStudioWithSuite(display, loader, suiteVm);
            studio.Inspections.Selected = InspectionToolKind.InspectionSuite;

            var imageRef = new ImageReference("mem://test", "test", 800, 600);
            studio.Session.InitializeImage(imageRef, new ViewportTransform(1.0, default));

            Guid roiId = studio.Session.Add(new RoiGeometry(new PointD(200, 120), 180, 40, 0));
            studio.Session.Select([roiId]);

            suiteVm.SelectedRoiOcvExpectedText = "BATCH01";
            suiteVm.SelectedRoiOcvFormatMask = "AAAAADD";

            Assert.Equal("BATCH01", studio.Session.State.Rois[roiId].Ocv.ExpectedText);
            Assert.Equal("AAAAADD", studio.Session.State.Rois[roiId].Ocv.FormatMask);

            // Undo FormatMask -> Undo ExpectedText
            Assert.True(studio.Session.CanUndo);
            studio.Session.Undo();
            Assert.Equal("BATCH01", suiteVm.SelectedRoiOcvExpectedText);
            Assert.Equal(string.Empty, suiteVm.SelectedRoiOcvFormatMask);

            Assert.True(studio.Session.CanUndo);
            studio.Session.Undo();
            Assert.Equal(string.Empty, suiteVm.SelectedRoiOcvExpectedText);

            // Redo ExpectedText -> Redo FormatMask
            Assert.True(studio.Session.CanRedo);
            studio.Session.Redo();
            Assert.True(studio.Session.CanRedo);
            studio.Session.Redo();
            Assert.Equal("BATCH01", suiteVm.SelectedRoiOcvExpectedText);
            Assert.Equal("AAAAADD", suiteVm.SelectedRoiOcvFormatMask);
        }
        finally
        {
            TryDeleteTempDir(tempDir);
        }
    }

    [Fact]
    public async Task RealKensaImage_EndToEndViewModelTeachChip1AndVerifyChips2To4_RecordsHonestResults()
    {
        (OcrBenchmarkDataset dataset, string repoRootPath, _) = OcrBenchmarkDataset.LoadFromDefaultLocation();
        OcrPrintGroupSpec kensaGroup = dataset.PrintGroups.Single(g => g.GroupId == "pg_kensa_chip_laser");

        string tempDir = CreateTempDir();
        try
        {
            string kensaPath = Path.Combine(repoRootPath, "kensa.png");
            Assert.True(File.Exists(kensaPath));
            Assert.NotNull(kensaGroup.OcvSpecimens);

            IImageLoader loader = ImageLoaderHost.Create(Path.Combine(tempDir, "cache"));
            var ocvService = new TaughtFontOcvService(loader, () => null);
            var suiteVm = new InspectionSuiteViewModel(new InspectionSuiteService(loader, () => null), ocvService);
            using var display = new NullImageDisplay();
            using MainWindowViewModel studio = CreateStudioWithSuite(display, loader, suiteVm);
            studio.Inspections.Selected = InspectionToolKind.InspectionSuite;

            OcrBenchmarkEntry firstVerify = dataset.Entries.First(e => e.Id.StartsWith("Kensa_Chip2_", StringComparison.Ordinal));
            var imageRef = new ImageReference(kensaPath, "kensa.png", firstVerify.ImageWidth, firstVerify.ImageHeight);
            studio.Session.InitializeImage(imageRef, new ViewportTransform(1.0, default));

            // 1) Teach from Chip 1 ("K9X8G08U0M" + "SAMSUNG") via UI ViewModel commands
            suiteVm.SelectedOcvPolarity = kensaGroup.DarkOnLight
                ? OcvPolaritySelection.DarkOnLight
                : OcvPolaritySelection.LightOnDark;
            suiteVm.OcvAppendTeachMode = true;
            foreach (OcrOcvSpecimenEntry tr in kensaGroup.OcvSpecimens)
            {
                Guid teachRoiId = studio.Session.Add(new RoiGeometry(new PointD(tr.CenterX, tr.CenterY), tr.Width, tr.Height, 0));
                studio.Session.Select([teachRoiId]);
                suiteVm.SelectedRoiOcvExpectedText = tr.GroundTruth;
                suiteVm.TeachSelectedRoiOcvFont.Execute(null);
                await WaitUntilIdleAsync(suiteVm);
                Assert.Equal("TEACH OK", suiteVm.OcvLastVerdictBadge);
            }

            // Clear Chip 1 teach ROIs from canvas so VerifyAllRoisOcv verifies Chips 2..4 (6 ROIs) cleanly
            studio.Session.SelectAll();
            studio.Session.DeleteSelection();

            string[] chip2To4Ids =
            [
                "Kensa_Chip2_Part", "Kensa_Chip2_Vendor",
                "Kensa_Chip3_Part", "Kensa_Chip3_Vendor",
                "Kensa_Chip4_Part", "Kensa_Chip4_Vendor",
            ];

            foreach (string entryId in chip2To4Ids)
            {
                OcrBenchmarkEntry ve = dataset.Entries.Single(e => e.Id == entryId);
                Guid verifyRoiId = studio.Session.Add(new RoiGeometry(new PointD(ve.CenterX, ve.CenterY), ve.Width, ve.Height, 0));
                studio.Session.Select([verifyRoiId]);
                suiteVm.SelectedRoiOcvExpectedText = ve.GroundTruthJoined;
            }

            suiteVm.VerifyAllRoisOcv.Execute(null);
            await WaitUntilIdleAsync(suiteVm);

            Assert.Equal(6, suiteVm.OcvVerificationItems.Count);
            Assert.NotNull(suiteVm.Overlay);

            var sb = new StringBuilder();
            sb.AppendLine("=== REAL KENSA.PNG UI VIEWMODEL OCV WORKFLOW (TEACH CHIP 1 -> VERIFY CHIPS 2..4) ===");
            foreach (OcvRoiInspectionItem item in suiteVm.OcvVerificationItems)
            {
                sb.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  [{item.RoiName}] Verdict={item.Verdict} ({item.VerdictBadge}) Expected=\"{item.ExpectedText}\" Decoded=\"{item.DecodedText}\" Mean={item.MeanScore:F3} Min={item.MinScore:F3} Cause=\"{item.CauseSummary}\""));
                Assert.NotEmpty(item.Characters);
            }

            output.WriteLine(sb.ToString());
        }
        finally
        {
            TryDeleteTempDir(tempDir);
        }
    }

    [Fact]
    public void B1_MaskConfirmationBias_OutOfMaskHomoglyph_12B4WithMaskDDDD_IsRejectedAsNg()
    {
        // Train a golden font containing both 'B' and '8' alongside '1', '2', '4'
        char[] alphabet = ['1', '2', '4', '8', 'B'];
        var samples = new List<GoldenCharSample>(alphabet.Length);
        foreach (char ch in alphabet)
        {
            byte[] patch = UnifiedTestImageSuiteGenerator.RenderCanonicalGlyphPatch(
                ch,
                fullWidth: 24,
                fullHeight: 34,
                out int cw,
                out int chH,
                continuousStroke: true);
            samples.Add(new GoldenCharSample(ch, patch, cw, chH, cw, DarkOnLight: true));
        }

        IndustrialOcrFontModel goldenFont = IndustrialOcrTrainer.Train(CollectionsMarshal.AsSpan(samples));
        var verifier = new TaughtFontOcvVerifier(goldenFont);
        using var workspace = new VisionWorkspace();
        var options = new OcvVerificationOptions(
            PrintMethod: PrintMethodType.ContinuousLaser,
            MinCharWidth: 5,
            MaxCharWidth: 28,
            ExpectedPitch: 24,
            RectifiedWidth: 220,
            RectifiedHeight: 40);
        var spec = new OcrFieldFormatSpec(FormatMask: "DDDD", ExpectedText: "1284", StrictLengthMatch: true);

        // 1) Genuine print "1284" must pass as OK
        byte[] okPixels = UnifiedTestImageSuiteGenerator.CreateSingleLineTextImage(
            width: 320,
            height: 80,
            text: "1284",
            continuousStroke: true,
            kerningScale: 1.0f,
            dotDropoutRatio: 0f,
            invertPolarity: false,
            strokeDefectCharIndex: -1,
            strokeDefectMissing: false,
            illuminationSlope: 0f,
            rotationDeg: 0f,
            lowContrast: false,
            out FixturePose2D okPose);
        OcvVerificationResult okRes = verifier.VerifyLine(
            new ReadOnlyImageView2D<byte>(okPixels, 320, 80, 320),
            in okPose,
            spec,
            options,
            workspace);
        Assert.Equal(OcvVerdictKind.Ok, okRes.Verdict);
        Assert.Equal("1284", okRes.DecodedText);

        // 2) Counterexample from B1: misprint "12B4" against mask "DDDD" & expected "1284" MUST be rejected as NG!
        byte[] misprintPixels = UnifiedTestImageSuiteGenerator.CreateSingleLineTextImage(
            width: 320,
            height: 80,
            text: "12B4",
            continuousStroke: true,
            kerningScale: 1.0f,
            dotDropoutRatio: 0f,
            invertPolarity: false,
            strokeDefectCharIndex: -1,
            strokeDefectMissing: false,
            illuminationSlope: 0f,
            rotationDeg: 0f,
            lowContrast: false,
            out FixturePose2D misprintPose);
        OcvVerificationResult misprintRes = verifier.VerifyLine(
            new ReadOnlyImageView2D<byte>(misprintPixels, 320, 80, 320),
            in misprintPose,
            spec,
            options,
            workspace);

        Assert.Equal(OcvVerdictKind.Ng, misprintRes.Verdict);
        Assert.False(misprintRes.IsOk);
        Assert.Equal("12B4", misprintRes.DecodedText);
        Assert.Equal('B', misprintRes.Characters[2].ObservedChar);
        Assert.Equal(OcvVerdictKind.Ng, misprintRes.Characters[2].Verdict);
        Assert.Contains("형식 밖 문자", misprintRes.Characters[2].Cause, StringComparison.Ordinal);
    }

    [Fact]
    public void B2_UiMaskExamples_ParseAndMatchInOcrFieldFormatSpec()
    {
        var simpleSpec = new OcrFieldFormatSpec(FormatMask: "AAAADDDD");
        Assert.Equal(8, simpleSpec.SlotCount);
        Assert.True(MatchesEntireString(simpleSpec, "ABCD1234"));
        Assert.False(MatchesEntireString(simpleSpec, "ABCD12B4"));
        Assert.False(MatchesEntireString(simpleSpec, "abcd1234"));

        var lotSpec = new OcrFieldFormatSpec(FormatMask: @"LOT\-DDDD\-ADD");
        Assert.Equal(12, lotSpec.SlotCount);
        Assert.True(MatchesEntireString(lotSpec, "LOT-2026-A09"));
        Assert.False(MatchesEntireString(lotSpec, "LOTX2026-A09"));
        Assert.False(MatchesEntireString(lotSpec, "LOT-2026-909"));

        var hexBracketSpec = new OcrFieldFormatSpec(FormatMask: "aX#[0-9A-F]");
        Assert.Equal(4, hexBracketSpec.SlotCount);
        Assert.True(MatchesEntireString(hexBracketSpec, "z9@F"));
        Assert.False(MatchesEntireString(hexBracketSpec, "Z9@F"));
        Assert.False(MatchesEntireString(hexBracketSpec, "z9@G"));
    }

    [Fact]
    public async Task M1_And_M2_ApplyPpOcrToOcv_And_MultiRoiSelection_EnforceSafetyInvariants()
    {
        string tempDir = CreateTempDir();
        try
        {
            IImageLoader loader = ImageLoaderHost.Create(Path.Combine(tempDir, "cache"));
            Guid roiA = Guid.Empty;
            Guid roiB = Guid.Empty;
            bool nextPassVerdict = true;
            float nextMeanConf = 0.998f;
            float nextMinConf = 0.992f;
            Guid? overrideResultRoiId = null;

            var stubSuiteService = new StubPpOcrSuiteService((targetRois, _) =>
            {
                Guid targetId = overrideResultRoiId ?? targetRois[0].RoiId;
                var diag = new OcrDiagnosticSnapshot(
                    PreviewWidth: 32,
                    PreviewHeight: 16,
                    EnhancedGrayPixels: [.. new byte[32 * 16]],
                    BinarizedRgbaPixels: [.. new byte[32 * 16 * 4]],
                    RecognizedText: "LOT2026",
                    Lines: ["LOT2026"],
                    Characters: [new PpOcrCharacterBox('L', nextMinConf, 0, 0, 10, 0, 15, false)],
                    MeanConfidence: nextMeanConf,
                    MinConfidence: nextMinConf,
                    AcceptThreshold: 0.995f,
                    ForegroundContrastOffset: 0f,
                    DarkOnLight: true,
                    PreprocessingBadge: "PP-OCRv4");
                var item = new InspectionSuiteItemResult(
                    RoiId: targetId,
                    Name: targetRois[0].Name,
                    IsPass: nextPassVerdict,
                    BadgeLabel: nextPassVerdict ? "OK" : "거부",
                    DetailLine: "LOT2026",
                    ReferencePoint: targetRois[0].Center,
                    AngleRadians: 0,
                    Bounds: targetRois[0].Outline,
                    AdditionalContours: [],
                    PrimaryPoints: [],
                    SecondaryPoints: [],
                    DefectOrOutlierPoints: [],
                    ElapsedMicroseconds: 1000,
                    OcrDiagnostic: diag);
                return new InspectionSuiteExecutionOutput(
                    InspectionSuiteAlgorithmKind.PpOcrOnnx,
                    "완료",
                    "상세",
                    [item],
                    1.0);
            });

            bool confirmResult = false;
            int confirmCallCount = 0;
            var ocvService = new TaughtFontOcvService(loader, () => null);
            var suiteVm = new InspectionSuiteViewModel(
                stubSuiteService,
                ocvService,
                confirmApplyPpOcrToOcv: (_, _, _) =>
                {
                    confirmCallCount++;
                    return confirmResult;
                });

            using var display = new NullImageDisplay();
            using MainWindowViewModel studio = CreateStudioWithSuite(display, loader, suiteVm);
            studio.Inspections.Selected = InspectionToolKind.InspectionSuite;
            studio.Session.InitializeImage(new ImageReference("mem://m1m2", "m1m2", 800, 600), new ViewportTransform(1.0, default));

            roiA = studio.Session.Add(new RoiGeometry(new PointD(200, 100), 180, 40, 0));
            studio.Session.Select([roiA]);
            suiteVm.SelectedRoiOcvExpectedText = "ROI_A_INIT";

            roiB = studio.Session.Add(new RoiGeometry(new PointD(200, 200), 180, 40, 0));
            studio.Session.Select([roiB]);
            suiteVm.SelectedRoiOcvExpectedText = "ROI_B_INIT";

            // M2: Select multiple ROIs -> editing is disabled and setting properties does NOT overwrite sibling ROIs
            studio.Session.Select([roiA, roiB]);
            Assert.False(suiteVm.CanEditSelectedRoiOcvSettings);
            suiteVm.SelectedRoiOcvExpectedText = "OVERWRITE_ATTEMPT";
            Assert.Equal("ROI_A_INIT", studio.Session.State.Rois[roiA].Ocv.ExpectedText);
            Assert.Equal("ROI_B_INIT", studio.Session.State.Rois[roiB].Ocv.ExpectedText);

            // M1(a): Execute on single ROI A with rejected/low-confidence PP-OCR result -> CanExecute must be false!
            studio.Session.Select([roiA]);
            Assert.True(suiteVm.CanEditSelectedRoiOcvSettings);
            nextPassVerdict = false;
            nextMeanConf = 0.850f;
            await suiteVm.ExecuteSelected.ExecuteAsync();
            Assert.False(suiteVm.ApplyPpOcrTextToOcvExpectedText.CanExecute(null));

            // M1(b): Passing PP-OCR on ROI A, then switch selection to ROI B -> CanExecute must be false (no Items[0] fallback!)
            nextPassVerdict = true;
            nextMeanConf = 0.998f;
            nextMinConf = 0.992f;
            await suiteVm.ExecuteSelected.ExecuteAsync();
            Assert.True(suiteVm.ApplyPpOcrTextToOcvExpectedText.CanExecute(null));

            studio.Session.Select([roiB]);
            Assert.False(suiteVm.ApplyPpOcrTextToOcvExpectedText.CanExecute(null));

            // M1(c): Select ROI A again; user cancels confirmation -> must NOT overwrite ROI A expected text
            studio.Session.Select([roiA]);
            Assert.True(suiteVm.ApplyPpOcrTextToOcvExpectedText.CanExecute(null));
            confirmResult = false;
            suiteVm.ApplyPpOcrTextToOcvExpectedText.Execute(null);
            Assert.Equal(1, confirmCallCount);
            Assert.Equal("ROI_A_INIT", studio.Session.State.Rois[roiA].Ocv.ExpectedText);

            // M1(d): User confirms -> applies "LOT2026" ONLY to ROI A
            confirmResult = true;
            suiteVm.ApplyPpOcrTextToOcvExpectedText.Execute(null);
            Assert.Equal(2, confirmCallCount);
            Assert.Equal("LOT2026", studio.Session.State.Rois[roiA].Ocv.ExpectedText);
            Assert.Equal("ROI_B_INIT", studio.Session.State.Rois[roiB].Ocv.ExpectedText);
        }
        finally
        {
            TryDeleteTempDir(tempDir);
        }
    }

    [Fact]
    public async Task M4_And_M5_LoadFontModel_PreservesInvertedPolarity_LoadsLegacyV1_AndMergesWithAppendTeach()
    {
        string tempDir = CreateTempDir();
        try
        {
            // M5: V2 persists DarkOnLight=false, and V1 JSON without darkOnLight loads cleanly with DarkOnLight=true
            byte[] invPatch = UnifiedTestImageSuiteGenerator.RenderCanonicalGlyphPatch(
                'A', fullWidth: 24, fullHeight: 34, out int cw, out int chH, continuousStroke: true, bg: 28, fg: 232);
            IndustrialOcrFontModel invModel = IndustrialOcrTrainer.Train(
                [new GoldenCharSample('A', invPatch, cw, chH, cw, DarkOnLight: false)]);
            Assert.False(invModel.DarkOnLight);

            string v2FontPath = Path.Combine(tempDir, "inverted_v2.ocvfont.json");
            using (FileStream fs = File.Create(v2FontPath))
            {
                IndustrialOcrFontModelSerializer.Save(fs, invModel);
            }

            IImageLoader loader = ImageLoaderHost.Create(Path.Combine(tempDir, "cache"));
            var ocvService = new TaughtFontOcvService(loader, () => null);
            using (FileStream loadFs = File.OpenRead(v2FontPath))
            {
                ocvService.LoadFontModel(loadFs);
            }

            IReadOnlyDictionary<string, string> exportedInv = ocvService.ExportAllFontModelsJson();
            Assert.True(exportedInv.ContainsKey("default"));
            using (var roundTripMs = new MemoryStream(Encoding.UTF8.GetBytes(exportedInv["default"])))
            {
                IndustrialOcrFontModel rtLoaded = IndustrialOcrFontModelSerializer.Load(roundTripMs);
                Assert.False(rtLoaded.DarkOnLight);
            }

            // Legacy V1 compatibility: rewrite formatVersion: 2 -> 1 and strip darkOnLight property
            string v2Json = File.ReadAllText(v2FontPath, Encoding.UTF8);
            string v1Json = System.Text.RegularExpressions.Regex.Replace(v2Json, "\"formatVersion\"\\s*:\\s*2", "\"formatVersion\": 1");
            v1Json = System.Text.RegularExpressions.Regex.Replace(v1Json, "\"darkOnLight\"\\s*:\\s*(true|false)\\s*,?", string.Empty);
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(v1Json)))
            {
                IndustrialOcrFontModel legacyLoaded = IndustrialOcrFontModelSerializer.Load(ms);
                Assert.True(legacyLoaded.DarkOnLight);
                Assert.Equal(1, legacyLoaded.Templates.Length);
            }

            // M4: Load a saved font ("LOT") into a fresh TaughtFontOcvService (where _accumulatedSamples is empty),
            // then run TeachFromRoiAsync("2026", appendToExistingFont: true) -> BOTH "LOT" and "2026" glyphs must remain!
            string sheetPath = Path.Combine(tempDir, "merge_sheet.png");
            using (var mat = new Mat(220, 420, MatType.CV_8UC1, Scalar.All(236)))
            {
                DrawSpacedLine(mat, "LOT", startX: 80, baselineY: 75, grayValue: 20, LineTypes.AntiAlias);
                DrawSpacedLine(mat, "2026", startX: 80, baselineY: 165, grayValue: 20, LineTypes.AntiAlias);
                Cv2.ImWrite(sheetPath, mat);
            }

            var imgRef = new ImageReference(sheetPath, "merge_sheet.png", 420, 220);
            var teachService = new TaughtFontOcvService(loader, () => null);
            OcvTeachingOutcome firstTeach = await teachService.TeachFromRoiAsync(
                imgRef,
                live: false,
                new InspectionSuiteRoiInput(Guid.NewGuid(), "ROI_LOT", new PointD(130, 64), 140, 44, 0, []),
                expectedText: "LOT",
                polarity: OcvPolaritySelection.DarkOnLight,
                foregroundContrastOffset: OcvVerificationOptions.DefaultForegroundContrastOffset,
                appendToExistingFont: false,
                token: CancellationToken.None);
            Assert.True(firstTeach.IsSuccess);

            string lotFontPath = Path.Combine(tempDir, "lot_only.ocvfont.json");
            using (FileStream saveLotFs = File.Create(lotFontPath))
            {
                teachService.SaveFontModel(saveLotFs);
            }

            var freshService = new TaughtFontOcvService(loader, () => null);
            using (FileStream loadLotFs = File.OpenRead(lotFontPath))
            {
                freshService.LoadFontModel(loadLotFs);
            }

            Assert.Equal(3, freshService.RegisteredCharacterCount);

            OcvTeachingOutcome appendTeach = await freshService.TeachFromRoiAsync(
                imgRef,
                live: false,
                new InspectionSuiteRoiInput(Guid.NewGuid(), "ROI_2026", new PointD(145, 154), 170, 44, 0, []),
                expectedText: "2026",
                polarity: OcvPolaritySelection.DarkOnLight,
                foregroundContrastOffset: OcvVerificationOptions.DefaultForegroundContrastOffset,
                appendToExistingFont: true,
                token: CancellationToken.None);
            Assert.True(appendTeach.IsSuccess);
            Assert.Equal(7, freshService.RegisteredCharacterCount); // 3 ('L','O','T') + 4 ('2','0','2','6')

            string mergedSummary = freshService.RegisteredCharactersSummary;
            Assert.Contains("L", mergedSummary, StringComparison.Ordinal);
            Assert.Contains("O", mergedSummary, StringComparison.Ordinal);
            Assert.Contains("T", mergedSummary, StringComparison.Ordinal);
            Assert.Contains("2", mergedSummary, StringComparison.Ordinal);
            Assert.Contains("0", mergedSummary, StringComparison.Ordinal);
            Assert.Contains("6", mergedSummary, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteTempDir(tempDir);
        }
    }

    [Fact]
    public async Task M6_MultiFontIdAndTeachingRecipeRoundTrip_PreservesRoiFontIdsAndSerializedFonts()
    {
        string tempDir = CreateTempDir();
        string recipePath = Path.Combine(AppContext.BaseDirectory, "latest-teaching-recipe.json");
        string? existingRecipeBackup = File.Exists(recipePath) ? File.ReadAllText(recipePath, Encoding.UTF8) : null;
        try
        {
            string imgPath = Path.Combine(tempDir, "multi_font_recipe.png");
            using (var mat = new Mat(220, 440, MatType.CV_8UC1, Scalar.All(236)))
            {
                DrawSpacedLine(mat, "LOT1", startX: 80, baselineY: 75, grayValue: 20, LineTypes.AntiAlias);
                DrawSpacedLine(mat, "EXP9", startX: 80, baselineY: 165, grayValue: 20, LineTypes.AntiAlias);
                Cv2.ImWrite(imgPath, mat);
            }

            IImageLoader loader = ImageLoaderHost.Create(Path.Combine(tempDir, "cache"));
            var ocvService1 = new TaughtFontOcvService(loader, () => null);
            var suiteVm1 = new InspectionSuiteViewModel(new InspectionSuiteService(loader, () => null), ocvService1);
            using var display1 = new NullImageDisplay();
            using MainWindowViewModel studio1 = CreateStudioWithSuite(display1, loader, suiteVm1);
            studio1.Inspections.Selected = InspectionToolKind.InspectionSuite;
            var imgRef = new ImageReference(imgPath, "multi_font_recipe.png", 440, 220);
            studio1.Session.InitializeImage(imgRef, new ViewportTransform(1.0, default));

            Guid roiLaser = studio1.Session.Add(new RoiGeometry(new PointD(145, 64), 170, 44, 0));
            studio1.Session.Select([roiLaser]);
            suiteVm1.SelectedRoiOcvFontId = "laser_font";
            suiteVm1.SelectedRoiOcvExpectedText = "LOT1";
            suiteVm1.SelectedRoiOcvFormatMask = "AAAD";
            suiteVm1.TeachSelectedRoiOcvFont.Execute(null);
            await WaitUntilIdleAsync(suiteVm1);

            Guid roiInkjet = studio1.Session.Add(new RoiGeometry(new PointD(145, 154), 170, 44, 0));
            studio1.Session.Select([roiInkjet]);
            suiteVm1.SelectedRoiOcvFontId = "inkjet_font";
            suiteVm1.SelectedRoiOcvExpectedText = "EXP9";
            suiteVm1.SelectedRoiOcvFormatMask = "AAAD";
            suiteVm1.TeachSelectedRoiOcvFont.Execute(null);
            await WaitUntilIdleAsync(suiteVm1);

            Assert.Equal(2, ocvService1.ExportAllFontModelsJson().Count);
            studio1.SaveTeachingRecipeToDefaultFile();
            Assert.True(File.Exists(recipePath));

            // Restore into a brand-new ViewModel + empty TaughtFontOcvService
            var ocvService2 = new TaughtFontOcvService(loader, () => null);
            var suiteVm2 = new InspectionSuiteViewModel(new InspectionSuiteService(loader, () => null), ocvService2);
            using var display2 = new NullImageDisplay();
            using MainWindowViewModel studio2 = CreateStudioWithSuite(display2, loader, suiteVm2);
            studio2.Session.InitializeImage(imgRef, new ViewportTransform(1.0, default));

            studio2.LoadTeachingRecipeFromDefaultFile();

            Assert.Equal(2, ocvService2.ExportAllFontModelsJson().Count);
            Assert.True(ocvService2.HasGoldenFontForId("laser_font"));
            Assert.True(ocvService2.HasGoldenFontForId("inkjet_font"));
            Assert.Equal("laser_font", studio2.Session.State.Rois[roiLaser].Ocv.FontId);
            Assert.Equal("LOT1", studio2.Session.State.Rois[roiLaser].Ocv.ExpectedText);
            Assert.Equal("AAAD", studio2.Session.State.Rois[roiLaser].Ocv.FormatMask);
            Assert.Equal("inkjet_font", studio2.Session.State.Rois[roiInkjet].Ocv.FontId);
            Assert.Equal("EXP9", studio2.Session.State.Rois[roiInkjet].Ocv.ExpectedText);

            // Verify all ROIs immediately using their restored per-ROI FontIds without re-teaching!
            suiteVm2.VerifyAllRoisOcv.Execute(null);
            await WaitUntilIdleAsync(suiteVm2);
            Assert.Equal(2, suiteVm2.OcvVerificationItems.Count);
            Assert.All(suiteVm2.OcvVerificationItems, item => Assert.Equal(OcvVerdictKind.Ok, item.Verdict));
        }
        finally
        {
            if (existingRecipeBackup is not null)
            {
                File.WriteAllText(recipePath, existingRecipeBackup, Encoding.UTF8);
            }
            else if (File.Exists(recipePath))
            {
                File.Delete(recipePath);
            }

            TryDeleteTempDir(tempDir);
        }
    }

    [Fact]
    public void M7_AutoPolarityVerification_PrefersBetterVerdictAndFewerNgCharsOverHigherMeanScore()
    {
        var reviewAllValid = new OcvVerificationResult(
            Verdict: OcvVerdictKind.Review,
            DecodedText: "LOT1",
            ExpectedText: "LOT1",
            Characters:
            [
                new OcvCharacterVerification(0, 'L', 'L', 0.80f, 0.12f, 0f, 0f, 30f, 0, 10, 0, 20, false, OcvVerdictKind.Review, "대비 경계"),
                new OcvCharacterVerification(1, 'O', 'O', 0.82f, 0.12f, 0f, 0f, 30f, 12, 22, 0, 20, false, OcvVerdictKind.Ok, "정상"),
            ],
            MeanMatchScore: 0.81f,
            MinMatchScore: 0.80f,
            MinContrastGv: 30f,
            MaxMissingStrokeRatio: 0f,
            MaxBleedRatio: 0f,
            CauseSummary: "대비 경계",
            RecommendedAction: "점검");

        var ngWithHigherMean = new OcvVerificationResult(
            Verdict: OcvVerdictKind.Ng,
            DecodedText: "L?T1",
            ExpectedText: "LOT1",
            Characters:
            [
                new OcvCharacterVerification(0, 'L', 'L', 0.99f, 0.25f, 0f, 0f, 80f, 0, 10, 0, 20, false, OcvVerdictKind.Ok, "정상"),
                new OcvCharacterVerification(1, 'O', '?', 0.85f, 0.00f, 0.5f, 0f, 80f, 12, 22, 0, 20, false, OcvVerdictKind.Ng, "불일치"),
            ],
            MeanMatchScore: 0.92f, // +0.11 higher mean score, yet has an NG character!
            MinMatchScore: 0.85f,
            MinContrastGv: 80f,
            MaxMissingStrokeRatio: 0.5f,
            MaxBleedRatio: 0f,
            CauseSummary: "불일치",
            RecommendedAction: "분리");

        OcvVerificationResult chosen1 = TaughtFontOcvService.SelectPreferredPolarityVerification(reviewAllValid, ngWithHigherMean);
        Assert.Same(reviewAllValid, chosen1);

        OcvVerificationResult chosen2 = TaughtFontOcvService.SelectPreferredPolarityVerification(ngWithHigherMean, reviewAllValid);
        Assert.Same(reviewAllValid, chosen2);
    }

    private static bool MatchesEntireString(OcrFieldFormatSpec spec, string candidate)
    {
        if (candidate.Length != spec.SlotCount)
        {
            return false;
        }

        for (int i = 0; i < candidate.Length; i++)
        {
            if (!spec.AllowsCharacterAt(i, candidate[i]))
            {
                return false;
            }
        }

        return true;
    }

    private sealed class StubPpOcrSuiteService(
        Func<IReadOnlyList<InspectionSuiteRoiInput>, InspectionSuiteParameters, InspectionSuiteExecutionOutput> handler)
        : IInspectionSuiteService
    {
        public bool IsPpOcrWired => true;

        public string PpOcrStatusSummary => "PP-OCR 스텁 준비됨";

        public Task<InspectionSuiteExecutionOutput> ExecuteRoiInspectionAsync(
            ImageReference image,
            bool live,
            InspectionSuiteAlgorithmKind algorithm,
            IReadOnlyList<InspectionSuiteRoiInput> targetRois,
            IReadOnlyList<InspectionSuiteRoiInput> referenceRois,
            InspectionSuiteParameters parameters,
            CancellationToken token)
            => Task.FromResult(handler(targetRois, parameters));

        public Task<InspectionSuiteExecutionOutput> RunMaster8kSuiteAsync(CancellationToken token)
            => throw new NotSupportedException();
    }

    private static void CreateSyntheticOcvSheet(string outputPath, out int width, out int height)
    {
        width = 480;
        height = 380;
        using var mat = new Mat(height, width, MatType.CV_8UC1, Scalar.All(236));

        // Strip 1 (y=70): Reference "LOT2026" (contrast = 216 GV)
        DrawSpacedLine(mat, "LOT2026", startX: 112, baselineY: 81, grayValue: 20, LineTypes.AntiAlias);
        // Strip 2 (y=150): Identical OK target "LOT2026" (contrast = 216 GV)
        DrawSpacedLine(mat, "LOT2026", startX: 112, baselineY: 161, grayValue: 20, LineTypes.AntiAlias);
        // Strip 3 (y=230): Borderline Review target "LOT2026" (bg=236, fg=208 -> contrast = 28 GV in [22, 34))
        DrawSpacedLine(mat, "LOT2026", startX: 112, baselineY: 241, grayValue: 208, LineTypes.Link8);
        // Strip 4 (y=310): NG target "LOT2826" (character substitution '0' -> '8')
        DrawSpacedLine(mat, "LOT2826", startX: 112, baselineY: 321, grayValue: 20, LineTypes.AntiAlias);

        Cv2.ImWrite(outputPath, mat);
    }

    private static void DrawSpacedLine(Mat mat, string text, int startX, int baselineY, byte grayValue, LineTypes lineType)
    {
        const int stepX = 32;
        for (int i = 0; i < text.Length; i++)
        {
            Cv2.PutText(
                mat,
                text[i].ToString(),
                new Point(startX + (i * stepX), baselineY),
                HersheyFonts.HersheySimplex,
                0.95,
                Scalar.All(grayValue),
                thickness: 2,
                lineType);
        }
    }

    private static MainWindowViewModel CreateStudioWithSuite(
        NullImageDisplay display,
        IImageLoader loader,
        InspectionSuiteViewModel suiteVm)
    {
        var inspections = new InspectionToolsViewModel(
            new ShapeMatchingViewModel(
                new ShapeMatchingService(loader, () => null, pipeline: ShapeSearchPipeline.Paper),
                new TemplateMatchingService(loader, () => null),
                new CpuTemplateMatchingService(loader, () => null),
                new PatMaxMatchingService(loader, () => null)),
            new PrimitiveShapeDetectionViewModel(new PrimitiveShapeDetectionService(loader, () => null)),
            new EdgeFewShotViewModel(new EdgeFewShotService(loader, () => null)),
            suiteVm);

        return new MainWindowViewModel(
            display,
            new ViewportLayout(ViewportWidth, ViewportHeight),
            loader,
            new LivePreview(display, _ => throw new NotSupportedException()),
            new NullFilterPreview(),
            inspections);
    }

    private static async Task WaitUntilIdleAsync(InspectionSuiteViewModel vm)
    {
        for (int i = 0; i < 400 && vm.IsBusy; i++)
        {
            await Task.Delay(15);
        }

        Assert.True(vm.IsIdle, "InspectionSuiteViewModel did not return to idle within timeout.");
    }

    private static string CreateTempDir()
    {
        string path = Path.Combine(Path.GetTempPath(), $"TeachingToolWpf_OcvWf_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteTempDir(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Ignore transient test cleanup lock.
        }
    }

    private sealed class NullImageDisplay : IImageDisplay, IFramePresenter, IDisposable
    {
        private ITileSource? _source;

        public long SetSource(ITileSource source)
        {
            _source = source;
            return 1;
        }

        public Task RestartAsync() => Task.CompletedTask;

        public void PublishFrame(VideoFrame frame)
        {
        }

        public void ClearFrames()
        {
        }

        public void Dispose() => _source?.Dispose();
    }

    private sealed class NullFilterPreview : IRoiFilterPreview
    {
        public Task<FilterPreviewResult> ProcessAsync(FilterRequest request, CancellationToken token)
            => throw new NotSupportedException();
    }
}
