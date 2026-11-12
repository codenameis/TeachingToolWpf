using VisionAlgorithm.OpenSrc.DataCode2D;
using VisionAlgorithm.OpenSrc.Metrology;
using VisionAlgorithm.OpenSrc.PhotometricStereo;
using VisionAlgorithm.OpenSrc.Preprocessing;
using Xunit;

namespace VisionAlgorithm.Tests;

public sealed class HalconCognexExpansionAlgorithmsTests
{
    [Fact]
    public void FrequencyAndTextureFilterEngine_FftNotch_SuppressesPeriodicMoireInterference()
    {
        const int width = 64;
        const int height = 64;
        byte[] src = new byte[width * height];
        byte[] dst = new byte[width * height];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double moire = 55.0 * Math.Cos(2.0 * Math.PI * x / 8.0) * Math.Cos(2.0 * Math.PI * y / 8.0);
                src[(y * width) + x] = (byte)Math.Clamp((int)Math.Round(128.0 + moire), 0, 255);
            }
        }

        RoiHistogramProfileSummary before = FrequencyAndTextureFilterEngine.ComputeRoiHistogramAndProfile(src, width, height, width);
        int attenuatedPeaks = FrequencyAndTextureFilterEngine.ApplyFftPeriodicNotchFilter(
            src,
            width,
            height,
            width,
            dst,
            minFrequencyRadius: 3,
            notchSigma: 2.5,
            peakThresholdRatio: 3.5);
        RoiHistogramProfileSummary after = FrequencyAndTextureFilterEngine.ComputeRoiHistogramAndProfile(dst, width, height, width);

        Assert.True(attenuatedPeaks > 0, $"Expected attenuated periodic FFT peaks > 0, actual: {attenuatedPeaks}");
        Assert.True(after.StdDevIntensity < before.StdDevIntensity * 0.65, $"Expected StdDev reduction from {before.StdDevIntensity:F2} to {after.StdDevIntensity:F2}");
    }

    [Fact]
    public void FrequencyAndTextureFilterEngine_HomomorphicAndGabor_EnhanceReflectanceAndDirectionalScratch()
    {
        const int width = 64;
        const int height = 64;
        byte[] src = new byte[width * height];
        byte[] homoDst = new byte[width * height];
        byte[] gaborDst = new byte[width * height];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double illum = 0.30 + (1.20 * (x / (double)width));
                double scratch = Math.Abs((x - y) - 4) <= 1 ? 75.0 : 0.0;
                src[(y * width) + x] = (byte)Math.Clamp((int)Math.Round((90.0 + scratch) * illum), 5, 250);
            }
        }

        FrequencyAndTextureFilterEngine.ApplyHomomorphicFilter(
            src,
            width,
            height,
            width,
            homoDst,
            gammaLow: 0.30,
            gammaHigh: 1.85,
            sigma: 10.0);

        FrequencyAndTextureFilterEngine.ApplyGaborTextureFilter(
            homoDst,
            width,
            height,
            width,
            gaborDst,
            wavelength: 6.0,
            orientationDeg: 135.0,
            sigma: 2.8,
            aspectRatio: 0.6);

        RoiHistogramProfileSummary gaborStats = FrequencyAndTextureFilterEngine.ComputeRoiHistogramAndProfile(gaborDst, width, height, width);
        Assert.True(gaborStats.MaxIntensity > 100);
        Assert.InRange(gaborStats.OtsuThreshold, (byte)1, (byte)254);
    }

    [Fact]
    public void Barcode1DDecoder_Code128B_DecodesMultiScanlineAndComputesIso15416QualityGrade()
    {
        const int width = 420;
        const int height = 120;
        byte[] canvas = new byte[width * height];
        Array.Fill(canvas, (byte)238);

        Code128SymbolRenderer.RenderCode128Symbol(
            canvas,
            width,
            height,
            width,
            payload: "LOT26A",
            barLeftX: 40,
            barTopY: 20,
            barHeight: 80,
            moduleWidthPx: 3,
            backgroundIntensity: 238,
            barIntensity: 20);

        Barcode1DDecodeResult result = Barcode1DDecoder.DecodeMultiScanline(canvas, width, height, width, scanlineCount: 9);

        Assert.True(result.IsDecoded);
        Assert.True(result.IsChecksumValid);
        Assert.Equal("LOT26A", result.Payload);
        Assert.Equal("Code 128B", result.Symbology);
        Assert.True(result.OverallGradeScore >= 3.0);
        Assert.StartsWith("A", result.IsoGradeLetter, StringComparison.Ordinal);
        Assert.InRange(Math.Abs(result.AngleDeg), 0.0, 0.25);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-18.0)]
    [InlineData(22.5)]
    [InlineData(90.0)]
    [InlineData(180.0)]
    public void Barcode1DDecoder_Code128B_MeasuresTrueRotationAngleAcrossQuadrants(double expectedAngleDeg)
    {
        const int width = 380;
        const int height = 380;
        byte[] canvas = new byte[width * height];
        Array.Fill(canvas, (byte)238);

        float expectedRad = (float)(expectedAngleDeg * (Math.PI / 180.0));
        Code128SymbolRenderer.RenderRotatedCode128Symbol(
            canvas,
            width,
            height,
            width,
            payload: "ANG18",
            centerX: 190.0f,
            centerY: 190.0f,
            barHeight: 112,
            moduleWidthPx: 3.0f,
            angleRad: expectedRad,
            backgroundIntensity: 238,
            barIntensity: 20);

        Barcode1DDecodeResult result = Barcode1DDecoder.DecodeMultiScanline(canvas, width, height, width, scanlineCount: 9);

        Assert.True(result.IsDecoded, $"Failed to decode rotated 1D barcode at {expectedAngleDeg:F1} deg");
        Assert.True(result.IsChecksumValid);
        Assert.Equal("ANG18", result.Payload);
        Assert.InRange(Math.Abs(result.CenterX - 190.0), 0.0, 3.0);
        Assert.InRange(Math.Abs(result.CenterY - 190.0), 0.0, 3.0);

        double angleErrorDeg = Math.Abs(WrapAngleDiffDegrees(result.AngleDeg, expectedAngleDeg));
        Assert.True(angleErrorDeg <= 1.2, $"Expected 1D barcode angle {expectedAngleDeg:F2} deg, actual {result.AngleDeg:F2} deg (err={angleErrorDeg:F2} deg)");
    }

    private static double WrapAngleDiffDegrees(double a, double b)
    {
        double d = (a - b) % 360.0;
        if (d > 180.0) d -= 360.0;
        if (d <= -180.0) d += 360.0;
        return d;
    }

    [Fact]
    public void PhotometricStereoEngine_FourQuadrantLambertian_Separates2DStainFrom3DDentCurvature()
    {
        const int quadSize = 80;
        const int width = quadSize * 2;
        const int height = quadSize * 2;
        byte[] stainOnly = new byte[width * height];
        byte[] with3dDent = new byte[width * height];

        RenderSyntheticPhotometricQuadrants(stainOnly, width, height, quadSize, inject3dDent: false);
        RenderSyntheticPhotometricQuadrants(with3dDent, width, height, quadSize, inject3dDent: true);

        PhotometricStereoResult okResult = PhotometricStereoEngine.AnalyzeCompositeOrSingleFrame(stainOnly, width, height, width, slantAngleDeg: 45.0, curvatureThreshold: 0.045);
        PhotometricStereoResult ngResult = PhotometricStereoEngine.AnalyzeCompositeOrSingleFrame(with3dDent, width, height, width, slantAngleDeg: 45.0, curvatureThreshold: 0.045);

        Assert.Equal(0, okResult.DefectPixelCount);
        Assert.True(ngResult.DefectPixelCount > 10, $"Expected 3D dent defect pixels > 10, actual: {ngResult.DefectPixelCount}");
        Assert.True(ngResult.MaxAbsMeanCurvature > okResult.MaxAbsMeanCurvature * 3.0);
    }

    [Fact]
    public void GeometricToleranceAnalyzer_EvaluatesDatumOrthogonalityAndConcentricity()
    {
        const int width = 240;
        const int height = 240;
        byte[] okPart = new byte[width * height];
        Array.Fill(okPart, (byte)36);

        for (int y = 40; y <= 200; y++)
        {
            for (int x = 40; x <= 200; x++)
            {
                okPart[(y * width) + x] = 215;
            }
        }

        double boreCx = width * 0.56;
        double boreCy = height * 0.56;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double r2 = ((x - boreCx) * (x - boreCx)) + ((y - boreCy) * (y - boreCy));
                if (r2 <= 52.0 * 52.0)
                {
                    okPart[(y * width) + x] = 45;
                }
                if (r2 <= 24.0 * 24.0)
                {
                    okPart[(y * width) + x] = 215;
                }
            }
        }

        GeometricToleranceResult result = GeometricToleranceAnalyzer.AnalyzeGeometricTolerances(
            okPart,
            width,
            height,
            width,
            nominalAngleDeg: 90.0,
            angleToleranceDeg: 1.5,
            nominalCenterDistancePx: 0.0,
            distanceTolerancePx: 3.5,
            concentricityLimitPx: 2.0,
            gradientThreshold: 18.0);

        Assert.True(result.IsMeasured);
        Assert.True(result.AngleErrorDeg < 1.5, $"Angle error: {result.AngleErrorDeg:F3} deg");
        Assert.True(result.CircleConcentricityOffsetPx < 1.5, $"Concentricity offset: {result.CircleConcentricityOffsetPx:F3} px");
    }

    private static void RenderSyntheticPhotometricQuadrants(byte[] canvas, int width, int height, int quadSize, bool inject3dDent)
    {
        const float sinSlant = 0.70710678f;
        const float cosSlant = 0.70710678f;

        for (int qy = 0; qy < quadSize; qy++)
        {
            for (int qx = 0; qx < quadSize; qx++)
            {
                float dx = qx - (quadSize * 0.5f);
                float dy = qy - (quadSize * 0.5f);
                float albedo = (((dx + 14f) * (dx + 14f)) + ((dy - 12f) * (dy - 12f))) < 100f ? 110f : 195f;

                float dzdx = 0f;
                float dzdy = 0f;
                if (inject3dDent)
                {
                    float r2 = ((dx - 8f) * (dx - 8f)) + ((dy + 6f) * (dy + 6f));
                    float sigmaSq = 36.0f;
                    float depth = -5.5f * MathF.Exp(-r2 / (2f * sigmaSq));
                    dzdx = -depth * (dx - 8f) / sigmaSq;
                    dzdy = -depth * (dy + 6f) / sigmaSq;
                }

                float invNorm = 1.0f / MathF.Sqrt((dzdx * dzdx) + (dzdy * dzdy) + 1.0f);
                float nx = -dzdx * invNorm;
                float ny = -dzdy * invNorm;
                float nz = invNorm;

                canvas[(qy * width) + qx] = (byte)Math.Clamp((int)MathF.Round(albedo * MathF.Max(0f, (nx * sinSlant) + (nz * cosSlant))), 0, 255);
                canvas[(qy * width) + quadSize + qx] = (byte)Math.Clamp((int)MathF.Round(albedo * MathF.Max(0f, (-ny * sinSlant) + (nz * cosSlant))), 0, 255);
                canvas[((quadSize + qy) * width) + qx] = (byte)Math.Clamp((int)MathF.Round(albedo * MathF.Max(0f, (-nx * sinSlant) + (nz * cosSlant))), 0, 255);
                canvas[((quadSize + qy) * width) + quadSize + qx] = (byte)Math.Clamp((int)MathF.Round(albedo * MathF.Max(0f, (ny * sinSlant) + (nz * cosSlant))), 0, 255);
            }
        }
    }
}
