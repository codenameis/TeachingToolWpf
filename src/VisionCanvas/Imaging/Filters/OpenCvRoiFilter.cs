using OpenCvSharp;
using VisionCanvas.Core.Editing;
using VisionImaging.Abstractions;

namespace VisionCanvas.Imaging.Filters;

/// <summary>OpenCV boundary. Input is retained by its caller; every native Mat is deterministically disposed.</summary>
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
}
