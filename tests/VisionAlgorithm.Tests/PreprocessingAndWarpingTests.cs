// File: tests/VisionAlgorithm.Tests/PreprocessingAndWarpingTests.cs
using VisionAlgorithm.OpenSrc.EdgeFewShot.Preprocessing;
using VisionAlgorithm.OpenSrc.Preprocessing;
using Xunit;

namespace VisionAlgorithm.Tests;

public sealed class PreprocessingAndWarpingTests
{
    [Fact]
    public void GuidedFilter_And_BilateralFilter_SmoothFlatNoiseWhilePreservingStepEdge()
    {
        const int width = 64;
        const int height = 64;
        var src = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int baseVal = x < 32 ? 50 : 200;
                int jitter = ((x + y) & 1) == 0 ? 6 : -6;
                src[(y * width) + x] = (byte)Math.Clamp(baseVal + jitter, 0, 255);
            }
        }

        var guidedDst = new byte[width * height];
        EdgePreservingFilterEngine.ApplyGuidedFilter(src, width, height, width, new GuidedFilterSpec(radius: 3, regularizationEpsilon: 100.0f), guidedDst, width);

        var bilateralDst = new byte[width * height];
        EdgePreservingFilterEngine.ApplyBilateralFilter(src, width, height, width, new BilateralFilterSpec(radius: 3, spatialSigma: 2.5f, rangeSigma: 25.0f), bilateralDst, width);

        // Flat regions away from the x=32 boundary should be smoothed close to 50 and 200
        Assert.InRange(guidedDst[(32 * width) + 10], (byte)48, (byte)52);
        Assert.InRange(guidedDst[(32 * width) + 50], (byte)198, (byte)202);
        Assert.InRange(bilateralDst[(32 * width) + 10], (byte)48, (byte)52);
        Assert.InRange(bilateralDst[(32 * width) + 50], (byte)198, (byte)202);

        // Edge jump across x=30 -> x=33 should remain sharp (> 110 gray levels)
        int guidedEdgeJump = guidedDst[(32 * width) + 33] - guidedDst[(32 * width) + 30];
        int bilateralEdgeJump = bilateralDst[(32 * width) + 33] - bilateralDst[(32 * width) + 30];
        Assert.True(guidedEdgeJump > 110, $"Expected guided filter to preserve step edge, got jump={guidedEdgeJump}");
        Assert.True(bilateralEdgeJump > 120, $"Expected bilateral filter to preserve step edge, got jump={bilateralEdgeJump}");
    }

    [Fact]
    public void MedianFilter_RemovesSaltAndPepperImpulseNoise()
    {
        const int width = 32;
        const int height = 32;
        var src = new byte[width * height];
        Array.Fill(src, (byte)120);

        // Inject isolated salt (255) and pepper (0) spikes
        src[(10 * width) + 10] = 255;
        src[(20 * width) + 20] = 0;

        var dstRadius1 = new byte[width * height];
        EdgePreservingFilterEngine.ApplyMedianFilter(src, width, height, width, radius: 1, dstRadius1, width);

        var dstRadius2 = new byte[width * height];
        EdgePreservingFilterEngine.ApplyMedianFilter(src, width, height, width, radius: 2, dstRadius2, width);

        Assert.Equal((byte)120, dstRadius1[(10 * width) + 10]);
        Assert.Equal((byte)120, dstRadius1[(20 * width) + 20]);
        Assert.Equal((byte)120, dstRadius2[(10 * width) + 10]);
        Assert.Equal((byte)120, dstRadius2[(20 * width) + 20]);
    }

    [Fact]
    public void Clahe_And_AdaptiveThreshold_EnhanceContrastAndBinarizeUnderIlluminationGradient()
    {
        const int width = 64;
        const int height = 64;
        var src = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                // Narrow dynamic range [95..145] with illumination gradient + local dark engraved mark
                int bg = 115 + ((x + y) / 3);
                bool isDarkMark = (x >= 24 && x <= 40 && y >= 24 && y <= 40);
                src[(y * width) + x] = (byte)Math.Clamp(isDarkMark ? bg - 40 : bg, 0, 255);
            }
        }

        var claheDst = new byte[width * height];
        ContrastAndAdaptiveFilterEngine.ApplyClahe(src, width, height, width, new ClaheFilterSpec(tileGridX: 4, tileGridY: 4, clipLimit: 2.5f), claheDst, width);

        int srcMin = src.Min(b => (int)b);
        int srcMax = src.Max(b => (int)b);
        int claheMin = claheDst.Min(b => (int)b);
        int claheMax = claheDst.Max(b => (int)b);
        Assert.True((claheMax - claheMin) > (srcMax - srcMin));

        var adaptiveDst = new byte[width * height];
        ContrastAndAdaptiveFilterEngine.ApplyAdaptiveThreshold(
            src,
            width,
            height,
            width,
            new AdaptiveThresholdSpec(windowRadius: 12, offsetConstant: 6.0f, sauvolaSensitivityK: 0.08f, useSauvolaStdWeighting: true),
            adaptiveDst,
            width);

        // Dark mark boundary (25,25) falls below local Sauvola threshold -> 0; illuminated background (8,8) -> 255
        Assert.Equal((byte)0, adaptiveDst[(25 * width) + 25]);
        Assert.Equal((byte)255, adaptiveDst[(8 * width) + 8]);
    }

    [Fact]
    public void UnsharpMask_SobelGradient_And_ZScoreNormalization_ProduceExpectedOutputs()
    {
        const int width = 32;
        const int height = 32;
        var src = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                src[(y * width) + x] = (byte)(x < 16 ? 60 : 180);
            }
        }

        var unsharpDst = new byte[width * height];
        ContrastAndAdaptiveFilterEngine.ApplyUnsharpMask(src, width, height, width, new UnsharpSobelSpec(radius: 2, sigma: 1.2f, amount: 1.5f), unsharpDst, width);
        // Unsharp mask overshoots near the step edge (x=15 dark side < 60, x=16 bright side > 180)
        Assert.True(unsharpDst[(16 * width) + 15] < 60);
        Assert.True(unsharpDst[(16 * width) + 16] > 180);

        var sobelDst = new byte[width * height];
        ContrastAndAdaptiveFilterEngine.ApplySobelGradient(src, width, height, width, scaleFactor: 1.0f, sobelDst, width);
        Assert.True(sobelDst[(16 * width) + 16] > 100);
        Assert.Equal((byte)0, sobelDst[(16 * width) + 4]);

        var floatSamples = new float[width * height];
        for (int i = 0; i < src.Length; i++)
        {
            floatSamples[i] = src[i];
        }

        var normalizer = new IlluminationNormalizer();
        normalizer.NormalizeInPlace(floatSamples);
        var (mean, stdDev) = ContrastAndAdaptiveFilterEngine.ComputeMeanAndStdDev(floatSamples);
        Assert.InRange(mean, -1e-4f, 1e-4f);
        Assert.InRange(stdDev, 0.999f, 1.001f);
    }

    [Fact]
    public void ImageWarpingEngine_SupportsAffine_Perspective_QuadToRect_PolarUnwrap_And_LensUndistort()
    {
        const int width = 64;
        const int height = 64;
        var src = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double dx = x - 31.5;
                double dy = y - 31.5;
                double r = Math.Sqrt((dx * dx) + (dy * dy));
                src[(y * width) + x] = (r >= 14.0 && r <= 20.0) ? (byte)240 : (byte)20;
            }
        }

        // 1. Identity Affine Warp preserves image
        var affineDst = new byte[width * height];
        ImageWarpingEngine.ApplyWarp(
            src,
            width,
            height,
            width,
            new ImageWarpSpec(WarpTransformKind.Affine, angleDegrees: 0f, scaleX: 1f, scaleY: 1f),
            affineDst,
            width,
            height,
            width);
        Assert.Equal(src[(32 * width) + 16], affineDst[(32 * width) + 16]);

        // 2. Polar Unwrap maps concentric ring (r in [14..20]) into horizontal band across all angles (columns)
        var polarDst = new byte[width * height];
        ImageWarpingEngine.ApplyWarp(
            src,
            width,
            height,
            width,
            new ImageWarpSpec(WarpTransformKind.PolarAnnularUnwrap, polarInnerRadiusRatio: 0.0f),
            polarDst,
            width,
            height,
            width);
        int ringRow = 34; // ~17px radius out of maxRadius ~32px -> row ~34
        int brightColsInRingRow = 0;
        for (int x = 0; x < width; x++)
        {
            if (polarDst[(ringRow * width) + x] > 180)
            {
                brightColsInRingRow++;
            }
        }
        Assert.True(brightColsInRingRow >= width - 4, $"Expected polar unwrap ring to form horizontal stripe, got {brightColsInRingRow}/{width} bright cols.");

        // 3. Perspective Keystone & QuadToRect execute cleanly without out-of-bounds
        var keystoneDst = new byte[width * height];
        ImageWarpingEngine.ApplyWarp(
            src,
            width,
            height,
            width,
            new ImageWarpSpec(WarpTransformKind.PerspectiveKeystone, keystoneX: 0.15f, keystoneY: 0.05f),
            keystoneDst,
            width,
            height,
            width);
        Assert.Contains(keystoneDst, b => b > 200);

        var quadDst = new byte[width * height];
        ReadOnlySpan<System.Numerics.Vector2> corners =
        [
            new(4f, 4f),
            new(59f, 4f),
            new(59f, 59f),
            new(4f, 59f)
        ];
        ImageWarpingEngine.WarpQuadToRect(
            src,
            width,
            height,
            width,
            corners,
            quadDst,
            width,
            height,
            width);
        Assert.Contains(quadDst, b => b > 200);

        // 4. Lens Undistort
        var undistortDst = new byte[width * height];
        ImageWarpingEngine.ApplyWarp(
            src,
            width,
            height,
            width,
            new ImageWarpSpec(WarpTransformKind.BrownConradyUndistort, radialK1: -0.15f, radialK2: 0.02f),
            undistortDst,
            width,
            height,
            width);
        Assert.Contains(undistortDst, b => b > 200);
    }
}
