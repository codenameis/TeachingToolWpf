using OpenCvSharp;
using VisionAlgorithm.OpenSrc.Preprocessing;
using VisionCanvas.Core.Editing;
using VisionImaging.Abstractions;

namespace VisionCanvas.Imaging.Filters;

/// <summary>OpenCV and OpenSrc.Preprocessing boundary. Input is retained by its caller; every native Mat is deterministically disposed.</summary>
public static class OpenCvRoiFilter
{
    internal static MatType Type(PixelKind kind) => kind switch
    {
        PixelKind.Gray8 => MatType.CV_8UC1,
        PixelKind.Gray16 => MatType.CV_16UC1,
        _ => MatType.CV_8UC4
    };

    internal static Mat Wrap(PixelBuffer pixels) => Mat.FromPixelData(pixels.Height, pixels.Width, Type(pixels.Kind), pixels.Bytes, pixels.Stride);

    public static byte[] Apply(PixelBuffer pixels, FilterSettings settings)
    {
        using var input = Wrap(pixels);
        return Apply(input, pixels.Kind, settings);
    }

    internal static byte[] Apply(Mat pixels, PixelKind kind, FilterSettings settings)
    {
        settings.Validate();
        using var gray = new Mat();
        if (kind == PixelKind.Gray16)
        {
            pixels.ConvertTo(gray, MatType.CV_8UC1, 1d / 257);
        }
        else if (kind == PixelKind.Gray8)
        {
            pixels.CopyTo(gray);
        }
        else
        {
            Cv2.CvtColor(pixels, gray, kind == PixelKind.Rgba32 ? ColorConversionCodes.RGBA2GRAY : ColorConversionCodes.BGRA2GRAY);
        }

        if (settings.IsPreprocessingOrWarp)
        {
            return ApplyPreprocessingOrWarp(gray, settings);
        }

        using var output = new Mat();
        switch (settings.Kind)
        {
            case RoiFilterKind.Binarize:
                Cv2.Threshold(gray, output, settings.Threshold, 255, ThresholdTypes.Binary);
                break;
            case RoiFilterKind.Edge:
                Cv2.Canny(gray, output, settings.EdgeLow, settings.EdgeHigh);
                break;
            default:
                gray.CopyTo(output);
                break;
        }
        var result = new byte[checked(output.Width * output.Height)];
        System.Runtime.InteropServices.Marshal.Copy(output.Data, result, 0, result.Length);
        return result;
    }

    private static byte[] ApplyPreprocessingOrWarp(Mat gray, FilterSettings settings)
    {
        var width = gray.Width;
        var height = gray.Height;
        var src = new byte[checked(width * height)];
        var dst = new byte[src.Length];
        for (var y = 0; y < height; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(gray.Ptr(y), src, y * width, width);
        }

        switch (settings.Kind)
        {
            case RoiFilterKind.Clahe:
            {
                var tiles = Math.Clamp(settings.Radius * 2, 2, 16);
                var clipLimit = Math.Clamp((float)settings.Sigma, 1.0f, 16.0f);
                ContrastAndAdaptiveFilterEngine.ApplyClahe(src, width, height, width, new ClaheFilterSpec(tiles, tiles, clipLimit), dst, width);
                break;
            }
            case RoiFilterKind.GuidedFilter:
            {
                var eps = Math.Clamp((float)settings.Sigma * 60.0f, 1.0f, 6000.0f);
                EdgePreservingFilterEngine.ApplyGuidedFilter(src, width, height, width, new GuidedFilterSpec(settings.Radius, eps), dst, width);
                break;
            }
            case RoiFilterKind.Bilateral:
            {
                var r = Math.Clamp(settings.Radius, 1, 12);
                var sigmaRange = Math.Clamp((float)settings.Sigma * 12.0f, 2.0f, 150.0f);
                var sigmaSpace = Math.Max(1.0f, r * 0.75f);
                EdgePreservingFilterEngine.ApplyBilateralFilter(src, width, height, width, new BilateralFilterSpec(r, sigmaSpace, sigmaRange), dst, width);
                break;
            }
            case RoiFilterKind.Median:
            {
                EdgePreservingFilterEngine.ApplyMedianFilter(src, width, height, width, Math.Clamp(settings.Radius, 1, 15), dst, width);
                break;
            }
            case RoiFilterKind.AdaptiveThreshold:
            {
                var winRadius = Math.Clamp(settings.Radius * 2, 2, 64);
                var sauvolaK = Math.Clamp((float)settings.Sigma * 0.08f, 0.02f, 0.50f);
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
            case RoiFilterKind.UnsharpMask:
            {
                var r = Math.Clamp(settings.Radius, 1, 15);
                var amount = Math.Clamp((float)settings.Sigma, 0.1f, 5.0f);
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
            case RoiFilterKind.SobelGradient:
            {
                var gain = Math.Clamp((float)settings.Sigma, 0.2f, 8.0f);
                ContrastAndAdaptiveFilterEngine.ApplySobelGradient(
                    src,
                    width,
                    height,
                    width,
                    scaleFactor: gain,
                    dst,
                    width);
                break;
            }
            case RoiFilterKind.WarpAffineShear:
            {
                var angleDeg = (float)((settings.Threshold - 128.0) * 0.5);
                var shearX = (float)Math.Clamp((settings.Sigma - 1.0) * 0.08, -0.45, 0.45);
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
            case RoiFilterKind.WarpPerspectiveKeystone:
            {
                var keystoneX = (float)Math.Clamp((settings.Threshold - 128.0) / 320.0, -0.40, 0.40);
                var keystoneY = (float)Math.Clamp((settings.Sigma - 1.0) * 0.05, -0.40, 0.40);
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
            case RoiFilterKind.WarpPolarUnwrap:
            {
                var innerRatio = (float)Math.Clamp(settings.Threshold / 255.0 * 0.70, 0.0, 0.70);
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
            case RoiFilterKind.WarpLensUndistort:
            {
                var k1 = (float)Math.Clamp((settings.Threshold - 128.0) / 256.0, -0.50, 0.50);
                var k2 = (float)Math.Clamp((settings.Sigma - 1.0) * 0.03, -0.25, 0.25);
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
            case RoiFilterKind.FftNotch:
            {
                var minRadius = Math.Clamp(settings.Radius + 2, 3, 24);
                var notchSigma = Math.Clamp(settings.Sigma, 1.0, 18.0);
                var ratio = Math.Clamp(settings.Threshold / 28.0, 2.2, 14.0);
                FrequencyAndTextureFilterEngine.ApplyFftPeriodicNotchFilter(src, width, height, width, dst, minRadius, notchSigma, ratio);
                break;
            }
            case RoiFilterKind.Homomorphic:
            {
                var gammaLow = Math.Clamp(settings.Threshold / 255.0, 0.08, 0.95);
                var gammaHigh = Math.Clamp(1.0 + (settings.Sigma * 0.4), 1.05, 3.8);
                var spaceSigma = Math.Clamp(settings.Radius * 3.0, 3.0, 60.0);
                FrequencyAndTextureFilterEngine.ApplyHomomorphicFilter(src, width, height, width, dst, gammaLow, gammaHigh, spaceSigma);
                break;
            }
            case RoiFilterKind.GaborTexture:
            {
                var wavelength = Math.Clamp((settings.Radius * 2.0) + 2.0, 4.0, 32.0);
                var orientationDeg = (settings.Threshold / 255.0) * 180.0;
                var gaborSigma = Math.Clamp(settings.Sigma + 1.5, 1.5, 10.0);
                FrequencyAndTextureFilterEngine.ApplyGaborTextureFilter(src, width, height, width, dst, wavelength, orientationDeg, gaborSigma, 0.5);
                break;
            }
            default:
                Array.Copy(src, dst, src.Length);
                break;
        }

        return dst;
    }
}
