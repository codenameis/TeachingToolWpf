using System.Collections.Immutable;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionAlgorithm.OpenSrc.PatternMatching.Imaging;

namespace VisionAlgorithm.OpenSrc.ShapeDetection;

/// <summary>이진 외곽 윤곽의 기하 규칙으로 채워진 기본 도형을 분류한다. 템플릿 학습은 사용하지 않는다.</summary>
public static class PrimitiveShapeDetector
{
    public const int MaximumPixels = 2 * 1024 * 1024;

    public static PrimitiveDetectionResult Detect(GrayImage image, PrimitiveDetectionOptions options, CancellationToken token = default)
    {
        options.Validate();
        token.ThrowIfCancellationRequested();
        if ((long)image.Width * image.Height > MaximumPixels)
        {
            throw new ArgumentException("도형 검출 창은 최대 2 Mpx입니다.", nameof(image));
        }
        var extraction = PrimitiveContourExtractor.Extract(image, options, token);
        var shapes = new List<PrimitiveShapeDetection>();
        var clipped = ImmutableArray.CreateBuilder<ImagePoint>();
        foreach (var contour in extraction.Contours)
        {
            token.ThrowIfCancellationRequested();
            var width = contour.Points.Max(point => point.X) - contour.Points.Min(point => point.X);
            var height = contour.Points.Max(point => point.Y) - contour.Points.Min(point => point.Y);
            if (Math.Min(width, height) < options.MinimumSize)
            {
                continue;
            }
            if (contour.Clipped)
            {
                clipped.Add(contour.Center);
                continue;
            }
            if (Math.Max(width, height) > options.MaximumSize || contour.HasHole)
            {
                continue;
            }
            var classification = PrimitiveContourClassifier.Classify(contour, options);
            if (classification is { } shape && (options.Shapes & shape.Kind) != 0 && shape.Score >= options.MinimumScore)
            {
                shapes.Add(shape);
            }
        }
        return new(shapes.OrderByDescending(shape => shape.Score).ThenBy(shape => shape.Center.Y).ThenBy(shape => shape.Center.X)
            .Take(options.MaximumResults).ToImmutableArray(), clipped.ToImmutable(), extraction.Limited || shapes.Count > options.MaximumResults);
    }
}
