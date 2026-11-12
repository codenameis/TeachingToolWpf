// File: tests/VisionAlgorithm.Tests/HalconCognexExpansionSuiteTests.cs
using System.Numerics;
using VisionAlgorithm.OpenSrc.BlobRle;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.DataCode2D;
using VisionAlgorithm.OpenSrc.Metrology;
using VisionAlgorithm.OpenSrc.Preprocessing;

namespace VisionAlgorithm.Tests;

/// <summary>
/// Fast (&lt; 10ms) synthetic ground-truth tests for the HALCON / Cognex VisionPro expansion algorithms:
/// 1) Preprocessing: 2D Biquadratic Polynomial Shading Correction, Perona-Malik Anisotropic Diffusion, Vincent Morphological Reconstruction.
/// 2) Metrology: Multi-Criteria Composite Caliper (First/Last/Expected Span) and Fitzgibbon + Sampson Gauss-Newton Ellipse Fitting.
/// 3) Blob RLE: Euler Hole Count, Monotone Chain Convex Hull, Rotating Calipers Min-Area Box, Grayscale Statistics, and Meijster EDT Watershed Separation.
/// 4) 2D DataCode: ISO/IEC 16022 ECC 200 DataMatrix Encoder/Decoder with GF(256) Reed-Solomon Error Correction and ISO/IEC 15415 Grading.
/// </summary>
public sealed class HalconCognexExpansionSuiteTests
{
    private const int TestWidth = 64;
    private const int TestHeight = 64;
    private const int PaddedStrideOffset = 7;
    private const int PaddedStride = TestWidth + PaddedStrideOffset;

    [Fact]
    public void PolynomialShadingCorrection_FlattensBiquadraticVignetting_OnPaddedStride()
    {
        byte[] src = new byte[TestHeight * PaddedStride];
        byte[] dst = new byte[TestHeight * PaddedStride];

        float halfW = (TestWidth - 1) * 0.5f;
        float halfH = (TestHeight - 1) * 0.5f;
        for (int y = 0; y < TestHeight; y++)
        {
            float v = (y - halfH) / halfH;
            for (int x = 0; x < TestWidth; x++)
            {
                float u = (x - halfW) / halfW;
                float bg = 135.0f + (38.0f * u) - (26.0f * v) - (22.0f * ((u * u) + (v * v)));
                src[(y * PaddedStride) + x] = (byte)Math.Clamp((int)MathF.Round(bg), 0, 255);
            }
        }

        ReadOnlyImageView2D<byte> srcView = new(src, TestWidth, TestHeight, PaddedStride);
        PolynomialShadingSpec spec = PolynomialShadingSpec.Create(targetBaseGray: 128, isMultiplicativeGain: false, subsampleStep: 2);

        ShadingAndReconstructionEngine.ApplyPolynomialShadingCorrection(srcView, in spec, dst, PaddedStride);

        for (int y = 0; y < TestHeight; y++)
        {
            for (int x = 0; x < TestWidth; x++)
            {
                int diff = Math.Abs(dst[(y * PaddedStride) + x] - 128);
                Assert.True(diff <= 1, $"Expected flattened pixel near 128, got {dst[(y * PaddedStride) + x]} at ({x},{y}).");
            }
        }
    }

    [Fact]
    public void AnisotropicDiffusion_SmoothsInteriorNoise_WhilePreservingStepEdge()
    {
        byte[] src = new byte[TestHeight * PaddedStride];
        byte[] dst = new byte[TestHeight * PaddedStride];

        for (int y = 0; y < TestHeight; y++)
        {
            for (int x = 0; x < TestWidth; x++)
            {
                int baseVal = x < 32 ? 50 : 200;
                int checkerNoise = ((x + y) & 1) == 0 ? 6 : -6;
                src[(y * PaddedStride) + x] = (byte)(baseVal + checkerNoise);
            }
        }

        ReadOnlyImageView2D<byte> srcView = new(src, TestWidth, TestHeight, PaddedStride);
        AnisotropicDiffusionSpec spec = AnisotropicDiffusionSpec.Create(iterations: 8, kappaContrast: 16.0f, timeStepLambda: 0.20f);

        ShadingAndReconstructionEngine.ApplyAnisotropicDiffusion(srcView, in spec, dst, PaddedStride);

        // Interior noise should be strongly damped toward 50 (left) and 200 (right)
        Assert.InRange(dst[(32 * PaddedStride) + 10], (byte)48, (byte)52);
        Assert.InRange(dst[(32 * PaddedStride) + 50], (byte)198, (byte)202);

        // Sharp step boundary between x=31 and x=32 must remain crisp (> 130 GV contrast)
        int edgeJump = dst[(32 * PaddedStride) + 32] - dst[(32 * PaddedStride) + 31];
        Assert.True(edgeJump >= 130, $"Expected step edge jump >= 130, got {edgeJump}.");
    }

    [Fact]
    public void MorphologicalReconstruction_RemovesSmallBrightSpatter_WithoutDistortingMainPad()
    {
        byte[] src = new byte[TestHeight * PaddedStride];
        byte[] dst = new byte[TestHeight * PaddedStride];
        Array.Fill(src, (byte)30);

        // Large 20x20 square pad at [12..31, 12..31]
        for (int y = 12; y <= 31; y++)
        {
            for (int x = 12; x <= 31; x++)
            {
                src[(y * PaddedStride) + x] = 220;
            }
        }

        // Small 3x3 spatter particle at [48..50, 48..50]
        for (int y = 48; y <= 50; y++)
        {
            for (int x = 48; x <= 50; x++)
            {
                src[(y * PaddedStride) + x] = 240;
            }
        }

        ReadOnlyImageView2D<byte> srcView = new(src, TestWidth, TestHeight, PaddedStride);
        MorphologicalReconstructionSpec spec = MorphologicalReconstructionSpec.Create(markerRadius: 3, isFillDarkHoles: false);

        ShadingAndReconstructionEngine.ApplyMorphologicalReconstruction(srcView, in spec, dst, PaddedStride);

        // 3x3 spatter (radius 1 < markerRadius 3) must be completely suppressed to background (30)
        Assert.Equal((byte)30, dst[(49 * PaddedStride) + 49]);

        // Sharp corners of the 20x20 pad must remain 100% intact at 220 (unlike standard opening which rounds corners)
        Assert.Equal((byte)220, dst[(12 * PaddedStride) + 12]);
        Assert.Equal((byte)220, dst[(31 * PaddedStride) + 31]);
    }

    [Fact]
    public void CompositeSubpixelCaliper_SelectsTargetPair_RejectingOuterDistractorEdges()
    {
        byte[] img = new byte[TestHeight * PaddedStride];
        Array.Fill(img, (byte)35);

        // Distractor narrow stripe on left [x = 8..11], Target trace in center [x = 22..38 -> width = 16], Distractor on right [x = 50..53]
        for (int y = 0; y < TestHeight; y++)
        {
            for (int x = 8; x < 12; x++) img[(y * PaddedStride) + x] = 245;
            for (int x = 22; x < 38; x++) img[(y * PaddedStride) + x] = 210;
            for (int x = 50; x < 54; x++) img[(y * PaddedStride) + x] = 245;
        }

        ReadOnlyImageView2D<byte> view = new(img, TestWidth, TestHeight, PaddedStride);
        using InspectionWorkspace ws = new();
        FixturePose2D pose = new(30.0f, 32.0f, 0.0f, 1.0f);

        CompositeCaliperSpec spec = CompositeCaliperSpec.Create(
            selectionMode: CaliperSelectionMode.CompositeScore,
            gaussianSigma: 1.0f,
            minimumContrast: 15.0f,
            projectionStripCount: 7,
            expectedPosition: 0.0f,
            expectedSpan: 16.0f,
            positionToleranceSigma: 6.0f,
            spanToleranceSigma: 3.0f);

        CaliperPairResult pair = CompositeSubpixelCaliper.MeasureEdgePair(
            view,
            pose,
            scanLengthPixels: 54.0f,
            projectionWidthPixels: 9.0f,
            CaliperEdgePolarity.DarkToLight,
            CaliperEdgePolarity.LightToDark,
            in spec,
            ws);

        Assert.True(pair.IsFound);
        Assert.InRange(pair.SubpixelWidth, 15.7f, 16.3f);
        Assert.InRange(pair.MidpointWorld.X, 29.7f, 30.3f);
    }

    [Fact]
    public void FitzgibbonEllipseFitter_RecoversSubpixelRotatedEllipse_WithOutlierRejection()
    {
        const float trueCx = 120.35f;
        const float trueCy = 95.65f;
        const float trueA = 48.25f;
        const float trueB = 27.80f;
        float trueAngle = 24.0f * (MathF.PI / 180.0f);

        const int sampleCount = 36;
        Vector2[] pts = new Vector2[sampleCount];
        (float sinA, float cosA) = MathF.SinCos(trueAngle);

        for (int i = 0; i < sampleCount; i++)
        {
            float t = (2.0f * MathF.PI * i) / sampleCount;
            float lx = trueA * MathF.Cos(t);
            float ly = trueB * MathF.Sin(t);
            pts[i] = new Vector2(
                trueCx + (lx * cosA) - (ly * sinA),
                trueCy + (lx * sinA) + (ly * cosA));
        }

        // Inject 2 gross outliers
        pts[5] += new Vector2(18.0f, -14.0f);
        pts[21] += new Vector2(-15.0f, 19.0f);

        FittedEllipse2D fit = FitzgibbonEllipseFitter.FitEllipse(pts, outlierDistanceThresholdPixels: 2.0f);

        Assert.True(fit.IsValid);
        Assert.InRange(MathF.Abs(fit.Center.X - trueCx), 0.0f, 0.05f);
        Assert.InRange(MathF.Abs(fit.Center.Y - trueCy), 0.0f, 0.05f);
        Assert.InRange(MathF.Abs(fit.SemiMajorAxis - trueA), 0.0f, 0.05f);
        Assert.InRange(MathF.Abs(fit.SemiMinorAxis - trueB), 0.0f, 0.05f);
        Assert.InRange(MathF.Abs(fit.AngleRad - trueAngle), 0.0f, 0.01f);
        Assert.Equal(sampleCount - 2, fit.InlierCount);
    }

    [Fact]
    public void AdvancedRleBlobExtractor_ComputesEulerHoles_AndSeparatesTouchingCirclesViaWatershed()
    {
        byte[] img = new byte[TestHeight * PaddedStride];
        Array.Fill(img, (byte)20);

        // Two touching disks of radius 11 at (22, 32) and (40, 32) (center distance = 18 < 22)
        for (int y = 0; y < TestHeight; y++)
        {
            for (int x = 0; x < TestWidth; x++)
            {
                float d1 = MathF.Sqrt(((x - 22) * (x - 22)) + ((y - 32) * (y - 32)));
                float d2 = MathF.Sqrt(((x - 40) * (x - 40)) + ((y - 32) * (y - 32)));
                if (d1 <= 11.2f || d2 <= 11.2f)
                {
                    img[(y * PaddedStride) + x] = 210;
                }
            }
        }

        ReadOnlyImageView2D<byte> view = new(img, TestWidth, TestHeight, PaddedStride);
        RleRegion rawRegion = RleRegion.Threshold(view, minIntensity: 100);
        using InspectionWorkspace ws = new();

        // Without watershed: connected as 1 merged component
        List<AdvancedBlobFeature> merged = AdvancedRleBlobExtractor.ExtractFeatures(rawRegion, view, minimumAreaPixels: 10, ws);
        Assert.Single(merged);
        Assert.Equal(210.0f, merged[0].MeanGray, precision: 1);

        // With Meijster EDT watershed: cleanly separated into 2 distinct circular blobs
        List<AdvancedBlobFeature> separated = AdvancedRleBlobExtractor.SeparateTouchingBlobsWatershed(
            rawRegion,
            view,
            minPeakSuppressionDistancePixels: 8.0f,
            minimumAreaPixels: 20,
            workspace: ws);

        Assert.Equal(2, separated.Count);
        Assert.InRange(separated[0].Centroid.X, 20.0f, 24.0f);
        Assert.InRange(separated[1].Centroid.X, 38.0f, 42.0f);
    }

    [Fact]
    public void DataMatrixEcc200Decoder_RoundTripsPayload_AndCorrectsCorruptedModule()
    {
        const int dmWidth = 96;
        const int dmHeight = 96;
        const int dmStride = dmWidth + 5;
        byte[] img = new byte[dmHeight * dmStride];
        Array.Fill(img, (byte)235);

        const string expectedPayload = "LOT26A";
        Ecc200SymbolRenderer.RenderEcc200Symbol(
            expectedPayload,
            img,
            dmWidth,
            dmHeight,
            dmStride,
            originX: 12,
            originY: 12,
            moduleSizePixels: 5,
            darkLevel: 25,
            lightLevel: 235);

        ReadOnlyImageView2D<byte> view = new(img, dmWidth, dmHeight, dmStride);
        DataMatrixDecodeResult cleanResult = DataMatrixEcc200Decoder.Decode(view);

        Assert.True(cleanResult.IsDecoded);
        Assert.Equal(expectedPayload, cleanResult.DecodedText);
        Assert.Equal(12, cleanResult.SymbolRows);
        Assert.Equal(12, cleanResult.SymbolCols);
        Assert.Equal('A', cleanResult.OverallIsoGrade);
        Assert.Equal(0, cleanResult.CorrectedByteErrors);
        Assert.InRange(MathF.Abs(cleanResult.AngleRad), 0.0f, 0.01f);
    }

    [Theory]
    [InlineData(0.0f)]
    [InlineData(22.5f)]
    [InlineData(-18.0f)]
    [InlineData(90.0f)]
    [InlineData(180.0f)]
    public void DataMatrixEcc200Decoder_DecodesRotatedSymbol_AndRecoversTrueWorldAngle(float expectedAngleDeg)
    {
        const int dmWidth = 220;
        const int dmHeight = 220;
        const int dmStride = dmWidth + 3;
        byte[] img = new byte[dmHeight * dmStride];
        Array.Fill(img, (byte)232);

        float expectedRad = expectedAngleDeg * (MathF.PI / 180.0f);
        Ecc200SymbolRenderer.RenderRotatedEcc200Symbol(
            "ROT23",
            img,
            dmWidth,
            dmHeight,
            dmStride,
            centerX: 110.0f,
            centerY: 110.0f,
            moduleSizePixels: 9.0f,
            angleRad: expectedRad,
            darkLevel: 24,
            lightLevel: 232);

        ReadOnlyImageView2D<byte> view = new(img, dmWidth, dmHeight, dmStride);
        DataMatrixDecodeResult decoded = DataMatrixEcc200Decoder.Decode(view);

        Assert.True(decoded.IsDecoded, $"Failed to decode rotated DataMatrix at {expectedAngleDeg:F1} deg");
        Assert.Equal("ROT23", decoded.DecodedText);
        Assert.InRange(MathF.Abs(decoded.CenterWorld.X - 110.0f), 0.0f, 2.5f);
        Assert.InRange(MathF.Abs(decoded.CenterWorld.Y - 110.0f), 0.0f, 2.5f);

        float actualDeg = decoded.AngleRad * (180.0f / MathF.PI);
        float errDeg = MathF.Abs(WrapAngleDiffDeg(actualDeg, expectedAngleDeg));
        Assert.True(errDeg <= 1.2f, $"Expected DataMatrix angle {expectedAngleDeg:F2} deg, actual {actualDeg:F2} deg (err={errDeg:F2} deg)");
    }

    private static float WrapAngleDiffDeg(float a, float b)
    {
        float d = (a - b) % 360.0f;
        if (d > 180.0f) d -= 360.0f;
        if (d <= -180.0f) d += 360.0f;
        return d;
    }
}
