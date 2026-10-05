using System.Collections.Immutable;
using System.Diagnostics;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionAlgorithm.OpenSrc.ShapeDetection;
using VisionGeometry;
using VisionImaging;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Matching;

public sealed record PrimitiveDetectionOutput(ImmutableArray<PrimitiveShapeDetection> Shapes, int Windows,
    bool Incomplete, int ClippedContours, double Milliseconds);

public interface IPrimitiveShapeDetectionService
{
    Task<PrimitiveDetectionOutput> DetectAsync(ImageReference image, bool live, RoiGeometry? region,
        PrimitiveDetectionOptions options, CancellationToken token);
}

/// <summary>독립 원본 reader와 제한된 창을 소유한다. 최대 도형 폭보다 넓은 halo와 중심 소유권으로 이음새를 처리한다.</summary>
public sealed class PrimitiveShapeDetectionService(IImageLoader loader, Func<VideoFrame?> retainFrame) : IPrimitiveShapeDetectionService
{
    public Task<PrimitiveDetectionOutput> DetectAsync(ImageReference image, bool live, RoiGeometry? region,
        PrimitiveDetectionOptions options, CancellationToken token)
        => Task.Run(async () =>
        {
            options.Validate();
            if (region is not null)
            {
                ShapeMatchingService.ValidateRegion(image, region);
            }
            var timer = Stopwatch.StartNew();
            using var reader = await MatchingImageReader.OpenAsync(image, live, loader, retainFrame, token).ConfigureAwait(false);
            var halo = (int)Math.Ceiling(options.MaximumSize) + 4;
            var interior = Math.Min(768, (int)Math.Sqrt(PrimitiveShapeDetector.MaximumPixels) - (2 * halo));
            var bounds = region?.Bounds ?? new BoundsD(0, 0, image.Width, image.Height);
            var found = new List<PrimitiveShapeDetection>();
            var clipped = 0;
            var windows = 0;
            var incomplete = false;
            for (var y = Math.Max(0, (int)bounds.Top / interior * interior); y < bounds.Bottom; y += interior)
            {
                for (var x = Math.Max(0, (int)bounds.Left / interior * interior); x < bounds.Right; x += interior)
                {
                    token.ThrowIfCancellationRequested();
                    var left = Math.Max(0, x - halo);
                    var top = Math.Max(0, y - halo);
                    var right = Math.Min(image.Width, x + interior + halo);
                    var bottom = Math.Min(image.Height, y + interior + halo);
                    var pixels = reader.Read(left, top, right - left, bottom - top, token, PrimitiveShapeDetector.MaximumPixels);
                    var result = PrimitiveShapeDetector.Detect(pixels, options with
                    {
                        MaximumResults = 1000
                    }, token);
                    windows++;
                    incomplete |= result.LimitExceeded;
                    bool Owned(ImagePoint center) => center.X + left >= x && center.X + left < x + interior &&
                        center.Y + top >= y && center.Y + top < y + interior;
                    clipped += result.ClippedCenters.Count(center => Owned(center) &&
                        (region?.Contains(new PointD(center.X + left, center.Y + top)) ?? true));
                    foreach (var detection in result.Shapes.Where(shape => Owned(shape.Center)))
                    {
                        token.ThrowIfCancellationRequested();
                        var contour = detection.Contour.Select(point => new ImagePoint(point.X + left, point.Y + top)).ToImmutableArray();
                        if (region is not null && !ContainsContour(region, contour))
                        {
                            continue;
                        }
                        if (found.Count < options.MaximumResults)
                        {
                            found.Add(detection with
                            {
                                Center = new(detection.Center.X + left, detection.Center.Y + top),
                                Contour = contour
                            });
                        }
                        else
                        {
                            incomplete = true;
                        }
                    }
                }
            }
            return new PrimitiveDetectionOutput(found.ToImmutableArray(), windows, incomplete || clipped > 0, clipped, timer.Elapsed.TotalMilliseconds);
        }, token);

    private static bool ContainsContour(RoiGeometry region, ImmutableArray<ImagePoint> contour)
    {
        var points = contour.Select(point => new PointD(point.X, point.Y)).ToArray();
        if (points.Any(point => !region.Contains(point, 1e-7)))
        {
            return false;
        }
        if (region.Shape.Kind != RoiShapeKind.Polygon)
        {
            return true;
        }
        var boundary = region.Outline;
        for (var index = 0; index < points.Length; index++)
        {
            var start = points[index];
            var direction = points[(index + 1) % points.Length] - start;
            var cuts = new List<double> { 0, 1 };
            for (var edge = 0; edge < boundary.Length; edge++)
            {
                var a = boundary[edge];
                var b = boundary[(edge + 1) % boundary.Length];
                var other = b - a;
                var denominator = Cross(direction, other);
                if (Math.Abs(denominator) > 1e-10)
                {
                    var t = Cross(a - start, other) / denominator;
                    var u = Cross(a - start, direction) / denominator;
                    if (t > 0 && t < 1 && u >= -1e-10 && u <= 1 + 1e-10)
                    {
                        cuts.Add(t);
                    }
                }
            }
            cuts.Sort();
            // 부모/자식 계층의 면적 규칙과 구별하여 동일 경계를 허용한다.
            // 모든 교차 사이 구간을 확인하므로 오목 ROI의 홈을 건너뛰지 않는다.
            for (var cut = 1; cut < cuts.Count; cut++)
            {
                if (!region.Contains(start + (direction * ((cuts[cut - 1] + cuts[cut]) / 2)), 1e-7))
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static double Cross(PointD first, PointD second) => (first.X * second.Y) - (first.Y * second.X);
}
