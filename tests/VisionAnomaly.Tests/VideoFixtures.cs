using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenCvSharp;
using VisionImaging.Abstractions;

namespace VisionAnomaly.Tests;

internal static class VideoFixtures
{
    public static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));

    public static Mat Image(int frame, bool defect = false, int speed = 1)
    {
        var image = new Mat(360, 640, MatType.CV_8UC3, new Scalar(61, 73, 71));
        Cv2.Rectangle(image, new(0, 95, 640, 205), new Scalar(37, 50, 50), -1);
        for (var x = 0; x < 640; x += 16)
        {
            Cv2.Line(image, new(x, 100), new(x, 295), new Scalar(99, 110, 104), 5);
        }

        for (var item = -1; item < 5; item++)
        {
            var x = (((item * 150) + (frame * speed * 2)) % 750) - 60;
            Cv2.Rectangle(image, new(x, 145, 95, 95), new Scalar(144, 173, 193), -1);
            Cv2.Rectangle(image, new(x + 12, 165, 45, 25), new Scalar(219, 225, 225), -1);
            for (var line = 0; line < 9; line++)
            {
                Cv2.Line(image, new(x + 15 + (line * 4), 169), new(x + 15 + (line * 4), 185), Scalar.Black, 1);
            }
        }
        if (defect)
        {
            Cv2.Rectangle(image, new(260, 135, 95, 110), new Scalar(35, 40, 215), -1);
            Cv2.Line(image, new(260, 135), new(355, 245), Scalar.Black, 12);
        }
        Cv2.PutText(image, "SYNTHETIC VALIDATION / WATCH", new(20, 40), HersheyFonts.HersheySimplex, .6, new Scalar(203, 218, 217));
        return image;
    }

    public static VideoFrame Frame(int index, bool defect = false, int speed = 1)
    {
        using var image = Image(index, defect, speed);
        using var bgra = new Mat();
        Cv2.CvtColor(image, bgra, ColorConversionCodes.BGR2BGRA);
        var pixels = new PixelBuffer(640, 360, PixelKind.Bgra32);
        Marshal.Copy(bgra.Data, pixels.Bytes, 0, pixels.Length);
        return new(index + 1, (long)(index * .2 * Stopwatch.Frequency), pixels);
    }

    public static string WriteVideo(string name, bool anomalies)
    {
        var folder = Path.Combine(Root, "samples", "AnomalyMonitor.Data");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name + ".avi");
        using var writer = new VideoWriter(path, FourCC.MJPG, 10, new(640, 360));
        if (!writer.IsOpened())
        {
            throw new IOException("검증 AVI를 생성하지 못했습니다.");
        }

        for (var frame = 0; frame < 240; frame++)
        {
            using var image = Image(frame / 2, anomalies && frame is >= 110 and <= 125,
                anomalies && frame is >= 160 and <= 195 ? 7 : 1);
            writer.Write(image);
        }
        return path;
    }
}
