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
        var thresholdType = ThresholdTypes.Binary | (options.AutomaticThreshold ? ThresholdTypes.Otsu : 0);
        Cv2.Threshold(filtered, binary, options.Threshold, 255, thresholdType);
        token.ThrowIfCancellationRequested();
        if (options.Polarity == ForegroundPolarity.Dark ||
            (options.Polarity == ForegroundPolarity.Auto && Cv2.CountNonZero(binary) > image.Width * image.Height / 2))
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
}
