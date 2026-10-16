using OpenCvSharp;
using VisionImaging.Abstractions;

namespace VisionAnomaly.Imaging;

internal static class FrameImages
{
    public static Mat Crop(VideoFrame frame, AnalysisRegion region)
    {
        region.Validate();
        var pixels = frame.Pixels;
        if (pixels.Kind != PixelKind.Bgra32)
        {
            throw new ArgumentException("BGRA32 프레임이 필요합니다.");
        }

        using var source = Mat.FromPixelData(pixels.Height, pixels.Width, MatType.CV_8UC4, pixels.Bytes, pixels.Stride);
        using var crop = new Mat(source, Bounds(pixels.Width, pixels.Height, region));
        var resized = new Mat();
        Cv2.Resize(crop, resized, new(224, 224), interpolation: InterpolationFlags.Area);
        return resized;
    }

    internal static Rect Bounds(int width, int height, AnalysisRegion region)
    {
        var left = Math.Clamp((int)(region.X * width), 0, width - 1);
        var top = Math.Clamp((int)(region.Y * height), 0, height - 1);
        var right = Math.Clamp((int)Math.Ceiling((region.X + region.Width) * width), left + 1, width);
        var bottom = Math.Clamp((int)Math.Ceiling((region.Y + region.Height) * height), top + 1, height);
        return new(left, top, right - left, bottom - top);
    }

    public static void SaveEvidence(VideoFrame frame, string path)
    {
        var pixels = frame.Pixels;
        using var source = Mat.FromPixelData(pixels.Height, pixels.Width, MatType.CV_8UC4, pixels.Bytes, pixels.Stride);
        using var color = new Mat();
        Cv2.CvtColor(source, color, ColorConversionCodes.BGRA2BGR);
        if (!Cv2.ImWrite(path, color))
        {
            throw new IOException("알람 증거 영상을 저장하지 못했습니다.");
        }
    }
}
