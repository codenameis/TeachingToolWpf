using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using VisionAlgorithm.OpenSrc.AutoTeaching;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.IndustrialOcrOcv;

namespace VisionAlgorithm.OpenSrc.Synthetic8kTestGen;

/// <summary>
/// End-to-end verification runner for TaughtFontOcvVerifier (across IndustrialOcrOcv and LightweightOcr suites)
/// and AutoTeaching across all 19 test scenarios (including 7680x4320 8K sheets).
/// </summary>
public static class UnifiedNewSubsystems8kRunner
{
    private const int DefaultLaserStripWidth = 420;
    private const int DefaultLaserStripHeight = 40;
    private const int DefaultSerialStripWidth = 480;
    private const int DefaultSerialStripHeight = 36;
    private const int MinBandHeightPx = 10;
    private const float MultiLineBandThresholdOffsetGv = 28.0f;

    public sealed record VerificationCaseSummary(
        string SuiteName,
        string CaseName,
        bool Passed,
        double ElapsedMilliseconds,
        string Details);

    public sealed record UnifiedVerificationReport(
        bool AllPassed,
        IReadOnlyList<VerificationCaseSummary> Cases,
        double TotalElapsedMilliseconds);

    public static UnifiedVerificationReport RunAllVerifications(
        string? exportDirectory = null,
        int sheetWidth = UnifiedTestImageSuiteGenerator.Full8kWidth,
        int sheetHeight = UnifiedTestImageSuiteGenerator.Full8kHeight)
    {
        var totalSw = Stopwatch.StartNew();
        var results = new List<VerificationCaseSummary>(24);

        bool ownsTempDirectory = string.IsNullOrWhiteSpace(exportDirectory);
        string tempOrExportDir = ownsTempDirectory
            ? Path.Combine(Path.GetTempPath(), "TeachingToolWpf_UnifiedVisionSuites_" + Guid.NewGuid().ToString("N")[..8])
            : exportDirectory!;

        try
        {
            UnifiedTestImageSuiteGenerator.ExportAllTestSuitesToDisk(
                tempOrExportDir,
                includeFull8kBmp: true,
                sheetWidth: sheetWidth,
                sheetHeight: sheetHeight);

            using var workspace = new VisionWorkspace(checked(sheetWidth * sheetHeight));

            RunIndustrialOcrOcvSuite(tempOrExportDir, workspace, results);
            RunLightweightOcrSuite(tempOrExportDir, workspace, results);
            RunAutoTeachingSuite(tempOrExportDir, workspace, results);

            totalSw.Stop();
            bool allPassed = true;
            for (int i = 0; i < results.Count; i++)
            {
                if (!results[i].Passed)
                {
                    allPassed = false;
                    break;
                }
            }

            return new UnifiedVerificationReport(allPassed, results, totalSw.Elapsed.TotalMilliseconds);
        }
        finally
        {
            if (ownsTempDirectory && Directory.Exists(tempOrExportDir))
            {
                try
                {
                    Directory.Delete(tempOrExportDir, recursive: true);
                }
                catch
                {
                    // Ignore transient OS file-lock delay on temp cleanup.
                }
            }
        }
    }

    private static void RunIndustrialOcrOcvSuite(
        string rootDir,
        VisionWorkspace workspace,
        List<VerificationCaseSummary> results)
    {
        string suiteDir = Path.Combine(rootDir, "IndustrialOcrOcv");
        IndustrialOcrFontModel fontModel = UnifiedTestImageSuiteGenerator.TrainDefaultIndustrialFontModel();
        var verifier = new TaughtFontOcvVerifier(fontModel);

        var defaultRoiPose = new FixturePose2D(
            UnifiedTestImageSuiteGenerator.RoiTestWidth * 0.5f,
            UnifiedTestImageSuiteGenerator.RoiTestHeight * 0.5f,
            0f);

        var laserOptions = new OcvVerificationOptions(
            PassMatchScore: 0.65f,
            ReviewMatchScore: 0.45f,
            MinConfusionMargin: 0.005f,
            PrintMethod: PrintMethodType.ContinuousLaser,
            MinCharWidth: 5,
            MaxCharWidth: 28,
            ExpectedPitch: 24,
            DotClosingRadiusX: 1,
            DotClosingRadiusY: 1,
            ForegroundContrastOffset: 16.0f,
            DarkOnLight: true,
            RectifiedWidth: DefaultLaserStripWidth,
            RectifiedHeight: DefaultLaserStripHeight);

        var dotMatrixOptions = laserOptions with
        {
            PassMatchScore = 0.60f,
            ReviewMatchScore = 0.40f,
            PrintMethod = PrintMethodType.InkjetDotMatrix,
            DotClosingRadiusX = 2,
            DotClosingRadiusY = 2,
            ForegroundContrastOffset = 14.0f,
        };

        var touchingOptions = laserOptions with
        {
            MaxCharWidth = 20,
            ExpectedPitch = 19,
        };

        var strictDefectOptions = laserOptions with
        {
            MaxMissingStrokeRatio = 0.22f,
            ReviewMissingStrokeRatio = 0.14f,
        };

        // Case 01: Clean Laser Lot "LOT-2026-A09"
        VerifyOcrOcvFile(
            "IndustrialOcrOcv",
            suiteDir,
            "Case01_CleanLaserLot_LOT2026A09.bmp",
            verifier,
            workspace,
            defaultRoiPose,
            new OcrFieldFormatSpec(@"AAA\-DDDD\-ADD", "LOT-2026-A09"),
            laserOptions,
            expectPass: true,
            expectedString: "LOT-2026-A09",
            results);

        // Case 02: Dot-Matrix Inkjet "EXP.2027.11.24"
        VerifyOcrOcvFile(
            "IndustrialOcrOcv",
            suiteDir,
            "Case02_DotMatrixInkjet_EXP20271124.bmp",
            verifier,
            workspace,
            defaultRoiPose,
            new OcrFieldFormatSpec(@"AAA\.DDDD\.DD\.DD", "EXP.2027.11.24"),
            dotMatrixOptions,
            expectPass: true,
            expectedString: "EXP.2027.11.24",
            results);

        // Case 03: Touching Kerning "SN884102B"
        VerifyOcrOcvFile(
            "IndustrialOcrOcv",
            suiteDir,
            "Case03_TouchingKerning_SN884102B.bmp",
            verifier,
            workspace,
            defaultRoiPose,
            new OcrFieldFormatSpec("AADDDDDDA", "SN884102B"),
            touchingOptions,
            expectPass: true,
            expectedString: "SN884102B",
            results);

        // Case 04: Confusable Pairs "B80ODQ1I5S2Z" with Fielding Mask "ADDAAADADADA"
        VerifyOcrOcvFile(
            "IndustrialOcrOcv",
            suiteDir,
            "Case04_ConfusionAmbiguity_B80ODQ1I5S2Z.bmp",
            verifier,
            workspace,
            defaultRoiPose,
            new OcrFieldFormatSpec("ADDAAADADADA", "B80ODQ1I5S2Z"),
            laserOptions,
            expectPass: true,
            expectedString: "B80ODQ1I5S2Z",
            results);

        // Case 05: OCV Stroke Defect on "LOT-2026-A09" (Index 6 broken stroke -> IsOk must be false)
        VerifyOcrOcvFile(
            "IndustrialOcrOcv",
            suiteDir,
            "Case05_OcvStrokeDefect_LOT2026A09_Broken8.bmp",
            verifier,
            workspace,
            defaultRoiPose,
            new OcrFieldFormatSpec(@"AAA\-DDDD\-ADD", "LOT-2026-A09"),
            strictDefectOptions,
            expectPass: false,
            expectedString: null,
            results);

        // Case 06: Rotated +7.5 deg & Uneven Illumination "LOT-2026-A09"
        float rotRad = 7.5f * (MathF.PI / 180.0f);
        var rotatedPose = new FixturePose2D(
            UnifiedTestImageSuiteGenerator.RoiTestWidth * 0.5f,
            UnifiedTestImageSuiteGenerator.RoiTestHeight * 0.5f,
            rotRad);
        VerifyOcrOcvFile(
            "IndustrialOcrOcv",
            suiteDir,
            "Case06_RotatedUnevenIllum_LOT2026A09_Rot7Deg.bmp",
            verifier,
            workspace,
            rotatedPose,
            new OcrFieldFormatSpec(@"AAA\-DDDD\-ADD", "LOT-2026-A09"),
            laserOptions,
            expectPass: true,
            expectedString: "LOT-2026-A09",
            results);

        // Case 07: Full 8K (7680x4320) 4x6=24 Blister Cells (23 PASS, Cell #11 NG_OCV)
        var sw8k = Stopwatch.StartNew();
        string path8k = Path.Combine(suiteDir, "Case07_Full8K_MultiBlisterSheet_7680x4320.bmp");
        byte[] pixels8k = Gray8BmpFileExporter.LoadGray8Bmp(path8k, out int w8k, out int h8k);
        var view8k = new ReadOnlyImageView2D<byte>(pixels8k, w8k, h8k, w8k);

        const int cols = 6;
        const int rows = 4;
        int cellW = w8k / cols;
        int cellH = h8k / rows;
        int passCount = 0;
        int ocvFailCount = 0;
        bool cell11DetectedAsOcvFail = false;
        var blisterSpec = new OcrFieldFormatSpec(@"AAA\-DDDD\-ADD", "LOT-2026-A09");

        int idx = 0;
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                float cx = c * cellW + cellW * 0.5f;
                float cy = r * cellH + cellH * 0.5f;
                var cellPose = new FixturePose2D(cx, cy, 0f);
                OcvVerificationResult res = verifier.VerifyLine(
                    view8k, in cellPose, blisterSpec, strictDefectOptions, workspace);
                if (!res.IsNg && res.DecodedText == "LOT-2026-A09")
                {
                    passCount++;
                }
                else
                {
                    ocvFailCount++;
                    if (idx == 11)
                    {
                        cell11DetectedAsOcvFail = true;
                    }
                }
                idx++;
            }
        }
        sw8k.Stop();
        bool case7Passed = (passCount == 23 && ocvFailCount == 1 && cell11DetectedAsOcvFail);
        results.Add(new VerificationCaseSummary(
            "IndustrialOcrOcv",
            "Case07_Full8K_MultiBlisterSheet_7680x4320.bmp",
            case7Passed,
            sw8k.Elapsed.TotalMilliseconds,
            $"Cells=24, Pass={passCount}, OcvNg={ocvFailCount}, Cell11Ng={cell11DetectedAsOcvFail}"));
    }

    private static void VerifyOcrOcvFile(
        string suiteName,
        string suiteDir,
        string fileName,
        TaughtFontOcvVerifier verifier,
        VisionWorkspace workspace,
        in FixturePose2D roiPose,
        OcrFieldFormatSpec fieldSpec,
        OcvVerificationOptions options,
        bool expectPass,
        string? expectedString,
        List<VerificationCaseSummary> results)
    {
        var sw = Stopwatch.StartNew();
        string fullPath = Path.Combine(suiteDir, fileName);
        byte[] pixels = Gray8BmpFileExporter.LoadGray8Bmp(fullPath, out int w, out int h);
        var view = new ReadOnlyImageView2D<byte>(pixels, w, h, w);

        OcvVerificationResult res = verifier.VerifyLine(
            view, in roiPose, fieldSpec, options, workspace);
        sw.Stop();

        bool actualPass = !res.IsNg;
        bool stringMatches = expectedString is null || string.Equals(res.DecodedText, expectedString, StringComparison.Ordinal);
        bool passed = stringMatches && (actualPass == expectPass);

        results.Add(new VerificationCaseSummary(
            suiteName,
            fileName,
            passed,
            sw.Elapsed.TotalMilliseconds,
            $"Decoded='{res.DecodedText}' (Expected='{expectedString ?? "DEFECT"}'), Verdict={res.Verdict}, MinScore={res.MinMatchScore:F3}, Missing={res.MaxMissingStrokeRatio:F3}, Cause='{res.CauseSummary}'"));
    }

    private static void RunLightweightOcrSuite(
        string rootDir,
        VisionWorkspace workspace,
        List<VerificationCaseSummary> results)
    {
        string suiteDir = Path.Combine(rootDir, "LightweightOcr");
        IndustrialOcrFontModel fontModel = UnifiedTestImageSuiteGenerator.TrainDefaultIndustrialFontModel();
        var verifier = new TaughtFontOcvVerifier(fontModel);

        var defaultRoiPose = new FixturePose2D(
            UnifiedTestImageSuiteGenerator.RoiTestWidth * 0.5f,
            UnifiedTestImageSuiteGenerator.RoiTestHeight * 0.5f,
            0f);

        var serialOptions = new OcvVerificationOptions(
            PassMatchScore: 0.62f,
            ReviewMatchScore: 0.42f,
            MinConfusionMargin: 0.005f,
            PrintMethod: PrintMethodType.ContinuousLaser,
            MinCharWidth: 5,
            MaxCharWidth: 28,
            ExpectedPitch: 24,
            DotClosingRadiusX: 1,
            DotClosingRadiusY: 1,
            ForegroundContrastOffset: 16.0f,
            DarkOnLight: true,
            RectifiedWidth: DefaultSerialStripWidth,
            RectifiedHeight: DefaultSerialStripHeight);

        // Case 01: Single-line "WAFER-094817-A2"
        VerifyOcrOcvFile(
            "LightweightOcr",
            suiteDir,
            "Case01_SingleLineSerial_WAFER094817A2.bmp",
            verifier,
            workspace,
            defaultRoiPose,
            new OcrFieldFormatSpec(@"AAAAA\-DDDDDD\-AD", "WAFER-094817-A2"),
            serialOptions,
            expectPass: true,
            expectedString: "WAFER-094817-A2",
            results);

        // Case 02: Multi-line 3 Lines ("PN-99402-A", "LOT-2026-11", "QTY-0500")
        var sw2 = Stopwatch.StartNew();
        string path2 = Path.Combine(suiteDir, "Case02_MultiLineLabel_3Lines.bmp");
        byte[] px2 = Gray8BmpFileExporter.LoadGray8Bmp(path2, out int w2, out int h2);
        var view2 = new ReadOnlyImageView2D<byte>(px2, w2, h2, w2);
        List<float> bandCentersY = DetectHorizontalTextBandCentersY(view2);

        string[] expectedMultiLines = ["PN-99402-A", "LOT-2026-11", "QTY-0500"];
        var decodedLines = new List<string>(bandCentersY.Count);
        for (int i = 0; i < bandCentersY.Count; i++)
        {
            var linePose = new FixturePose2D(w2 * 0.5f, bandCentersY[i], 0f);
            string? expectedLine = i < expectedMultiLines.Length ? expectedMultiLines[i] : null;
            OcvVerificationResult lineRes = verifier.VerifyLine(
                view2,
                in linePose,
                new OcrFieldFormatSpec(ExpectedText: expectedLine),
                serialOptions,
                workspace);
            if (!lineRes.IsNg)
            {
                decodedLines.Add(lineRes.DecodedText);
            }
        }
        sw2.Stop();

        bool case2Pass = decodedLines.Count == 3 &&
                         decodedLines[0] == "PN-99402-A" &&
                         decodedLines[1] == "LOT-2026-11" &&
                         decodedLines[2] == "QTY-0500";
        results.Add(new VerificationCaseSummary(
            "LightweightOcr",
            "Case02_MultiLineLabel_3Lines.bmp",
            case2Pass,
            sw2.Elapsed.TotalMilliseconds,
            $"Lines={decodedLines.Count}, Decoded='{string.Join(" | ", decodedLines)}'"));

        // Case 03: Skewed +4.0 deg "BATCH-7731-X9"
        var skewPose = new FixturePose2D(
            UnifiedTestImageSuiteGenerator.RoiTestWidth * 0.5f,
            UnifiedTestImageSuiteGenerator.RoiTestHeight * 0.5f,
            4.0f * (MathF.PI / 180.0f));
        var variablePitchOptions = serialOptions with { ExpectedPitch = 22 };
        VerifyOcrOcvFile(
            "LightweightOcr",
            suiteDir,
            "Case03_VariablePitchSkew_BATCH7731X9.bmp",
            verifier,
            workspace,
            skewPose,
            new OcrFieldFormatSpec(@"AAAAA\-DDDD\-AD", "BATCH-7731-X9"),
            variablePitchOptions,
            expectPass: true,
            expectedString: "BATCH-7731-X9",
            results);

        // Case 04: Low Contrast "PCM-4409-10Z"
        var lowContrastOptions = serialOptions with { ForegroundContrastOffset = 10.0f };
        VerifyOcrOcvFile(
            "LightweightOcr",
            suiteDir,
            "Case04_LowContrastNoisy_PCM440910Z.bmp",
            verifier,
            workspace,
            defaultRoiPose,
            new OcrFieldFormatSpec(@"AAA\-DDDD\-DDA", "PCM-4409-10Z"),
            lowContrastOptions,
            expectPass: true,
            expectedString: "PCM-4409-10Z",
            results);

        // Case 05: Inverted Light-on-Dark "INV-2026-8801"
        IndustrialOcrFontModel invertedFont = TrainInvertedIndustrialFontModel();
        var invertedVerifier = new TaughtFontOcvVerifier(invertedFont);
        var invertedOptions = serialOptions with { DarkOnLight = false };
        VerifyOcrOcvFile(
            "LightweightOcr",
            suiteDir,
            "Case05_InvertedDarkOnBright_INV20268801.bmp",
            invertedVerifier,
            workspace,
            defaultRoiPose,
            new OcrFieldFormatSpec(@"AAA\-DDDD\-DDDD", "INV-2026-8801"),
            invertedOptions,
            expectPass: true,
            expectedString: "INV-2026-8801",
            results);

        // Case 06: Full 8K (7680x4320) 4x8=32 High-Speed Labels
        var sw6 = Stopwatch.StartNew();
        string path6 = Path.Combine(suiteDir, "Case06_Full8K_MultiLabelSheet_7680x4320.bmp");
        byte[] px6 = Gray8BmpFileExporter.LoadGray8Bmp(path6, out int w6, out int h6);
        var view6 = new ReadOnlyImageView2D<byte>(px6, w6, h6, w6);

        const int cols = 8;
        const int rows = 4;
        int cellW = w6 / cols;
        int cellH = h6 / rows;
        int matchedLabels = 0;
        var waferSpec = new OcrFieldFormatSpec(@"AAAAA\-DDDDDD\-AD", "WAFER-094817-A2");

        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                var pose = new FixturePose2D(c * cellW + cellW * 0.5f, r * cellH + cellH * 0.5f, 0f);
                OcvVerificationResult cellRes = verifier.VerifyLine(view6, in pose, waferSpec, serialOptions, workspace);
                if (!cellRes.IsNg && cellRes.DecodedText == "WAFER-094817-A2")
                {
                    matchedLabels++;
                }
            }
        }
        sw6.Stop();
        results.Add(new VerificationCaseSummary(
            "LightweightOcr",
            "Case06_Full8K_MultiLabelSheet_7680x4320.bmp",
            matchedLabels == 32,
            sw6.Elapsed.TotalMilliseconds,
            $"MatchedLabels={matchedLabels}/32"));
    }

    private static IndustrialOcrFontModel TrainInvertedIndustrialFontModel()
    {
        var samples = new List<GoldenCharSample>(UnifiedTestImageSuiteGenerator.DefaultCatalogAlphabet.Length);
        foreach (char ch in UnifiedTestImageSuiteGenerator.DefaultCatalogAlphabet)
        {
            byte[] patch = UnifiedTestImageSuiteGenerator.RenderCanonicalGlyphPatch(
                ch,
                fullWidth: 24,
                fullHeight: 34,
                out int cw,
                out int chH,
                continuousStroke: true,
                bg: 32,
                fg: 228);
            samples.Add(new GoldenCharSample(ch, patch, cw, chH, cw, DarkOnLight: false));
        }

        return IndustrialOcrTrainer.Train(CollectionsMarshal.AsSpan(samples));
    }

    private static List<float> DetectHorizontalTextBandCentersY(ReadOnlyImageView2D<byte> image)
    {
        long sum = 0;
        for (int y = 0; y < image.Height; y++)
        {
            ReadOnlySpan<byte> row = image.GetRowSpan(y);
            for (int x = 0; x < image.Width; x++)
            {
                sum += row[x];
            }
        }

        float bgMean = (float)sum / Math.Max(1, image.Width * image.Height);
        float darkThreshold = bgMean - MultiLineBandThresholdOffsetGv;

        var centers = new List<float>(4);
        int bandStart = -1;
        for (int y = 0; y < image.Height; y++)
        {
            ReadOnlySpan<byte> row = image.GetRowSpan(y);
            bool hasInk = false;
            for (int x = 0; x < image.Width; x++)
            {
                if (row[x] < darkThreshold)
                {
                    hasInk = true;
                    break;
                }
            }

            if (hasInk)
            {
                if (bandStart < 0)
                {
                    bandStart = y;
                }
            }
            else if (bandStart >= 0)
            {
                int bandH = y - bandStart;
                if (bandH >= MinBandHeightPx)
                {
                    centers.Add(bandStart + (bandH * 0.5f));
                }

                bandStart = -1;
            }
        }

        if (bandStart >= 0 && (image.Height - bandStart) >= MinBandHeightPx)
        {
            centers.Add(bandStart + ((image.Height - bandStart) * 0.5f));
        }

        return centers;
    }

    private static void RunAutoTeachingSuite(
        string rootDir,
        VisionWorkspace workspace,
        List<VerificationCaseSummary> results)
    {
        string suiteDir = Path.Combine(rootDir, "AutoTeaching");
        using var pool = new PartModelPool();

        var childRoisA = new[]
        {
            new ChildRoiTemplateSpec("HoleCheck_A", ChildToolKind.SubpixelCaliperWidth, new FixturePose2D(0f, 0f, 0f), 120f, 120f),
            new ChildRoiTemplateSpec("CornerCheck_A", ChildToolKind.FiducialCrosshairCheck, new FixturePose2D(-58f, -62f, 0f), 48f, 48f)
        };
        var childRoisB = new[]
        {
            new ChildRoiTemplateSpec("LeadCaliper_B", ChildToolKind.RleBlobPinInspection, new FixturePose2D(0f, 56f, 0f), 110f, 32f),
            new ChildRoiTemplateSpec("Pin1Dot_B", ChildToolKind.GoldenDifferenceAnomaly, new FixturePose2D(-38f, -20f, 0f), 24f, 24f)
        };
        var childRoisC = new[]
        {
            new ChildRoiTemplateSpec("CrossFiducial_C", ChildToolKind.FiducialCrosshairCheck, new FixturePose2D(0f, 0f, 0f), 110f, 110f),
            new ChildRoiTemplateSpec("OrientNotch_C", ChildToolKind.SubpixelCaliperWidth, new FixturePose2D(36f, -36f, 0f), 24f, 24f)
        };

        var goldenPose = new FixturePose2D(512f, 384f, 0f);
        const float partRoiW = 200f;
        const float partRoiH = 200f;

        // Cases 01..03: Teach Golden Parts A, B, C
        TeachGoldenPartFile(suiteDir, "Case01_GoldenPartA_BracketWithHoleAndSlot.bmp", "PartA_Bracket", goldenPose, partRoiW, partRoiH, childRoisA, pool, workspace, results);
        TeachGoldenPartFile(suiteDir, "Case02_GoldenPartB_ConnectorWithPins.bmp", "PartB_Connector", goldenPose, partRoiW, partRoiH, childRoisB, pool, workspace, results);
        TeachGoldenPartFile(suiteDir, "Case03_GoldenPartC_ShieldCanWithFiducial.bmp", "PartC_ShieldCan", goldenPose, partRoiW, partRoiH, childRoisC, pool, workspace, results);

        // Case 04: Mixed Multi-Part Scene with Non-Integer Sub-Pixel Ground-Truth Poses
        var sw4 = Stopwatch.StartNew();
        byte[] px4 = Gray8BmpFileExporter.LoadGray8Bmp(Path.Combine(suiteDir, "Case04_MixedMultiPartScene_RotatedOccluded.bmp"), out int w4, out int h4);
        var view4 = new ReadOnlyImageView2D<byte>(px4, w4, h4, w4);
        AutoTeachingResult autoRes4 = AutoTeachingOrchestrator.RecognizeAndAutoTeach(
            view4, pool, workspace, minGeometricScore: 0.50f, minAppearanceCosine: 0.45f, maxMahalanobisDistance: 25.0f);
        sw4.Stop();

        var gtPoseA = new FixturePose2D(
            UnifiedTestImageSuiteGenerator.Case04PartACenterX,
            UnifiedTestImageSuiteGenerator.Case04PartACenterY,
            UnifiedTestImageSuiteGenerator.Case04PartAAngleDeg * (MathF.PI / 180.0f));
        var gtPoseB = new FixturePose2D(
            UnifiedTestImageSuiteGenerator.Case04PartBCenterX,
            UnifiedTestImageSuiteGenerator.Case04PartBCenterY,
            UnifiedTestImageSuiteGenerator.Case04PartBAngleDeg * (MathF.PI / 180.0f));
        var gtPoseC = new FixturePose2D(
            UnifiedTestImageSuiteGenerator.Case04PartCCenterX,
            UnifiedTestImageSuiteGenerator.Case04PartCCenterY,
            UnifiedTestImageSuiteGenerator.Case04PartCAngleDeg * (MathF.PI / 180.0f));

        bool hasA = ExistsPartNear(autoRes4.Instances, "PartA_Bracket", gtPoseA.CenterX, gtPoseA.CenterY, 5.0f, out DetectedPartInstance instA);
        bool hasB = ExistsPartNear(autoRes4.Instances, "PartB_Connector", gtPoseB.CenterX, gtPoseB.CenterY, 5.0f, out DetectedPartInstance instB);
        bool hasC = ExistsPartNear(autoRes4.Instances, "PartC_ShieldCan", gtPoseC.CenterX, gtPoseC.CenterY, 5.0f, out DetectedPartInstance instC);

        float maxChildErr = 0f;
        if (hasA && instA.ChildRois.Length == childRoisA.Length)
        {
            for (int c = 0; c < childRoisA.Length; c++)
            {
                var gtChildWorld = gtPoseA.Compose(childRoisA[c].LocalOffsetPose);
                float dx = instA.ChildRois[c].WorldPose.OriginX - gtChildWorld.OriginX;
                float dy = instA.ChildRois[c].WorldPose.OriginY - gtChildWorld.OriginY;
                float err = MathF.Sqrt((dx * dx) + (dy * dy));
                if (err > maxChildErr) maxChildErr = err;
            }
        }
        if (hasB && instB.ChildRois.Length == childRoisB.Length)
        {
            for (int c = 0; c < childRoisB.Length; c++)
            {
                var gtChildWorld = gtPoseB.Compose(childRoisB[c].LocalOffsetPose);
                float dx = instB.ChildRois[c].WorldPose.OriginX - gtChildWorld.OriginX;
                float dy = instB.ChildRois[c].WorldPose.OriginY - gtChildWorld.OriginY;
                float err = MathF.Sqrt((dx * dx) + (dy * dy));
                if (err > maxChildErr) maxChildErr = err;
            }
        }
        if (hasC && instC.ChildRois.Length == childRoisC.Length)
        {
            for (int c = 0; c < childRoisC.Length; c++)
            {
                var gtChildWorld = gtPoseC.Compose(childRoisC[c].LocalOffsetPose);
                float dx = instC.ChildRois[c].WorldPose.OriginX - gtChildWorld.OriginX;
                float dy = instC.ChildRois[c].WorldPose.OriginY - gtChildWorld.OriginY;
                float err = MathF.Sqrt((dx * dx) + (dy * dy));
                if (err > maxChildErr) maxChildErr = err;
            }
        }

        bool case4Passed = hasA && hasB && hasC && maxChildErr > 0.0001f && maxChildErr <= 0.05f;
        results.Add(new VerificationCaseSummary(
            "AutoTeaching",
            "Case04_MixedMultiPartScene_RotatedOccluded.bmp",
            case4Passed,
            sw4.Elapsed.TotalMilliseconds,
            $"Summary='{autoRes4.SummaryText}' (PartA={hasA}, PartB={hasB}, PartC={hasC}), ChildRoiError={maxChildErr:F5}px"));

        // Case 05: Duplicate Teach Attempt (Part A rotated +15 deg at 340, 420) & Unknown Foreign Distractors
        var sw5 = Stopwatch.StartNew();
        byte[] px5 = Gray8BmpFileExporter.LoadGray8Bmp(Path.Combine(suiteDir, "Case05_DuplicateTeachAttempt_And_UnknownForeignObject.bmp"), out int w5, out int h5);
        var view5 = new ReadOnlyImageView2D<byte>(px5, w5, h5, w5);
        var dupPose = new FixturePose2D(340f, 420f, 15.0f * (MathF.PI / 180.0f));
        PartTeachRegistrationResult dupReport = pool.RegisterOrMergeOnTeach(
            view5, in dupPose, partRoiW, partRoiH, "PartA_DuplicateTry", childRoisA, workspace);

        AutoTeachingResult autoRes5 = AutoTeachingOrchestrator.RecognizeAndAutoTeach(
            view5, pool, workspace, minGeometricScore: 0.55f, minAppearanceCosine: 0.55f, maxMahalanobisDistance: 20.0f);
        sw5.Stop();

        bool case5Passed = dupReport.WasMergedIntoExisting &&
                           pool.Count == 3 &&
                           ExistsPartNear(autoRes5.Instances, "PartA_Bracket", 340f, 420f, 5.0f, out _);
        results.Add(new VerificationCaseSummary(
            "AutoTeaching",
            "Case05_DuplicateTeachAttempt_And_UnknownForeignObject.bmp",
            case5Passed,
            sw5.Elapsed.TotalMilliseconds,
            $"Merged={dupReport.WasMergedIntoExisting},Sim={dupReport.MatchedSimilarity:F3}, PoolCount={pool.Count}, Summary='{autoRes5.SummaryText}'"));

        // Case 06: Full 8K (7680x4320) Mass Production Panel (3x5 = 15 Parts) — single full-8K call
        byte[] px6 = Gray8BmpFileExporter.LoadGray8Bmp(Path.Combine(suiteDir, "Case06_Full8K_MassProductionPanel_7680x4320.bmp"), out int w6, out int h6);
        var view6 = new ReadOnlyImageView2D<byte>(px6, w6, h6, w6);
        var sw6 = Stopwatch.StartNew();
        AutoTeachingResult panelRes6 = AutoTeachingOrchestrator.RecognizeAndAutoTeach(
            view6, pool, workspace, minGeometricScore: 0.50f, minAppearanceCosine: 0.45f, maxMahalanobisDistance: 25.0f);
        sw6.Stop();
        int recognizedCells = panelRes6.Instances.Length;
        results.Add(new VerificationCaseSummary(
            "AutoTeaching",
            "Case06_Full8K_MassProductionPanel_7680x4320.bmp",
            recognizedCells == 15,
            sw6.Elapsed.TotalMilliseconds,
            $"RecognizedPanelCells={recognizedCells}/15"));
    }

    private static void TeachGoldenPartFile(
        string suiteDir,
        string fileName,
        string partName,
        in FixturePose2D pose,
        float roiW,
        float roiH,
        ReadOnlySpan<ChildRoiTemplateSpec> childRois,
        PartModelPool pool,
        VisionWorkspace workspace,
        List<VerificationCaseSummary> results)
    {
        var sw = Stopwatch.StartNew();
        byte[] px = Gray8BmpFileExporter.LoadGray8Bmp(Path.Combine(suiteDir, fileName), out int w, out int h);
        var view = new ReadOnlyImageView2D<byte>(px, w, h, w);
        PartTeachRegistrationResult report = pool.RegisterOrMergeOnTeach(view, in pose, roiW, roiH, partName, childRois, workspace);
        sw.Stop();

        bool passed = !report.WasMergedIntoExisting && report.AssignedId.IsValid;
        results.Add(new VerificationCaseSummary(
            "AutoTeaching",
            fileName,
            passed,
            sw.Elapsed.TotalMilliseconds,
            $"PartName='{partName}', AssignedId={report.AssignedId}, Merged={report.WasMergedIntoExisting}"));
    }

    private static bool ExistsPartNear(
        ReadOnlySpan<DetectedPartInstance> instances,
        string expectedPartName,
        float expectedX,
        float expectedY,
        float tolerancePx,
        out DetectedPartInstance matchedInstance)
    {
        for (int i = 0; i < instances.Length; i++)
        {
            DetectedPartInstance inst = instances[i];
            if (!string.Equals(inst.PartName, expectedPartName, StringComparison.Ordinal))
            {
                continue;
            }

            float dx = inst.WorldPose.OriginX - expectedX;
            float dy = inst.WorldPose.OriginY - expectedY;
            if (MathF.Sqrt(dx * dx + dy * dy) <= tolerancePx)
            {
                matchedInstance = inst;
                return true;
            }
        }

        matchedInstance = default;
        return false;
    }
}
