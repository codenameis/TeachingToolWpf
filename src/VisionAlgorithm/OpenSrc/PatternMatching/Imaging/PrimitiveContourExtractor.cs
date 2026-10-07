using System.Collections.Immutable;
using OpenCvSharp;
using VisionAlgorithm.OpenSrc.ShapeDetection;

namespace VisionAlgorithm.OpenSrc.PatternMatching.Imaging;

internal sealed record PrimitiveContour(ImmutableArray<ImagePoint> Points, ImmutableArray<ImagePoint> Approximation,
    ImagePoint Center, double Area, double Perimeter, bool Clipped, bool HasHole);

/// <summary>OpenCV 객체와 네이티브 호출 수명을 이 경계에만 유지한다.</summary>
internal static class PrimitiveContourExtractor
{
    public const int MaximumContours = 4096;

    public static (ImmutableArray<PrimitiveContour> Contours, bool Limited) Extract(GrayImage image,
        PrimitiveDetectionOptions options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var pixels = new byte[checked(image.Width * image.Height)];
        for (var row = 0; row < image.Height; row++)
        {
            token.ThrowIfCancellationRequested();
            image.Pixels.Span.Slice(row * image.Stride, image.Width).CopyTo(pixels.AsSpan(row * image.Width));
        }
        using var source = Mat.FromPixelData(image.Height, image.Width, MatType.CV_8UC1, pixels, image.Width);
        using var filtered = new Mat();
        using var binary = new Mat();
        Cv2.MedianBlur(source, filtered, 3);
        var masked = !image.ExcludedPixels.IsEmpty;
        var thresholdType = ThresholdTypes.Binary | (options.AutomaticThreshold && !masked ? ThresholdTypes.Otsu : 0);
        var threshold = options.AutomaticThreshold && masked ? MaskedThreshold(filtered, image, token) : options.Threshold;
        Cv2.Threshold(filtered, binary, threshold, 255, thresholdType);
        token.ThrowIfCancellationRequested();
        if (options.Polarity == ForegroundPolarity.Dark ||
            (options.Polarity == ForegroundPolarity.Auto && (masked ? MostlyBright(binary, image, token) : Cv2.CountNonZero(binary) > image.Width * image.Height / 2)))
        {
            Cv2.BitwiseNot(binary, binary);
        }
        Cv2.FindContours(binary, out Point[][] contours, out HierarchyIndex[] hierarchy,
            RetrievalModes.CComp, ContourApproximationModes.ApproxSimple);
        token.ThrowIfCancellationRequested();
        var output = ImmutableArray.CreateBuilder<PrimitiveContour>();
        var limited = contours.Length > MaximumContours;
        for (var index = 0; index < Math.Min(contours.Length, MaximumContours); index++)
        {
            token.ThrowIfCancellationRequested();
            if (hierarchy[index].Parent >= 0 || contours[index].Length < 3)
            {
                continue;
            }
            var contour = contours[index];
            var area = Math.Abs(Cv2.ContourArea(contour));
            if (area < 9)
            {
                continue;
            }
            var moments = Cv2.Moments(contour);
            if (Math.Abs(moments.M00) < 1e-8)
            {
                continue;
            }
            var perimeter = Cv2.ArcLength(contour, true);
            var approximation = Cv2.ApproxPolyDP(contour, options.ApproximationTolerance * perimeter, true);
            output.Add(new(contour.Select(point => new ImagePoint(point.X + .5, point.Y + .5)).ToImmutableArray(),
                approximation.Select(point => new ImagePoint(point.X + .5, point.Y + .5)).ToImmutableArray(),
                new((moments.M10 / moments.M00) + .5, (moments.M01 / moments.M00) + .5), area, perimeter,
                contour.Any(point => point.X <= 0 || point.Y <= 0 || point.X >= image.Width - 1 || point.Y >= image.Height - 1),
                hierarchy[index].Child >= 0));
        }
        return (output.ToImmutable(), limited);
    }

    private static double MaskedThreshold(Mat filtered, GrayImage image, CancellationToken token)
    {
        var values = new List<byte>();
        for (var y = 1; y < image.Height - 1; y++)
        {
            token.ThrowIfCancellationRequested();
            for (var x = 1; x < image.Width - 1; x++)
            {
                var valid = true;
                for (var dy = -1; dy <= 1 && valid; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        valid &= image.ExcludedPixels.Span[((y + dy) * image.Width) + x + dx] == 0;
                    }
                }
                if (valid)
                {
                    values.Add(filtered.At<byte>(y, x));
                }
            }
        }
        if (values.Count == 0)
        {
            throw new ArgumentException("마스크를 제외한 유효 검사 픽셀이 없습니다.");
        }
        using var samples = Mat.FromPixelData(1, values.Count, MatType.CV_8UC1, values.ToArray());
        using var result = new Mat();
        return Cv2.Threshold(samples, result, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
    }

    private static bool MostlyBright(Mat binary, GrayImage image, CancellationToken token)
    {
        var valid = 0;
        var bright = 0;
        for (var y = 0; y < image.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (var x = 0; x < image.Width; x++)
            {
                if (image.ExcludedPixels.Span[(y * image.Width) + x] == 0)
                {
                    valid++;
                    bright += binary.At<byte>(y, x) == 0 ? 0 : 1;
                }
            }
        }
        return bright > valid / 2;
    }
}
