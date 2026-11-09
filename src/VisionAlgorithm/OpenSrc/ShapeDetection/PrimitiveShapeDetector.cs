// File: src/VisionAlgorithm/OpenSrc/ShapeDetection/PrimitiveShapeDetector.cs
using System.Collections.Immutable;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionAlgorithm.OpenSrc.PatternMatching.Imaging;

namespace VisionAlgorithm.OpenSrc.ShapeDetection;

/// <summary>이진 윤곽과 구멍의 기하 규칙으로 기본 도형을 분류한다. 템플릿 학습은 사용하지 않는다.</summary>
public static class PrimitiveShapeDetector
{
    public const int MaximumPixels = 2 * 1024 * 1024;

    public static PrimitiveDetectionResult Detect(GrayImage image, PrimitiveDetectionOptions options, CancellationToken token = default,
        IPrimitivePreprocessor? preprocessor = null, Memory<byte> preprocessingBuffer = default)
    {
        options.Validate();
        token.ThrowIfCancellationRequested();
        if ((long)image.Width * image.Height > MaximumPixels)
        {
            throw new ArgumentException("도형 검출 창은 최대 2 Mpx입니다.", nameof(image));
        }
        if (options.UseGpu && preprocessor is null)
        {
            throw new NotSupportedException("CUDA 도형 전처리가 연결되지 않았습니다. CPU 모드를 선택하세요.");
        }
        var extraction = PrimitiveContourExtractor.Extract(image, options, token, options.UseGpu ? preprocessor : null, preprocessingBuffer);
        var shapes = new List<PrimitiveShapeDetection>();
        var doubleSquares = (options.Shapes & PrimitiveShapeKind.DoubleSquare) != 0;
        var clipped = ImmutableArray.CreateBuilder<ImagePoint>();
        foreach (var contour in extraction)
        {
            token.ThrowIfCancellationRequested();
            if (OverlapsMask(image, contour, token))
            {
                continue;
            }
            GetBounds(contour.Points.AsSpan(), out var minX, out var minY, out var maxX, out var maxY);
            var width = maxX - minX;
            var height = maxY - minY;
            if (Math.Min(width, height) < (doubleSquares ? 6 : options.MinimumSize))
            {
                continue;
            }
            if (contour.Clipped)
            {
                clipped.Add(contour.Center);
                continue;
            }
            if (Math.Max(width, height) > options.MaximumSize)
            {
                continue;
            }
            var classification = PrimitiveContourClassifier.Classify(contour, options);
            if (classification is { } shape && shape.Score >= options.MinimumScore)
            {
                shapes.Add(shape);
            }
        }
        if (doubleSquares)
        {
            shapes = DoubleSquareClassifier.Combine(shapes, options, token);
        }
        if ((options.Shapes & PrimitiveShapeKind.Rectangle) == 0 && (options.Shapes & PrimitiveShapeKind.Diamond) != 0)
        {
            shapes = shapes.Select(shape => shape.Kind == PrimitiveShapeKind.Rectangle && DoubleSquareClassifier.IsSquare(shape)
                ? shape with
                {
                    Kind = PrimitiveShapeKind.Diamond
                } : shape).ToList();
        }
        shapes = shapes.Where(shape => (options.Shapes & shape.Kind) != 0 && MeetsMinimumSize(shape)).ToList();
        var limit = options.MaximumResults == 0 ? int.MaxValue : options.MaximumResults;
        return new(shapes.OrderByDescending(shape => shape.Score).ThenBy(shape => shape.Center.Y).ThenBy(shape => shape.Center.X)
            .Take(limit).ToImmutableArray(), clipped.ToImmutable(), shapes.Count > limit);

        bool MeetsMinimumSize(PrimitiveShapeDetection shape)
        {
            var points = shape.Contour.Concat(shape.AdditionalContours.SelectMany(contour => contour)).ToArray();
            GetBounds(points.AsSpan(), out var minX, out var minY, out var maxX, out var maxY);
            return Math.Min(maxX - minX, maxY - minY) >= options.MinimumSize;
        }
    }

    private static bool OverlapsMask(GrayImage image, PrimitiveContour contour, CancellationToken token)
    {
        if (image.ExcludedPixels.IsEmpty)
        {
            return false;
        }
        var region = new SearchRegion(contour.Points);
        var holes = contour.Holes.Select(hole => new SearchRegion(hole.Points)).ToArray();
        GetBounds(contour.Points.AsSpan(), out var minX, out var minY, out var maxX, out var maxY);
        var left = Math.Max(0, (int)Math.Floor(minX) - 1);
        var top = Math.Max(0, (int)Math.Floor(minY) - 1);
        var right = Math.Min(image.Width - 1, (int)Math.Ceiling(maxX) + 1);
        var bottom = Math.Min(image.Height - 1, (int)Math.Ceiling(maxY) + 1);
        for (var y = top; y <= bottom; y++)
        {
            token.ThrowIfCancellationRequested();
            for (var x = left; x <= right; x++)
            {
                if (image.ExcludedPixels.Span[(y * image.Width) + x] != 0 && region.ContainsWithin(x + .5, y + .5, 1.5) &&
                    !holes.Any(hole => InsideHole(hole, x + .5, y + .5)))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool InsideHole(SearchRegion hole, double x, double y)
    {
        if (!hole.Contains(x, y))
        {
            return false;
        }
        for (var index = 0; index < hole.Boundary.Length; index++)
        {
            var a = hole.Boundary[index];
            var b = hole.Boundary[(index + 1) % hole.Boundary.Length];
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var lengthSquared = (dx * dx) + (dy * dy);
            var fraction = lengthSquared == 0 ? 0 : Math.Clamp((((x - a.X) * dx) + ((y - a.Y) * dy)) / lengthSquared, 0, 1);
            var ex = x - a.X - (fraction * dx);
            var ey = y - a.Y - (fraction * dy);
            // median이 참조하는 경계 주변은 구멍 쪽 마스크도 검출에서 제외한다.
            if ((ex * ex) + (ey * ey) <= 2.25)
            {
                return false;
            }
        }
        return true;
    }

    private static void GetBounds(ReadOnlySpan<ImagePoint> points, out double minX, out double minY, out double maxX, out double maxY)
    {
        if (points.IsEmpty)
        {
            minX = minY = maxX = maxY = 0;
            return;
        }
        minX = maxX = points[0].X;
        minY = maxY = points[0].Y;
        for (var i = 1; i < points.Length; i++)
        {
            var p = points[i];
            if (p.X < minX) minX = p.X;
            else if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y;
            else if (p.Y > maxY) maxY = p.Y;
        }
    }
}
