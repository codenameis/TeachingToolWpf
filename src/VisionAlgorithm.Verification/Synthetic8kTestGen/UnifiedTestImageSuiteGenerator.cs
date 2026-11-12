using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using VisionAlgorithm.OpenSrc.AutoTeaching;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.IndustrialOcrOcv;

namespace VisionAlgorithm.OpenSrc.Synthetic8kTestGen;

/// <summary>
/// Procedural SDF + erf anti-aliased synthetic image generator for TaughtFontOcvVerifier (13 cases across two suites)
/// and AutoTeaching (6 cases), including 8K (7680x4320) production sheets and GroundTruth_Manifest.txt export.
/// </summary>
public static class UnifiedTestImageSuiteGenerator
{
    public const int Full8kWidth = 7680;
    public const int Full8kHeight = 4320;
    public const int RoiTestWidth = 640;
    public const int RoiTestHeight = 160;
    public const int PartCanvasWidth = 1024;
    public const int PartCanvasHeight = 768;

    public const float Case04PartACenterX = 280.37f;
    public const float Case04PartACenterY = 319.76f;
    public const float Case04PartAAngleDeg = 23.7f;

    public const float Case04PartBCenterX = 660.42f;
    public const float Case04PartBCenterY = 340.18f;
    public const float Case04PartBAngleDeg = -41.3f;

    public const float Case04PartCCenterX = 979.63f;
    public const float Case04PartCCenterY = 600.29f;
    public const float Case04PartCAngleDeg = 112.4f;

    private const byte DefaultBgIntensity = 232; // Light background (darkOnLight = true)
    private const byte DefaultFgIntensity = 28;  // Dark ink foreground
    private const float ErfBlurScale = 0.85f;
    private const float ErfApproxA1 = 0.278393f;
    private const float ErfApproxA2 = 0.230389f;
    private const float ErfApproxA3 = 0.000972f;
    private const float ErfApproxA4 = 0.078108f;

    public const string DefaultCatalogAlphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ.-/";

    /// <summary>
    /// Canonical 5x7 dot-matrix / stroke bitmap definitions for 0-9, A-Z, '.', '-', '/'.
    /// Each entry is 7 rows of 5-bit masks (MSB = left column 0, LSB = right column 4).
    /// </summary>
    private static readonly Dictionary<char, byte[]> Canonical5x7Bitmaps = new()
    {
        ['0'] = new byte[] { 0b01110, 0b10001, 0b10011, 0b10101, 0b11001, 0b10001, 0b01110 },
        ['1'] = new byte[] { 0b00100, 0b01100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110 },
        ['2'] = new byte[] { 0b01110, 0b10001, 0b00001, 0b00110, 0b01000, 0b10000, 0b11111 },
        ['3'] = new byte[] { 0b11110, 0b00001, 0b00001, 0b01110, 0b00001, 0b00001, 0b11110 },
        ['4'] = new byte[] { 0b00010, 0b00110, 0b01010, 0b10010, 0b11111, 0b00010, 0b00010 },
        ['5'] = new byte[] { 0b11111, 0b10000, 0b11110, 0b00001, 0b00001, 0b10001, 0b01110 },
        ['6'] = new byte[] { 0b00110, 0b01000, 0b10000, 0b11110, 0b10001, 0b10001, 0b01110 },
        ['7'] = new byte[] { 0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b01000, 0b01000 },
        ['8'] = new byte[] { 0b01110, 0b10001, 0b10001, 0b01110, 0b10001, 0b10001, 0b01110 },
        ['9'] = new byte[] { 0b01110, 0b10001, 0b10001, 0b01111, 0b00001, 0b00010, 0b01100 },
        ['A'] = new byte[] { 0b01110, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001 },
        ['B'] = new byte[] { 0b11110, 0b10001, 0b10001, 0b11110, 0b10001, 0b10001, 0b11110 },
        ['C'] = new byte[] { 0b01110, 0b10001, 0b10000, 0b10000, 0b10000, 0b10001, 0b01110 },
        ['D'] = new byte[] { 0b11100, 0b10010, 0b10001, 0b10001, 0b10001, 0b10010, 0b11100 },
        ['E'] = new byte[] { 0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b11111 },
        ['F'] = new byte[] { 0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b10000 },
        ['G'] = new byte[] { 0b01110, 0b10001, 0b10000, 0b10111, 0b10001, 0b10001, 0b01111 },
        ['H'] = new byte[] { 0b10001, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001 },
        ['I'] = new byte[] { 0b01110, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110 },
        ['J'] = new byte[] { 0b00111, 0b00010, 0b00010, 0b00010, 0b10010, 0b10010, 0b01100 },
        ['K'] = new byte[] { 0b10001, 0b10010, 0b10100, 0b11000, 0b10100, 0b10010, 0b10001 },
        ['L'] = new byte[] { 0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b11111 },
        ['M'] = new byte[] { 0b10001, 0b11011, 0b10101, 0b10101, 0b10001, 0b10001, 0b10001 },
        ['N'] = new byte[] { 0b10001, 0b11001, 0b10101, 0b10011, 0b10001, 0b10001, 0b10001 },
        ['O'] = new byte[] { 0b01110, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110 },
        ['P'] = new byte[] { 0b11110, 0b10001, 0b10001, 0b11110, 0b10000, 0b10000, 0b10000 },
        ['Q'] = new byte[] { 0b01110, 0b10001, 0b10001, 0b10001, 0b10101, 0b10010, 0b01101 },
        ['R'] = new byte[] { 0b11110, 0b10001, 0b10001, 0b11110, 0b10100, 0b10010, 0b10001 },
        ['S'] = new byte[] { 0b01111, 0b10000, 0b10000, 0b01110, 0b00001, 0b00001, 0b11110 },
        ['T'] = new byte[] { 0b11111, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100 },
        ['U'] = new byte[] { 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110 },
        ['V'] = new byte[] { 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01010, 0b00100 },
        ['W'] = new byte[] { 0b10001, 0b10001, 0b10001, 0b10101, 0b10101, 0b10101, 0b01010 },
        ['X'] = new byte[] { 0b10001, 0b10001, 0b01010, 0b00100, 0b01010, 0b10001, 0b10001 },
        ['Y'] = new byte[] { 0b10001, 0b10001, 0b01010, 0b00100, 0b00100, 0b00100, 0b00100 },
        ['Z'] = new byte[] { 0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b10000, 0b11111 },
        ['.'] = new byte[] { 0b00000, 0b00000, 0b00000, 0b00000, 0b00000, 0b01100, 0b01100 },
        ['-'] = new byte[] { 0b00000, 0b00000, 0b00000, 0b11111, 0b00000, 0b00000, 0b00000 },
        ['/'] = new byte[] { 0b00001, 0b00010, 0b00010, 0b00100, 0b01000, 0b01000, 0b10000 },
        [' '] = new byte[] { 0b00000, 0b00000, 0b00000, 0b00000, 0b00000, 0b00000, 0b00000 }
    };

    /// <summary>
    /// Renders a tightly cropped canonical character patch (matching the exact ink bounding box segmented at runtime)
    /// for font model and sequence model training.
    /// </summary>
    public static byte[] RenderCanonicalGlyphPatch(
        char ch,
        int fullWidth,
        int fullHeight,
        out int croppedWidth,
        out int croppedHeight,
        bool continuousStroke = true,
        byte bg = DefaultBgIntensity,
        byte fg = DefaultFgIntensity)
    {
        byte[] pixels = new byte[fullWidth * fullHeight];
        Array.Fill(pixels, bg);
        RenderGlyphIntoBuffer(
            pixels,
            fullWidth,
            fullHeight,
            fullWidth,
            ch,
            x0: 4.0f,
            y0: 4.0f,
            glyphWidth: 16.0f,
            glyphHeight: 26.0f,
            continuousStroke: continuousStroke,
            fgIntensity: fg);

        // Tightly crop to the active ink bounding box just like OcrMaxSegmenter and SimdColumnFeatureEncoder do
        int minX = fullWidth;
        int maxX = -1;
        int minY = fullHeight;
        int maxY = -1;
        int inkThreshold = (bg + fg) / 2;
        bool darkOnLight = fg < bg;

        for (int y = 0; y < fullHeight; y++)
        {
            for (int x = 0; x < fullWidth; x++)
            {
                byte v = pixels[y * fullWidth + x];
                bool isInk = darkOnLight ? (v < inkThreshold) : (v > inkThreshold);
                if (isInk)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }

        if (maxX < minX || maxY < minY)
        {
            croppedWidth = fullWidth;
            croppedHeight = fullHeight;
            return pixels;
        }

        croppedWidth = (maxX - minX) + 1;
        croppedHeight = (maxY - minY) + 1;
        byte[] cropped = new byte[croppedWidth * croppedHeight];
        for (int y = 0; y < croppedHeight; y++)
        {
            for (int x = 0; x < croppedWidth; x++)
            {
                cropped[y * croppedWidth + x] = pixels[(minY + y) * fullWidth + (minX + x)];
            }
        }

        return cropped;
    }

    /// <summary>
    /// Trains the complete IndustrialOcrFontModel for all characters in DefaultCatalogAlphabet.
    /// </summary>
    public static IndustrialOcrFontModel TrainDefaultIndustrialFontModel()
    {
        const int trainPatchW = 28;
        const int trainPatchH = 40;
        var samples = new GoldenCharSample[DefaultCatalogAlphabet.Length];

        for (int i = 0; i < DefaultCatalogAlphabet.Length; i++)
        {
            char c = DefaultCatalogAlphabet[i];
            byte[] strokePixels = RenderCanonicalGlyphPatch(
                c, trainPatchW, trainPatchH, out int cw, out int ch, continuousStroke: true);
            samples[i] = new GoldenCharSample(c, strokePixels, cw, ch, cw, DarkOnLight: true);
        }

        return IndustrialOcrTrainer.Train(samples);
    }

    /// <summary>
    /// Exports all 19 .bmp test cases and GroundTruth_Manifest.txt to disk.
    /// </summary>
    public static void ExportAllTestSuitesToDisk(
        string rootOutputDir,
        bool includeFull8kBmp = true,
        int sheetWidth = Full8kWidth,
        int sheetHeight = Full8kHeight)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootOutputDir);
        ArgumentOutOfRangeException.ThrowIfLessThan(sheetWidth, RoiTestWidth * 8);
        ArgumentOutOfRangeException.ThrowIfLessThan(sheetHeight, RoiTestHeight * 4);

        string ocrOcvDir = Path.Combine(rootOutputDir, "IndustrialOcrOcv");
        string lightOcrDir = Path.Combine(rootOutputDir, "LightweightOcr");
        string autoTeachDir = Path.Combine(rootOutputDir, "AutoTeaching");

        Directory.CreateDirectory(ocrOcvDir);
        Directory.CreateDirectory(lightOcrDir);
        Directory.CreateDirectory(autoTeachDir);

        var manifest = new StringBuilder();
        manifest.AppendLine("# Unified Vision Subsystems GroundTruth Manifest");
        manifest.AppendLine("# Format: Suite | FileName | WidthxHeight | GroundTruthStringOrParts | ExpectedStatus | Notes");

        // ====================================================================
        // Suite 1: IndustrialOcrOcv (7 Cases)
        // ====================================================================
        SaveTextCaseBmp(
            Path.Combine(ocrOcvDir, "Case01_CleanLaserLot_LOT2026A09.bmp"),
            RoiTestWidth, RoiTestHeight, "LOT-2026-A09",
            continuousStroke: true, kerningScale: 1.0f, dotDropoutRatio: 0f,
            invertPolarity: false, strokeDefectCharIndex: -1, strokeDefectMissing: false, illuminationSlope: 0f,
            manifest, "IndustrialOcrOcv", "PASS (OcrPass & OcvPass)");

        SaveTextCaseBmp(
            Path.Combine(ocrOcvDir, "Case02_DotMatrixInkjet_EXP20271124.bmp"),
            RoiTestWidth, RoiTestHeight, "EXP.2027.11.24",
            continuousStroke: false, kerningScale: 1.0f, dotDropoutRatio: 0.0f,
            invertPolarity: false, strokeDefectCharIndex: -1, strokeDefectMissing: false, illuminationSlope: 0f,
            manifest, "IndustrialOcrOcv", "PASS (Dot-Matrix Connected Bridge)");

        SaveTextCaseBmp(
            Path.Combine(ocrOcvDir, "Case03_TouchingKerning_SN884102B.bmp"),
            RoiTestWidth, RoiTestHeight, "SN884102B",
            continuousStroke: true, kerningScale: 0.78f, dotDropoutRatio: 0f,
            invertPolarity: false, strokeDefectCharIndex: -1, strokeDefectMissing: false, illuminationSlope: 0f,
            manifest, "IndustrialOcrOcv", "PASS (Touching Character Valley Split)");

        SaveTextCaseBmp(
            Path.Combine(ocrOcvDir, "Case04_ConfusionAmbiguity_B80ODQ1I5S2Z.bmp"),
            RoiTestWidth, RoiTestHeight, "B80ODQ1I5S2Z",
            continuousStroke: true, kerningScale: 1.0f, dotDropoutRatio: 0f,
            invertPolarity: false, strokeDefectCharIndex: -1, strokeDefectMissing: false, illuminationSlope: 0f,
            manifest, "IndustrialOcrOcv", "PASS (Fielding Mask Disambiguates Confusable Pairs)");

        SaveTextCaseBmp(
            Path.Combine(ocrOcvDir, "Case05_OcvStrokeDefect_LOT2026A09_Broken8.bmp"),
            RoiTestWidth, RoiTestHeight, "LOT-2026-A09",
            continuousStroke: true, kerningScale: 1.0f, dotDropoutRatio: 0f,
            invertPolarity: false, strokeDefectCharIndex: 6, strokeDefectMissing: true, illuminationSlope: 0f,
            manifest, "IndustrialOcrOcv", "NG_OCV (Character Index 6 Missing Stroke / Ink Smear Defect)");

        SaveTextCaseBmp(
            Path.Combine(ocrOcvDir, "Case06_RotatedUnevenIllum_LOT2026A09_Rot7Deg.bmp"),
            RoiTestWidth, RoiTestHeight, "LOT-2026-A09",
            continuousStroke: true, kerningScale: 1.0f, dotDropoutRatio: 0f,
            invertPolarity: false, strokeDefectCharIndex: -1, strokeDefectMissing: false, illuminationSlope: 45f,
            manifest, "IndustrialOcrOcv", "PASS (FixturePose2D +7.5 deg Dewarp + Uneven Illumination)",
            rotationDeg: 7.5f);

        if (includeFull8kBmp)
        {
            Save8kMultiTextSheetBmp(
                Path.Combine(ocrOcvDir, "Case07_Full8K_MultiBlisterSheet_7680x4320.bmp"),
                "LOT-2026-A09",
                defectiveCellIndex: 11,
                manifest, "IndustrialOcrOcv",
                "4x6=24 Blister Cells on 7680x4320 (23 PASS, Cell #11 NG_OCV)",
                sheetWidth: sheetWidth,
                sheetHeight: sheetHeight);
        }

        // ====================================================================
        // Suite 2: LightweightOcr (6 Cases)
        // ====================================================================
        SaveTextCaseBmp(
            Path.Combine(lightOcrDir, "Case01_SingleLineSerial_WAFER094817A2.bmp"),
            RoiTestWidth, RoiTestHeight, "WAFER-094817-A2",
            continuousStroke: true, kerningScale: 1.0f, dotDropoutRatio: 0f,
            invertPolarity: false, strokeDefectCharIndex: -1, strokeDefectMissing: false, illuminationSlope: 0f,
            manifest, "LightweightOcr", "PASS (1D Column Feature + CTC)");

        SaveMultiLineTextCaseBmp(
            Path.Combine(lightOcrDir, "Case02_MultiLineLabel_3Lines.bmp"),
            RoiTestWidth, 260,
            new[] { "PN-99402-A", "LOT-2026-11", "QTY-0500" },
            manifest, "LightweightOcr", "PASS (3-Line RLE Text Band Detection + CTC)");

        SaveTextCaseBmp(
            Path.Combine(lightOcrDir, "Case03_VariablePitchSkew_BATCH7731X9.bmp"),
            RoiTestWidth, RoiTestHeight, "BATCH-7731-X9",
            continuousStroke: true, kerningScale: 0.92f, dotDropoutRatio: 0f,
            invertPolarity: false, strokeDefectCharIndex: -1, strokeDefectMissing: false, illuminationSlope: 0f,
            manifest, "LightweightOcr", "PASS (Skewed +4.0 deg Dewarp + Variable Pitch)",
            rotationDeg: 4.0f);

        SaveTextCaseBmp(
            Path.Combine(lightOcrDir, "Case04_LowContrastNoisy_PCM440910Z.bmp"),
            RoiTestWidth, RoiTestHeight, "PCM-4409-10Z",
            continuousStroke: true, kerningScale: 1.0f, dotDropoutRatio: 0f,
            invertPolarity: false, strokeDefectCharIndex: -1, strokeDefectMissing: false, illuminationSlope: 25f,
            manifest, "LightweightOcr", "PASS (Low Contrast + Uneven Illumination)",
            rotationDeg: 0f,
            lowContrast: true);

        SaveTextCaseBmp(
            Path.Combine(lightOcrDir, "Case05_InvertedDarkOnBright_INV20268801.bmp"),
            RoiTestWidth, RoiTestHeight, "INV-2026-8801",
            continuousStroke: true, kerningScale: 1.0f, dotDropoutRatio: 0f,
            invertPolarity: true, strokeDefectCharIndex: -1, strokeDefectMissing: false, illuminationSlope: 0f,
            manifest, "LightweightOcr", "PASS (Light-on-Dark Inverted Polarity)");

        if (includeFull8kBmp)
        {
            Save8kMultiTextSheetBmp(
                Path.Combine(lightOcrDir, "Case06_Full8K_MultiLabelSheet_7680x4320.bmp"),
                "WAFER-094817-A2",
                defectiveCellIndex: -1,
                manifest, "LightweightOcr",
                "4x8=32 High-Speed Labels on 7680x4320",
                cols: 8, rows: 4,
                sheetWidth: sheetWidth,
                sheetHeight: sheetHeight);
        }

        // ====================================================================
        // Suite 3: AutoTeaching (6 Cases)
        // ====================================================================
        SavePartGoldenBmp(
            Path.Combine(autoTeachDir, "Case01_GoldenPartA_BracketWithHoleAndSlot.bmp"),
            PartCanvasWidth, PartCanvasHeight, partType: 1, centerX: 512f, centerY: 384f, angleDeg: 0f,
            manifest, "Golden Part #1: Concentric Double-Ring Hole + L-Corner");

        SavePartGoldenBmp(
            Path.Combine(autoTeachDir, "Case02_GoldenPartB_ConnectorWithPins.bmp"),
            PartCanvasWidth, PartCanvasHeight, partType: 2, centerX: 512f, centerY: 384f, angleDeg: 0f,
            manifest, "Golden Part #2: Square IC Body + 4 Parallel Lead Pins + Pin-1 Dot");

        SavePartGoldenBmp(
            Path.Combine(autoTeachDir, "Case03_GoldenPartC_ShieldCanWithFiducial.bmp"),
            PartCanvasWidth, PartCanvasHeight, partType: 3, centerX: 512f, centerY: 384f, angleDeg: 0f,
            manifest, "Golden Part #3: Symmetric Cross Fiducial Mark + Chamfered Frame");

        SaveMultiPartSceneBmp(
            Path.Combine(autoTeachDir, "Case04_MixedMultiPartScene_RotatedOccluded.bmp"),
            1280, 960, includeDistractors: false,
            manifest, "Mixed Multi-Part Scene: Part #1 (+23.5 deg), Part #2 (-41.2 deg), Part #3 (+112.0 deg)");

        SaveMultiPartSceneBmp(
            Path.Combine(autoTeachDir, "Case05_DuplicateTeachAttempt_And_UnknownForeignObject.bmp"),
            1280, 960, includeDistractors: true,
            manifest, "Part #1 at +15 deg (Duplicate Teach Guard Test) + Unknown Foreign Distractors");

        if (includeFull8kBmp)
        {
            Save8kAutoTeachingPanelBmp(
                Path.Combine(autoTeachDir, "Case06_Full8K_MassProductionPanel_7680x4320.bmp"),
                manifest,
                sheetWidth: sheetWidth,
                sheetHeight: sheetHeight);
        }

        File.WriteAllText(Path.Combine(rootOutputDir, "GroundTruth_Manifest.txt"), manifest.ToString(), Encoding.UTF8);
    }

    /// <summary>
    /// Renders a single-line synthetic text image into a byte array.
    /// </summary>
    public static byte[] CreateSingleLineTextImage(
        int width,
        int height,
        string text,
        bool continuousStroke,
        float kerningScale,
        float dotDropoutRatio,
        bool invertPolarity,
        int strokeDefectCharIndex,
        bool strokeDefectMissing,
        float illuminationSlope,
        float rotationDeg,
        bool lowContrast,
        out FixturePose2D textFixturePose)
    {
        byte bg = invertPolarity ? (byte)32 : (lowContrast ? (byte)190 : DefaultBgIntensity);
        byte fg = invertPolarity ? (byte)228 : (lowContrast ? (byte)100 : DefaultFgIntensity);

        byte[] basePixels = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float grad = illuminationSlope * ((float)x / width - 0.5f);
                int val = (int)MathF.Round(bg + grad);
                basePixels[y * width + x] = (byte)Math.Clamp(val, 0, 255);
            }
        }

        float glyphW = 16.0f;
        float glyphH = 26.0f;
        float pitchX = MathF.Round(24.0f * kerningScale);
        float totalW = (text.Length - 1) * pitchX + glyphW;
        float startX = MathF.Max(20.0f, (width - totalW) * 0.5f);
        float startY = (height - glyphH) * 0.5f;

        for (int i = 0; i < text.Length; i++)
        {
            float gx = startX + i * pitchX;
            bool defectHere = (i == strokeDefectCharIndex);
            RenderGlyphIntoBuffer(
                basePixels,
                width,
                height,
                width,
                text[i],
                gx,
                startY,
                glyphW,
                glyphH,
                continuousStroke,
                fg,
                dotDropoutRatio,
                defectHere,
                strokeDefectMissing,
                seed: 1000 + i * 37);
        }

        if (MathF.Abs(rotationDeg) > 1e-3f)
        {
            float angleRad = rotationDeg * (MathF.PI / 180.0f);
            float cx = width * 0.5f;
            float cy = height * 0.5f;
            byte[] rotated = RotateImageBilinear(basePixels, width, height, cx, cy, angleRad, bg);
            textFixturePose = new FixturePose2D(cx, cy, angleRad);
            return rotated;
        }

        textFixturePose = new FixturePose2D(width * 0.5f, height * 0.5f, 0f);
        return basePixels;
    }

    public static byte[] CreateMultiLineTextImage(
        int width,
        int height,
        IReadOnlyList<string> lines)
    {
        byte[] pixels = new byte[width * height];
        Array.Fill(pixels, DefaultBgIntensity);

        float glyphW = 16.0f;
        float glyphH = 26.0f;
        float pitchX = 24.0f;
        float lineSpacing = 64.0f;
        float startY = 36.0f;

        for (int lineIdx = 0; lineIdx < lines.Count; lineIdx++)
        {
            string line = lines[lineIdx];
            float y0 = startY + lineIdx * lineSpacing;
            float totalW = (line.Length - 1) * pitchX + glyphW;
            float startX = (width - totalW) * 0.5f;
            for (int i = 0; i < line.Length; i++)
            {
                RenderGlyphIntoBuffer(
                    pixels,
                    width,
                    height,
                    width,
                    line[i],
                    startX + i * pitchX,
                    y0,
                    glyphW,
                    glyphH,
                    continuousStroke: true,
                    fgIntensity: DefaultFgIntensity);
            }
        }

        return pixels;
    }

    /// <summary>
    /// Renders one of the 3 distinct part types into an image buffer at the specified center and rotation angle.
    /// </summary>
    public static void RenderPartIntoBuffer(
        byte[] buffer,
        int width,
        int height,
        int stride,
        int partType,
        float centerX,
        float centerY,
        float angleDeg)
        => StandardPartArchetypeRenderer.RenderPartIntoBuffer(buffer, width, height, stride, partType, centerX, centerY, angleDeg);

    public static void RenderDistractorIntoBuffer(
        byte[] buffer,
        int width,
        int height,
        int stride,
        int distractorKind,
        float centerX,
        float centerY)
    {
        int halfExtent = 80;
        int minX = Math.Max(1, (int)(centerX - halfExtent));
        int maxX = Math.Min(width - 2, (int)(centerX + halfExtent));
        int minY = Math.Max(1, (int)(centerY - halfExtent));
        int maxY = Math.Min(height - 2, (int)(centerY + halfExtent));

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                float lx = x - centerX;
                float ly = y - centerY;
                float r = MathF.Sqrt(lx * lx + ly * ly);
                float sdf = distractorKind == 0
                    ? MathF.Abs(r - 18.0f) - 2.0f
                    : MathF.Max(MathF.Abs(lx) * 0.866f + ly * 0.5f, -ly) - 22.0f;

                if (sdf < 2.0f)
                {
                    float alpha = 0.5f * (1.0f - ErfApprox(sdf * ErfBlurScale));
                    byte cur = buffer[y * stride + x];
                    int blended = (int)MathF.Round(cur + (DefaultFgIntensity - cur) * alpha);
                    buffer[y * stride + x] = (byte)Math.Clamp(blended, 0, 255);
                }
            }
        }
    }

    private static void RenderGlyphIntoBuffer(
        byte[] buffer,
        int width,
        int height,
        int stride,
        char ch,
        float x0,
        float y0,
        float glyphWidth,
        float glyphHeight,
        bool continuousStroke,
        byte fgIntensity,
        float dotDropoutRatio = 0f,
        bool injectDefect = false,
        bool defectMissingStroke = false,
        int seed = 42)
    {
        char upper = char.ToUpperInvariant(ch);
        if (!Canonical5x7Bitmaps.TryGetValue(upper, out byte[]? rows))
        {
            return;
        }

        float cellW = glyphWidth / 5.0f;
        float cellH = glyphHeight / 7.0f;
        float dotRadius = MathF.Min(cellW, cellH) * (continuousStroke ? 0.58f : 0.44f);

        int minX = Math.Max(0, (int)MathF.Floor(x0 - 3.0f));
        int maxX = Math.Min(width - 1, (int)MathF.Ceiling(x0 + glyphWidth + 3.0f));
        int minY = Math.Max(0, (int)MathF.Floor(y0 - 3.0f));
        int maxY = Math.Min(height - 1, (int)MathF.Ceiling(y0 + glyphHeight + 3.0f));

        for (int py = minY; py <= maxY; py++)
        {
            for (int px = minX; px <= maxX; x_step(ref px))
            {
                float minDist = 1e6f;

                for (int r = 0; r < 7; r++)
                {
                    if (injectDefect && defectMissingStroke && r >= 3)
                    {
                        continue;
                    }

                    byte rowMask = rows[r];
                    for (int c = 0; c < 5; c++)
                    {
                        bool on = ((rowMask >> (4 - c)) & 1) != 0;
                        if (!on)
                        {
                            continue;
                        }

                        if (!continuousStroke && dotDropoutRatio > 0f)
                        {
                            int hash = ((r * 31 + c * 17 + seed) * 1103515245) & 0x7fffffff;
                            float u = (hash % 1000) / 1000.0f;
                            if (u < dotDropoutRatio * 0.5f && r > 0 && r < 6)
                            {
                                continue;
                            }
                        }

                        float cx = x0 + (c + 0.5f) * cellW;
                        float cy = y0 + (r + 0.5f) * cellH;
                        float d = MathF.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy)) - dotRadius;
                        if (d < minDist)
                        {
                            minDist = d;
                        }

                        if (continuousStroke)
                        {
                            if (c + 1 < 5 && ((rowMask >> (4 - (c + 1))) & 1) != 0)
                            {
                                float nx = x0 + (c + 1.5f) * cellW;
                                float segD = DistanceToSegment(px, py, cx, cy, nx, cy) - dotRadius * 0.92f;
                                if (segD < minDist) minDist = segD;
                            }
                            if (r + 1 < 7 && ((rows[r + 1] >> (4 - c)) & 1) != 0)
                            {
                                float ny = y0 + (r + 1.5f) * cellH;
                                float segD = DistanceToSegment(px, py, cx, cy, cx, ny) - dotRadius * 0.92f;
                                if (segD < minDist) minDist = segD;
                            }
                        }
                    }
                }

                if (minDist < 2.5f)
                {
                    float alpha = 0.5f * (1.0f - ErfApprox(minDist * ErfBlurScale));
                    byte cur = buffer[py * stride + px];
                    int blended = (int)MathF.Round(cur + (fgIntensity - cur) * alpha);
                    buffer[py * stride + px] = (byte)Math.Clamp(blended, 0, 255);
                }
            }
        }
    }

    private static void x_step(ref int px) => px++;

    private static float DistanceToSegment(float px, float py, float ax, float ay, float bx, float by)
    {
        float vx = bx - ax;
        float vy = by - ay;
        float wx = px - ax;
        float wy = py - ay;
        float c1 = vx * wx + vy * wy;
        if (c1 <= 0f) return MathF.Sqrt(wx * wx + wy * wy);
        float c2 = vx * vx + vy * vy;
        if (c2 <= c1) return MathF.Sqrt((px - bx) * (px - bx) + (py - by) * (py - by));
        float t = c1 / c2;
        float projX = ax + t * vx;
        float projY = ay + t * vy;
        return MathF.Sqrt((px - projX) * (px - projX) + (py - projY) * (py - projY));
    }

    private static byte[] RotateImageBilinear(
        byte[] src,
        int width,
        int height,
        float cx,
        float cy,
        float angleRad,
        byte borderValue)
    {
        byte[] dst = new byte[width * height];
        float cos = MathF.Cos(angleRad);
        float sin = MathF.Sin(angleRad);
        var srcView = new ReadOnlyImageView2D<byte>(src, width, height, width);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float dx = x - cx;
                float dy = y - cy;
                float sx = cx + dx * cos + dy * sin;
                float sy = cy - dx * sin + dy * cos;
                if (sx >= 1 && sx < width - 2 && sy >= 1 && sy < height - 2)
                {
                    float val = SubpixelInterpolator.SampleBilinear(srcView, sx, sy);
                    dst[y * width + x] = (byte)Math.Clamp((int)MathF.Round(val), 0, 255);
                }
                else
                {
                    dst[y * width + x] = borderValue;
                }
            }
        }

        return dst;
    }

    private static void SaveTextCaseBmp(
        string filePath,
        int width,
        int height,
        string text,
        bool continuousStroke,
        float kerningScale,
        float dotDropoutRatio,
        bool invertPolarity,
        int strokeDefectCharIndex,
        bool strokeDefectMissing,
        float illuminationSlope,
        StringBuilder manifest,
        string suiteName,
        string notes,
        float rotationDeg = 0f,
        bool lowContrast = false)
    {
        byte[] pixels = CreateSingleLineTextImage(
            width, height, text, continuousStroke, kerningScale, dotDropoutRatio,
            invertPolarity, strokeDefectCharIndex, strokeDefectMissing,
            illuminationSlope, rotationDeg, lowContrast, out _);

        Gray8BmpFileExporter.SaveGray8Bmp(filePath, new ReadOnlyImageView2D<byte>(pixels, width, height, width));
        manifest.AppendLine($"{suiteName} | {Path.GetFileName(filePath)} | {width}x{height} | {text} | {notes}");
    }

    private static void SaveMultiLineTextCaseBmp(
        string filePath,
        int width,
        int height,
        IReadOnlyList<string> lines,
        StringBuilder manifest,
        string suiteName,
        string notes)
    {
        byte[] pixels = CreateMultiLineTextImage(width, height, lines);
        Gray8BmpFileExporter.SaveGray8Bmp(filePath, new ReadOnlyImageView2D<byte>(pixels, width, height, width));
        manifest.AppendLine($"{suiteName} | {Path.GetFileName(filePath)} | {width}x{height} | {string.Join(" / ", lines)} | {notes}");
    }

    private static void Save8kMultiTextSheetBmp(
        string filePath,
        string text,
        int defectiveCellIndex,
        StringBuilder manifest,
        string suiteName,
        string notes,
        int cols = 6,
        int rows = 4,
        int sheetWidth = Full8kWidth,
        int sheetHeight = Full8kHeight)
    {
        byte[] sheet = new byte[sheetWidth * sheetHeight];
        Array.Fill(sheet, DefaultBgIntensity);

        int cellW = sheetWidth / cols;
        int cellH = sheetHeight / rows;

        byte[] cleanTile = CreateSingleLineTextImage(
            RoiTestWidth, RoiTestHeight, text,
            continuousStroke: true, kerningScale: 1.0f, dotDropoutRatio: 0f,
            invertPolarity: false, strokeDefectCharIndex: -1, strokeDefectMissing: false,
            illuminationSlope: 0f, rotationDeg: 0f, lowContrast: false, out _);

        byte[] defectTile = defectiveCellIndex >= 0
            ? CreateSingleLineTextImage(
                RoiTestWidth, RoiTestHeight, text,
                continuousStroke: true, kerningScale: 1.0f, dotDropoutRatio: 0f,
                invertPolarity: false, strokeDefectCharIndex: 6, strokeDefectMissing: true,
                illuminationSlope: 0f, rotationDeg: 0f, lowContrast: false, out _)
            : cleanTile;

        int idx = 0;
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                int ox = c * cellW + (cellW - RoiTestWidth) / 2;
                int oy = r * cellH + (cellH - RoiTestHeight) / 2;
                byte[] tile = (idx == defectiveCellIndex) ? defectTile : cleanTile;

                for (int ty = 0; ty < RoiTestHeight; ty++)
                {
                    Buffer.BlockCopy(tile, ty * RoiTestWidth, sheet, (oy + ty) * sheetWidth + ox, RoiTestWidth);
                }
                idx++;
            }
        }

        Gray8BmpFileExporter.SaveGray8Bmp(filePath, new ReadOnlyImageView2D<byte>(sheet, sheetWidth, sheetHeight, sheetWidth));
        manifest.AppendLine($"{suiteName} | {Path.GetFileName(filePath)} | {sheetWidth}x{sheetHeight} | {text} (x{cols * rows}) | {notes}");
    }

    private static void SavePartGoldenBmp(
        string filePath,
        int width,
        int height,
        int partType,
        float centerX,
        float centerY,
        float angleDeg,
        StringBuilder manifest,
        string notes)
    {
        byte[] pixels = new byte[width * height];
        Array.Fill(pixels, DefaultBgIntensity);
        RenderPartIntoBuffer(pixels, width, height, width, partType, centerX, centerY, angleDeg);
        Gray8BmpFileExporter.SaveGray8Bmp(filePath, new ReadOnlyImageView2D<byte>(pixels, width, height, width));
        manifest.AppendLine($"AutoTeaching | {Path.GetFileName(filePath)} | {width}x{height} | Part#{partType}@({centerX},{centerY},{angleDeg}deg) | {notes}");
    }

    private static void SaveMultiPartSceneBmp(
        string filePath,
        int width,
        int height,
        bool includeDistractors,
        StringBuilder manifest,
        string notes)
    {
        byte[] pixels = new byte[width * height];
        Array.Fill(pixels, DefaultBgIntensity);

        if (!includeDistractors)
        {
            RenderPartIntoBuffer(pixels, width, height, width, partType: 1, centerX: Case04PartACenterX, centerY: Case04PartACenterY, angleDeg: Case04PartAAngleDeg);
            RenderPartIntoBuffer(pixels, width, height, width, partType: 2, centerX: Case04PartBCenterX, centerY: Case04PartBCenterY, angleDeg: Case04PartBAngleDeg);
            RenderPartIntoBuffer(pixels, width, height, width, partType: 3, centerX: Case04PartCCenterX, centerY: Case04PartCCenterY, angleDeg: Case04PartCAngleDeg);
        }
        else
        {
            RenderPartIntoBuffer(pixels, width, height, width, partType: 1, centerX: 340f, centerY: 420f, angleDeg: 15.0f);
            RenderDistractorIntoBuffer(pixels, width, height, width, distractorKind: 0, centerX: 780f, centerY: 320f);
            RenderDistractorIntoBuffer(pixels, width, height, width, distractorKind: 1, centerX: 960f, centerY: 620f);
        }

        Gray8BmpFileExporter.SaveGray8Bmp(filePath, new ReadOnlyImageView2D<byte>(pixels, width, height, width));
        manifest.AppendLine($"AutoTeaching | {Path.GetFileName(filePath)} | {width}x{height} | MultiPartScene(Distractors={includeDistractors}) | {notes}");
    }

    private static void Save8kAutoTeachingPanelBmp(
        string filePath,
        StringBuilder manifest,
        int sheetWidth = Full8kWidth,
        int sheetHeight = Full8kHeight)
    {
        byte[] sheet = new byte[sheetWidth * sheetHeight];
        Array.Fill(sheet, DefaultBgIntensity);

        const int cols = 5;
        const int rows = 3;
        int cellW = sheetWidth / cols;
        int cellH = sheetHeight / rows;

        int idx = 0;
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                int partType = (idx % 3) + 1;
                float cx = c * cellW + cellW * 0.5f;
                float cy = r * cellH + cellH * 0.5f;
                float angleDeg = (idx * 15.0f) - 90.0f;
                RenderPartIntoBuffer(sheet, sheetWidth, sheetHeight, sheetWidth, partType, cx, cy, angleDeg);
                idx++;
            }
        }

        Gray8BmpFileExporter.SaveGray8Bmp(filePath, new ReadOnlyImageView2D<byte>(sheet, sheetWidth, sheetHeight, sheetWidth));
        manifest.AppendLine($"AutoTeaching | {Path.GetFileName(filePath)} | {sheetWidth}x{sheetHeight} | 3x5=15 Parts (#1,#2,#3 x 5 each) | PASS");
    }

    private static float ErfApprox(float x)
    {
        float sign = x < 0f ? -1.0f : 1.0f;
        float ax = MathF.Abs(x);
        float denom = 1.0f + ErfApproxA1 * ax + ErfApproxA2 * ax * ax + ErfApproxA3 * ax * ax * ax + ErfApproxA4 * ax * ax * ax * ax;
        float denom2 = denom * denom;
        float denom4 = denom2 * denom2;
        return sign * (1.0f - 1.0f / denom4);
    }
}
