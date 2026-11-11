using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;
using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Models;
using TeachingToolWpf.Services;
using TeachingToolWpf.ViewModels;
using VisionAlgorithm.OpenSrc.IndustrialOcrOcv;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionCanvas.Imaging.Filters;
using VisionCanvas.Presentation;
using VisionGeometry;
using VisionImaging;
using VisionImaging.Abstractions;
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
            string wrongVerJson = File.ReadAllText(fontPath, Encoding.UTF8)
                .Replace("\"formatVersion\": 1", "\"formatVersion\": 999", StringComparison.Ordinal);
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
