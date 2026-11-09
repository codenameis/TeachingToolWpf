using System;
using TeachingToolWpf.Fluent2.Common;
using TeachingToolWpf.Fluent2.TeachingReadiness;
using Xunit;

namespace TeachingToolWpf.Fluent2.Tests;

public sealed class SubPixelTeachingReadinessTests
{
    private const byte PaddingSentinelValue = 250;
    private const byte LowIntensity = 20;
    private const byte HighIntensity = 230;
    private const int CheckerPeriodPixels = 4;

    [Fact]
    public void Analyze_DefaultStructInputs_ReturnsFailureWithoutThrowing()
    {
        SubPixelTeachingReadinessEngine engine = new SubPixelTeachingReadinessEngine();
        byte[] buffer = new byte[64 * 64];
        GrayImageView2D validImage = GrayImageView2D.Create(buffer, 64, 64, 64);
        RoiPixelBounds validRoi = RoiPixelBounds.Create(4, 4, 32, 32);
        TeachingReadinessSpec validSpec = TeachingReadinessSpec.Create(validRoi);

        TeachingResult<TeachingReadinessReport> defaultImageResult = engine.Analyze(
            default,
            in validSpec);
        Assert.False(defaultImageResult.IsSuccess);

        TeachingReadinessSpec defaultSpec = default;
        TeachingResult<TeachingReadinessReport> defaultSpecResult = engine.Analyze(
            validImage,
            in defaultSpec);
        Assert.False(defaultSpecResult.IsSuccess);

        RoiPixelBounds defaultRoi = default;
        Assert.Throws<ArgumentException>(() => TeachingReadinessSpec.Create(defaultRoi));
    }

    [Theory]
    [InlineData(67, 100, 32)]
    [InlineData(99, 128, 120)]
    [InlineData(131, 160, 200)]
    public void Analyze_UniformFlatRegionWithPaddedRowStride_ReturnsInsufficientContrast(
        int width,
        int rowStride,
        byte uniformIntensity)
    {
        int height = 64;
        byte[] buffer = CreatePaddedBuffer(width, height, rowStride, PaddingSentinelValue);

        for (int y = 0; y < height; y++)
        {
            int rowStart = y * rowStride;
            for (int x = 0; x < width; x++)
            {
                buffer[rowStart + x] = uniformIntensity;
            }
        }

        GrayImageView2D image = GrayImageView2D.Create(buffer, width, height, rowStride);
        RoiPixelBounds roi = RoiPixelBounds.Create(2, 2, width - 4, height - 4);
        TeachingReadinessSpec spec = TeachingReadinessSpec.Create(roi);
        SubPixelTeachingReadinessEngine engine = new SubPixelTeachingReadinessEngine();

        TeachingResult<TeachingReadinessReport> result = engine.Analyze(image, in spec);

        Assert.True(result.IsSuccess);
        Assert.Equal(TeachingReadinessGrade.InsufficientContrast, result.Value.Grade);
        Assert.Equal(0.0f, result.Value.RmsGradientEnergy, 3);
        Assert.Equal(0.0f, result.Value.ContrastSpan, 3);
        Assert.Equal(0.0f, result.Value.StructureIsotropy, 3);
    }

    [Theory]
    [InlineData(true, 67, 96, 0.0f)]
    [InlineData(false, 75, 112, 90.0f)]
    public void Analyze_OneDimensionalStripePattern_DetectsApertureAmbiguityAndOrientation(
        bool verticalBars,
        int width,
        int rowStride,
        float expectedAngleDegrees)
    {
        int height = 64;
        byte[] buffer = CreatePaddedBuffer(width, height, rowStride, PaddingSentinelValue);

        for (int y = 0; y < height; y++)
        {
            int rowStart = y * rowStride;
            for (int x = 0; x < width; x++)
            {
                int coordinate = verticalBars ? x : y;
                bool isHigh = ((coordinate / CheckerPeriodPixels) & 1) == 0;
                buffer[rowStart + x] = isHigh ? HighIntensity : LowIntensity;
            }
        }

        GrayImageView2D image = GrayImageView2D.Create(buffer, width, height, rowStride);
        RoiPixelBounds roi = RoiPixelBounds.Create(4, 4, width - 8, height - 8);
        TeachingReadinessSpec spec = TeachingReadinessSpec.Create(roi);
        SubPixelTeachingReadinessEngine engine = new SubPixelTeachingReadinessEngine();

        TeachingResult<TeachingReadinessReport> result = engine.Analyze(image, in spec);

        Assert.True(result.IsSuccess);
        Assert.Equal(TeachingReadinessGrade.ApertureAmbiguous, result.Value.Grade);
        Assert.True(result.Value.StructureIsotropy < 0.05f);
        Assert.True(result.Value.ContrastSpan >= 200.0f);
        Assert.InRange(MathF.Abs(result.Value.DominantOrientationDegrees - expectedAngleDegrees), 0.0f, 1.5f);
    }

    [Theory]
    [InlineData(67, 100, 52)]
    [InlineData(131, 160, 64)]
    public void Analyze_TwoDimensionalCheckerboardWithNonAlignedStride_ReturnsOptimalSubPixelGrade(
        int width,
        int rowStride,
        int height)
    {
        byte[] buffer = CreatePaddedBuffer(width, height, rowStride, PaddingSentinelValue);

        for (int y = 0; y < height; y++)
        {
            int rowStart = y * rowStride;
            int cellY = y / CheckerPeriodPixels;
            for (int x = 0; x < width; x++)
            {
                int cellX = x / CheckerPeriodPixels;
                bool isHigh = ((cellX ^ cellY) & 1) == 0;
                buffer[rowStart + x] = isHigh ? HighIntensity : LowIntensity;
            }
        }

        GrayImageView2D image = GrayImageView2D.Create(buffer, width, height, rowStride);
        RoiPixelBounds roi = RoiPixelBounds.Create(4, 4, width - 8, height - 8);
        TeachingReadinessSpec spec = TeachingReadinessSpec.Create(roi);
        SubPixelTeachingReadinessEngine engine = new SubPixelTeachingReadinessEngine();

        TeachingResult<TeachingReadinessReport> result = engine.Analyze(image, in spec);

        Assert.True(result.IsSuccess);
        Assert.Equal(TeachingReadinessGrade.OptimalSubPixel, result.Value.Grade);
        Assert.True(result.Value.StructureIsotropy > 0.85f);
        Assert.True(result.Value.CompositeScore >= 0.72f);
        Assert.True(result.Value.ContrastSpan >= 200.0f);
    }

    private static byte[] CreatePaddedBuffer(int width, int height, int rowStride, byte paddingSentinel)
    {
        Assert.True(rowStride >= width);
        byte[] buffer = new byte[height * rowStride];
        Array.Fill(buffer, paddingSentinel);
        return buffer;
    }
}
