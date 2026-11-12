// File: src/VisionAlgorithm/OpenSrc/PatternMatching/Imaging/PrimitiveContourExtractor.cs
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using OpenCvSharp;
using VisionAlgorithm.OpenSrc.ShapeDetection;

namespace VisionAlgorithm.OpenSrc.PatternMatching.Imaging;

internal sealed record PrimitiveContour(ImmutableArray<ImagePoint> Points, ImmutableArray<ImagePoint> Approximation,
    ImagePoint Center, double Area, double Perimeter, bool Clipped, bool HasHole)
{
    public ImmutableArray<PrimitiveContour> Holes { get; init; } = [];
    public ImmutableArray<ImagePoint> FineApproximation { get; init; } = [];
}

/// <summary>OpenCV 객체와 네이티브 호출 수명을 이 경계에만 유지한다.</summary>
internal static class PrimitiveContourExtractor
{
    public static ImmutableArray<PrimitiveContour> Extract(ShapeGrayImage image,
        PrimitiveDetectionOptions options, CancellationToken token, IPrimitivePreprocessor? preprocessor = null,
        Memory<byte> preprocessingBuffer = default)
    {
        token.ThrowIfCancellationRequested();
        var prepared = preprocessor?.Median3x3(image, token, preprocessingBuffer);
        if (prepared is not null && (prepared.Width != image.Width || prepared.Height != image.Height))
        {
            throw new InvalidOperationException("도형 전처리 결과 크기가 원본과 다릅니다.");
        }
        using var filtered = prepared is null ? new Mat() : Source(prepared, token);
        using var binary = new Mat();
        if (prepared is null)
        {
            using var source = Source(image, token);
            Cv2.MedianBlur(source, filtered, 3);
        }
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
        for (var index = 0; index < contours.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            if (hierarchy[index].Parent >= 0 || CreateContour(index) is not { } outer)
            {
                continue;
            }
            var holes = ImmutableArray.CreateBuilder<PrimitiveContour>();
            // CComp의 구멍 안에 있는 독립 전경은 다시 최상위 윤곽이다.
            // 직접 자식만 묶어 구멍을 중복 집계하거나 내부 도형을 삼키지 않는다.
            for (var child = hierarchy[index].Child; child >= 0; child = hierarchy[child].Next)
            {
                token.ThrowIfCancellationRequested();
                if (CreateContour(child) is { } hole)
                {
                    holes.Add(hole);
                }
                else
                {
                    // 해석할 수 없는 구멍을 버려 채워진 도형으로 오인하지 않는다.
                    holes.Clear();
                    break;
                }
            }
            output.Add(outer with
            {
                Holes = holes.ToImmutable()
            });
        }
        return output.ToImmutable();

        PrimitiveContour? CreateContour(int index)
        {
            var contour = contours[index];
            if (contour.Length < 3)
            {
                return null;
            }
            var area = Math.Abs(Cv2.ContourArea(contour));
            if (area < 9)
            {
                return null;
            }
            var moments = Cv2.Moments(contour);
            if (Math.Abs(moments.M00) < 1e-8)
            {
                return null;
            }
            var perimeter = Cv2.ArcLength(contour, true);
            var approximation = Cv2.ApproxPolyDP(contour, options.ApproximationTolerance * perimeter, true);
            return new(contour.Select(point => new ImagePoint(point.X + .5, point.Y + .5)).ToImmutableArray(),
                approximation.Select(point => new ImagePoint(point.X + .5, point.Y + .5)).ToImmutableArray(),
                new((moments.M10 / moments.M00) + .5, (moments.M01 / moments.M00) + .5), area, perimeter,
                contour.Any(point => point.X <= 0 || point.Y <= 0 || point.X >= image.Width - 1 || point.Y >= image.Height - 1),
                hierarchy[index].Child >= 0)
            {
                FineApproximation = approximation.Length >= 6 && !Cv2.IsContourConvex(approximation) &&
                    (options.Shapes & (PrimitiveShapeKind.CrossHash | PrimitiveShapeKind.Star | PrimitiveShapeKind.Asterisk)) != 0
                    ? Cv2.ApproxPolyDP(contour, Math.Min(.005, options.ApproximationTolerance) * perimeter, true)
                        .Select(point => new ImagePoint(point.X + .5, point.Y + .5)).ToImmutableArray()
                    : []
            };
        }
    }

    private static Mat Source(ShapeGrayImage image, CancellationToken token)
    {
        // Mat pins this array only for the native call lifetime. The source remains read-only.
        if (MemoryMarshal.TryGetArray(image.Pixels, out var segment) && segment.Offset == 0 &&
            segment.Count >= (long)image.Stride * image.Height)
        {
            return Mat.FromPixelData(image.Height, image.Width, MatType.CV_8UC1, segment.Array!, image.Stride);
        }
        var pixels = new byte[checked(image.Width * image.Height)];
        for (var row = 0; row < image.Height; row++)
        {
            token.ThrowIfCancellationRequested();
            image.Pixels.Span.Slice(row * image.Stride, image.Width).CopyTo(pixels.AsSpan(row * image.Width));
        }
        return Mat.FromPixelData(image.Height, image.Width, MatType.CV_8UC1, pixels, image.Width);
    }

    private static double MaskedThreshold(Mat filtered, ShapeGrayImage image, CancellationToken token)
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

    private static bool MostlyBright(Mat binary, ShapeGrayImage image, CancellationToken token)
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
