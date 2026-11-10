using System.Buffers;
using System.Collections.Immutable;
using VisionAlgorithm.OpenSrc.Preprocessing;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// Executes the ordered pre-inspection preprocessing filter pipeline on grayscale image buffers
/// before core vision inspection or training algorithms run ("미리 필터가 들어가 있는 경우는 필터를 먼저 하고 본검사가 들어가야됨").
/// When the pipeline is empty or all steps are unchecked, zero work is performed.
/// </summary>
public static class PreInspectionFilterPipeline
{
    private const byte MaxByteValue = 255;
    private static ImmutableArray<PreInspectionFilterStep> _activeSteps = [];
    private static long _totalExecutions;
    private static double _lastElapsedMilliseconds;

    public static ImmutableArray<PreInspectionFilterStep> ActiveSteps => _activeSteps;

    public static bool HasEnabledSteps
    {
        get
        {
            var steps = _activeSteps;
            if (steps.IsDefaultOrEmpty)
            {
                return false;
            }

            foreach (var s in steps)
            {
                if (s.IsEnabled && s.Kind != PreInspectionFilterKind.None)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public static long TotalExecutions => Interlocked.Read(ref _totalExecutions);

    public static double LastElapsedMilliseconds => Volatile.Read(ref _lastElapsedMilliseconds);

    public static void SetActivePipeline(ImmutableArray<PreInspectionFilterStep> steps)
    {
        _activeSteps = steps.IsDefault ? [] : steps;
    }

    public static void ClearActivePipeline()
    {
        _activeSteps = [];
    }

    /// <summary>
    /// Applies all enabled filter steps in <see cref="ActiveSteps"/> sequentially in-place to <paramref name="pixels"/>.
    /// Returns the number of filter steps executed.
    /// </summary>
    public static int ApplyInPlaceIfConfigured(Span<byte> pixels, int width, int height)
    {
        var steps = _activeSteps;
        if (steps.IsDefaultOrEmpty || width <= 2 || height <= 2)
        {
            return 0;
        }

        var pixelCount = width * height;
        if (pixels.Length < pixelCount)
        {
            return 0;
        }

        var enabledCount = 0;
        for (var i = 0; i < steps.Length; i++)
        {
            if (steps[i].IsEnabled && steps[i].Kind != PreInspectionFilterKind.None)
            {
                enabledCount++;
            }
        }

        if (enabledCount == 0)
        {
            return 0;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var scratchPool = ArrayPool<byte>.Shared.Rent(pixelCount);
        try
        {
            var scratch = scratchPool.AsSpan(0, pixelCount);
            var srcSpan = pixels.Slice(0, pixelCount);
            var applied = 0;

            for (var i = 0; i < steps.Length; i++)
            {
                var step = steps[i];
                if (!step.IsEnabled || step.Kind == PreInspectionFilterKind.None)
                {
                    continue;
                }

                ApplySingleStep(srcSpan, scratch, width, height, step);
                scratch.CopyTo(srcSpan);
                applied++;
            }

            sw.Stop();
            Volatile.Write(ref _lastElapsedMilliseconds, sw.Elapsed.TotalMilliseconds);
            Interlocked.Increment(ref _totalExecutions);
            return applied;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratchPool);
        }
    }

    private static void ApplySingleStep(
        ReadOnlySpan<byte> src,
        Span<byte> dst,
        int width,
        int height,
        PreInspectionFilterStep step)
    {
        var r = Math.Clamp(step.Radius, 1, 15);
        switch (step.Kind)
        {
            case PreInspectionFilterKind.Binarize:
            case PreInspectionFilterKind.NativeThreshold:
            {
                var t = (byte)Math.Clamp((int)Math.Round(step.Threshold), 0, MaxByteValue);
                var len = width * height;
                for (var i = 0; i < len; i++)
                {
                    dst[i] = src[i] >= t ? MaxByteValue : (byte)0;
                }

                break;
            }
            case PreInspectionFilterKind.Edge:
            case PreInspectionFilterKind.SobelGradient:
            {
                var gain = Math.Clamp((float)step.Sigma, 0.2f, 8.0f);
                ContrastAndAdaptiveFilterEngine.ApplySobelGradient(src, width, height, width, gain, dst, width);
                break;
            }
            case PreInspectionFilterKind.Gaussian:
            {
                ApplyBoxBlurSeparable(src, dst, width, height, r);
                break;
            }
            case PreInspectionFilterKind.Erosion:
            {
                ApplyMorphMinMax(src, dst, width, height, r, isDilate: false);
                break;
            }
            case PreInspectionFilterKind.Dilation:
            {
                ApplyMorphMinMax(src, dst, width, height, r, isDilate: true);
                break;
            }
            case PreInspectionFilterKind.Opening:
            {
                var tempPool = ArrayPool<byte>.Shared.Rent(width * height);
                try
                {
                    var temp = tempPool.AsSpan(0, width * height);
                    ApplyMorphMinMax(src, temp, width, height, r, isDilate: false);
                    ApplyMorphMinMax(temp, dst, width, height, r, isDilate: true);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(tempPool);
                }

                break;
            }
            case PreInspectionFilterKind.Closing:
            {
                var tempPool = ArrayPool<byte>.Shared.Rent(width * height);
                try
                {
                    var temp = tempPool.AsSpan(0, width * height);
                    ApplyMorphMinMax(src, temp, width, height, r, isDilate: true);
                    ApplyMorphMinMax(temp, dst, width, height, r, isDilate: false);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(tempPool);
                }

                break;
            }
            case PreInspectionFilterKind.WhiteTopHat:
            case PreInspectionFilterKind.BlackHat:
            case PreInspectionFilterKind.GaussianBlackHatThreshold:
            {
                var tempPool = ArrayPool<byte>.Shared.Rent(width * height);
                try
                {
                    var temp = tempPool.AsSpan(0, width * height);
                    ApplyBoxBlurSeparable(src, temp, width, height, r);
                    var len = width * height;
                    for (var i = 0; i < len; i++)
                    {
                        var diff = step.Kind == PreInspectionFilterKind.WhiteTopHat
                            ? Math.Max(0, src[i] - temp[i])
                            : Math.Max(0, temp[i] - src[i]);
                        dst[i] = (byte)Math.Clamp(diff * 2, 0, MaxByteValue);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(tempPool);
                }

                break;
            }
            case PreInspectionFilterKind.Clahe:
            {
                var tiles = Math.Clamp(r * 2, 2, 16);
                var clipLimit = Math.Clamp((float)step.Sigma, 1.0f, 16.0f);
                ContrastAndAdaptiveFilterEngine.ApplyClahe(src, width, height, width, new ClaheFilterSpec(tiles, tiles, clipLimit), dst, width);
                break;
            }
            case PreInspectionFilterKind.GuidedFilter:
            {
                var eps = Math.Clamp((float)step.Sigma * 60.0f, 1.0f, 6000.0f);
                EdgePreservingFilterEngine.ApplyGuidedFilter(src, width, height, width, new GuidedFilterSpec(r, eps), dst, width);
                break;
            }
            case PreInspectionFilterKind.Bilateral:
            {
                var sigmaRange = Math.Clamp((float)step.Sigma * 12.0f, 2.0f, 150.0f);
                var sigmaSpace = Math.Max(1.0f, r * 0.75f);
                EdgePreservingFilterEngine.ApplyBilateralFilter(src, width, height, width, new BilateralFilterSpec(r, sigmaSpace, sigmaRange), dst, width);
                break;
            }
            case PreInspectionFilterKind.Median:
            {
                EdgePreservingFilterEngine.ApplyMedianFilter(src, width, height, width, r, dst, width);
                break;
            }
            case PreInspectionFilterKind.AdaptiveThreshold:
            {
                var winRadius = Math.Clamp(r * 2, 2, 64);
                var sauvolaK = Math.Clamp((float)step.Sigma * 0.08f, 0.02f, 0.50f);
                ContrastAndAdaptiveFilterEngine.ApplyAdaptiveThreshold(
                    src,
                    width,
                    height,
                    width,
                    new AdaptiveThresholdSpec(windowRadius: winRadius, offsetConstant: 6.0f, sauvolaSensitivityK: sauvolaK, useSauvolaStdWeighting: true),
                    dst,
                    width);
                break;
            }
            case PreInspectionFilterKind.UnsharpMask:
            {
                var amount = Math.Clamp((float)step.Sigma, 0.1f, 5.0f);
                ContrastAndAdaptiveFilterEngine.ApplyUnsharpMask(
                    src,
                    width,
                    height,
                    width,
                    new UnsharpSobelSpec(radius: r, sigma: Math.Max(0.8f, r * 0.5f), amount: amount),
                    dst,
                    width);
                break;
            }
            case PreInspectionFilterKind.WarpAffineShear:
            {
                var angleDeg = (float)((step.Threshold - 128.0) * 0.5);
                var shearX = (float)Math.Clamp((step.Sigma - 1.0) * 0.08, -0.45, 0.45);
                ImageWarpingEngine.ApplyWarp(
                    src,
                    width,
                    height,
                    width,
                    new ImageWarpSpec(WarpTransformKind.Affine, angleDegrees: angleDeg, scaleX: 1.0f, scaleY: 1.0f, shearX: shearX),
                    dst,
                    width,
                    height,
                    width);
                break;
            }
            case PreInspectionFilterKind.WarpPerspectiveKeystone:
            {
                var keystoneX = (float)Math.Clamp((step.Threshold - 128.0) / 320.0, -0.40, 0.40);
                var keystoneY = (float)Math.Clamp((step.Sigma - 1.0) * 0.05, -0.40, 0.40);
                ImageWarpingEngine.ApplyWarp(
                    src,
                    width,
                    height,
                    width,
                    new ImageWarpSpec(WarpTransformKind.PerspectiveKeystone, keystoneX: keystoneX, keystoneY: keystoneY),
                    dst,
                    width,
                    height,
                    width);
                break;
            }
            case PreInspectionFilterKind.WarpPolarUnwrap:
            {
                var innerRatio = (float)Math.Clamp(step.Threshold / 255.0 * 0.70, 0.0, 0.70);
                ImageWarpingEngine.ApplyWarp(
                    src,
                    width,
                    height,
                    width,
                    new ImageWarpSpec(WarpTransformKind.PolarAnnularUnwrap, polarInnerRadiusRatio: innerRatio),
                    dst,
                    width,
                    height,
                    width);
                break;
            }
            case PreInspectionFilterKind.WarpLensUndistort:
            {
                var k1 = (float)Math.Clamp((step.Threshold - 128.0) / 256.0, -0.50, 0.50);
                var k2 = (float)Math.Clamp((step.Sigma - 1.0) * 0.03, -0.25, 0.25);
                ImageWarpingEngine.ApplyWarp(
                    src,
                    width,
                    height,
                    width,
                    new ImageWarpSpec(WarpTransformKind.BrownConradyUndistort, radialK1: k1, radialK2: k2),
                    dst,
                    width,
                    height,
                    width);
                break;
            }
            case PreInspectionFilterKind.FftNotch:
            {
                var minRadius = Math.Clamp(r + 2, 3, 24);
                var notchSigma = Math.Clamp(step.Sigma, 1.0, 18.0);
                var ratio = Math.Clamp(step.Threshold / 28.0, 2.2, 14.0);
                FrequencyAndTextureFilterEngine.ApplyFftPeriodicNotchFilter(src, width, height, width, dst, minRadius, notchSigma, ratio);
                break;
            }
            case PreInspectionFilterKind.Homomorphic:
            {
                var gammaLow = Math.Clamp(step.Threshold / 255.0, 0.08, 0.95);
                var gammaHigh = Math.Clamp(1.0 + (step.Sigma * 0.4), 1.05, 3.8);
                var spaceSigma = Math.Clamp(r * 3.0, 3.0, 60.0);
                FrequencyAndTextureFilterEngine.ApplyHomomorphicFilter(src, width, height, width, dst, gammaLow, gammaHigh, spaceSigma);
                break;
            }
            case PreInspectionFilterKind.GaborTexture:
            {
                var wavelength = Math.Clamp((r * 2.0) + 2.0, 4.0, 32.0);
                var orientationDeg = (step.Threshold / 255.0) * 180.0;
                var gaborSigma = Math.Clamp(step.Sigma + 1.5, 1.5, 10.0);
                FrequencyAndTextureFilterEngine.ApplyGaborTextureFilter(src, width, height, width, dst, wavelength, orientationDeg, gaborSigma, 0.5);
                break;
            }
            default:
                src.Slice(0, width * height).CopyTo(dst);
                break;
        }
    }

    private static void ApplyBoxBlurSeparable(ReadOnlySpan<byte> src, Span<byte> dst, int width, int height, int radius)
    {
        var window = (2 * radius) + 1;
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                var sum = 0;
                for (var k = -radius; k <= radius; k++)
                {
                    var sx = Math.Clamp(x + k, 0, width - 1);
                    sum += src[row + sx];
                }

                dst[row + x] = (byte)(sum / window);
            }
        }
    }

    private static void ApplyMorphMinMax(ReadOnlySpan<byte> src, Span<byte> dst, int width, int height, int radius, bool isDilate)
    {
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                byte best = isDilate ? (byte)0 : MaxByteValue;
                for (var dy = -radius; dy <= radius; dy++)
                {
                    var sy = Math.Clamp(y + dy, 0, height - 1) * width;
                    for (var dx = -radius; dx <= radius; dx++)
                    {
                        var sx = Math.Clamp(x + dx, 0, width - 1);
                        var px = src[sy + sx];
                        if (isDilate ? px > best : px < best)
                        {
                            best = px;
                        }
                    }
                }

                dst[row + x] = best;
            }
        }
    }
}
