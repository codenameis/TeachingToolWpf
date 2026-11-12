using System.Collections.Immutable;
using System.Diagnostics;
using VisionAlgorithm;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionAlgorithm.OpenSrc.ShapeDetection;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>독립 원본 reader와 제한된 창을 소유한다. 최대 도형 폭보다 넓은 halo와 중심 소유권으로 이음새를 처리한다.</summary>
public sealed class PrimitiveShapeDetectionService(IImageLoader loader, Func<VideoFrame?> retainFrame,
    Func<IPrimitivePreprocessor>? createGpu = null,
    IPreInspectionFilterPipeline? preFilterPipeline = null) : IPrimitiveShapeDetectionService
{
    public Task<PrimitiveDetectionOutput> DetectAsync(ImageReference image, bool live, RoiGeometry? region,
        PrimitiveDetectionOptions options, CancellationToken token, RoiExclusion? exclusion = null)
        => Task.Run(async () =>
        {
            options.Validate();
            if (region is not null)
            {
                MatchingRegionValidator.ValidateTeachingRegion(image, region);
            }
            var timer = Stopwatch.StartNew();
            using var preprocessor = options.UseGpu
                ? (createGpu ?? throw new NotSupportedException("CUDA 도형 전처리가 연결되지 않았습니다. CPU 모드를 선택하세요."))()
                : null;
            var initializationMilliseconds = timer.Elapsed.TotalMilliseconds;
            using var reader = await MatchingImageReader.OpenAsync(image, live, loader, retainFrame, token, preFilterPipeline).ConfigureAwait(false);
            reader.PreferDirectGray = true;
            var halo = (int)Math.Ceiling(options.MaximumSize) + 4;
            reader.Exclusion = exclusion;
            var interior = Math.Min(768, (int)Math.Sqrt(PrimitiveShapeDetector.MaximumPixels) - (2 * halo));
            var bounds = region?.Bounds ?? new BoundsD(0, 0, image.Width, image.Height);
            var found = new List<PrimitiveShapeDetection>();
            var clipped = 0;
            var windows = 0;
            var incomplete = false;
            var batch = new List<DetectionWindow>(options.MaximumParallelism);
            var buffers = new byte[options.MaximumParallelism][];
            var preprocessingBuffers = new byte[options.MaximumParallelism][];
            var windowOptions = options with
            {
                MaximumResults = 0
            };
            var readTicks = 0L;
            var detectionTicks = 0L;
            var detectionWallTicks = 0L;
            for (var y = Math.Max(0, (int)bounds.Top / interior * interior); y < bounds.Bottom; y += interior)
            {
                for (var x = Math.Max(0, (int)bounds.Left / interior * interior); x < bounds.Right; x += interior)
                {
                    token.ThrowIfCancellationRequested();
                    var left = Math.Max(0, x - halo);
                    var top = Math.Max(0, y - halo);
                    var right = Math.Min(image.Width, x + interior + halo);
                    var bottom = Math.Min(image.Height, y + interior + halo);
                    var readStart = Stopwatch.GetTimestamp();
                    var buffer = buffers[batch.Count] ??= new byte[PrimitiveShapeDetector.MaximumPixels];
                    var pixels = reader.Read(left, top, right - left, bottom - top, token, PrimitiveShapeDetector.MaximumPixels, buffer);
                    readTicks += Stopwatch.GetTimestamp() - readStart;
                    batch.Add(new(pixels, x, y, left, top));
                    if (batch.Count == options.MaximumParallelism)
                    {
                        ProcessBatch();
                    }
                }
            }
            ProcessBatch();
            token.ThrowIfCancellationRequested();
            return new PrimitiveDetectionOutput(found.ToImmutableArray(), windows, incomplete || clipped > 0, clipped, timer.Elapsed.TotalMilliseconds)
            {
                ReadMilliseconds = readTicks * 1000d / Stopwatch.Frequency,
                DetectionWorkMilliseconds = detectionTicks * 1000d / Stopwatch.Frequency,
                DetectionMilliseconds = detectionWallTicks * 1000d / Stopwatch.Frequency,
                ComputeInitializationMilliseconds = initializationMilliseconds,
                Gpu = preprocessor?.Diagnostics
            };

            void ProcessBatch()
            {
                if (batch.Count == 0)
                {
                    return;
                }
                var batchStart = Stopwatch.GetTimestamp();
                var results = new PrimitiveDetectionResult[batch.Count];
                // 읽기/캐시는 한 작업자가 소유한다. 최대 네 창만 계산하며 완료 후 원래 순서로 합친다.
                Parallel.For(0, batch.Count, new ParallelOptions
                {
                    MaxDegreeOfParallelism = options.MaximumParallelism,
                    CancellationToken = token
                }, index =>
                {
                    var start = Stopwatch.GetTimestamp();
                    var preprocessingBuffer = options.UseGpu
                        ? preprocessingBuffers[index] ??= new byte[PrimitiveShapeDetector.MaximumPixels]
                        : null;
                    results[index] = PrimitiveShapeDetector.Detect(batch[index].Pixels, windowOptions, token, preprocessor, preprocessingBuffer);
                    Interlocked.Add(ref detectionTicks, Stopwatch.GetTimestamp() - start);
                });
                for (var index = 0; index < batch.Count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    var window = batch[index];
                    var result = results[index];
                    var left = window.Left;
                    var top = window.Top;
                    windows++;
                    incomplete |= result.LimitExceeded;
                    bool Owned(ImagePoint center) => center.X + left >= window.X && center.X + left < window.X + interior &&
                        center.Y + top >= window.Y && center.Y + top < window.Y + interior;
                    clipped += result.ClippedCenters.Count(center => Owned(center) &&
                        (region?.Contains(new PointD(center.X + left, center.Y + top)) ?? true));
                    foreach (var detection in result.Shapes.Where(shape => Owned(shape.Center)))
                    {
                        token.ThrowIfCancellationRequested();
                        var contour = detection.Contour.Select(point => new ImagePoint(point.X + left, point.Y + top)).ToImmutableArray();
                        var additional = detection.AdditionalContours.Select(component => component.Select(point =>
                            new ImagePoint(point.X + left, point.Y + top)).ToImmutableArray()).ToImmutableArray();
                        if (region is not null && (!ContainsContour(region, contour) || additional.Any(component => !ContainsContour(region, component))))
                        {
                            continue;
                        }
                        if (options.MaximumResults == 0 || found.Count < options.MaximumResults)
                        {
                            found.Add(detection with
                            {
                                Center = new(detection.Center.X + left, detection.Center.Y + top),
                                Contour = contour,
                                AdditionalContours = additional,
                                InnerContours = detection.InnerContours.Select(hole => hole.Select(point =>
                                    new ImagePoint(point.X + left, point.Y + top)).ToImmutableArray()).ToImmutableArray()
                            });
                        }
                        else
                        {
                            incomplete = true;
                        }
                    }
                }
                batch.Clear();
                detectionWallTicks += Stopwatch.GetTimestamp() - batchStart;
            }
        }, token);

    private sealed record DetectionWindow(ShapeGrayImage Pixels, int X, int Y, int Left, int Top);

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
