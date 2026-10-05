using System.Collections.Immutable;
using System.Diagnostics;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Matching;

public sealed record TemplateTrainingOutput(CorrelationTemplate Model, double Milliseconds);
public sealed record TemplateSearchOutput(ImmutableArray<CorrelationMatch> Matches, int Windows, bool Truncated,
    double ReadMilliseconds, double TotalMilliseconds, bool MatchLimitExceeded = false);

public interface ITemplateMatchingService
{
    Task<TemplateTrainingOutput> TrainAsync(ImageReference image, bool live, RoiGeometry roi, CancellationToken token);
    Task<TemplateSearchOutput> SearchAsync(ImageReference image, bool live, CorrelationTemplate model, RoiGeometry? region, SearchOptions options, CancellationToken token);
}

public sealed class TemplateMatchingService(IImageLoader loader, Func<VideoFrame?> retainFrame) : ITemplateMatchingService
{
    public Task<TemplateTrainingOutput> TrainAsync(ImageReference image, bool live, RoiGeometry roi, CancellationToken token)
        => Task.Run(async () =>
        {
            ShapeMatchingService.ValidateRegion(image, roi);
            var timer = Stopwatch.StartNew();
            using var reader = await MatchingImageReader.OpenAsync(image, live, loader, retainFrame, token).ConfigureAwait(false);
            var left = Math.Max(0, (int)Math.Floor(roi.Bounds.Left));
            var top = Math.Max(0, (int)Math.Floor(roi.Bounds.Top));
            var right = Math.Min(image.Width, (int)Math.Ceiling(roi.Bounds.Right));
            var bottom = Math.Min(image.Height, (int)Math.Ceiling(roi.Bounds.Bottom));
            var pixels = reader.Read(left, top, right - left, bottom - top, token, CorrelationTemplate.MaximumTemplatePixels);
            var mask = new byte[checked(pixels.Width * pixels.Height)];
            for (var y = 0; y < pixels.Height; y++)
            {
                token.ThrowIfCancellationRequested();
                for (var x = 0; x < pixels.Width; x++)
                {
                    mask[(y * pixels.Width) + x] = roi.Contains(new PointD(left + x + .5, top + y + .5)) ? (byte)255 : (byte)0;
                }
            }
            var model = CorrelationTemplate.Train(pixels, new(mask, new(roi.Center.X - left, roi.Center.Y - top), roi.Angle * Math.PI / 180), token);
            return new TemplateTrainingOutput(model, timer.Elapsed.TotalMilliseconds);
        }, token);

    public Task<TemplateSearchOutput> SearchAsync(ImageReference image, bool live, CorrelationTemplate model,
        RoiGeometry? region, SearchOptions options, CancellationToken token)
        => Task.Run(async () =>
        {
            options.Validate();
            if (options.ComputeMode != InspectionComputeMode.Cpu)
            {
                throw new NotSupportedException("OpenCV 템플릿 매칭은 현재 CPU 실행만 지원합니다.");
            }
            if (region is not null)
            {
                ShapeMatchingService.ValidateRegion(image, region);
            }
            var timer = Stopwatch.StartNew();
            using var reader = await MatchingImageReader.OpenAsync(image, live, loader, retainFrame, token).ConfigureAwait(false);
            var matches = new PriorityQueue<CorrelationMatch, (double Score, double Y, double X)>();
            var windows = 0;
            var truncated = false;
            var readMilliseconds = timer.Elapsed.TotalMilliseconds;
            var bounds = region?.Bounds ?? new BoundsD(0, 0, image.Width, image.Height);
            var firstX = Math.Max(0, (int)Math.Ceiling(bounds.Left - model.ReferencePoint.X));
            var firstY = Math.Max(0, (int)Math.Ceiling(bounds.Top - model.ReferencePoint.Y));
            var lastX = Math.Min(image.Width - model.Width, (int)Math.Floor(bounds.Right - model.ReferencePoint.X));
            var lastY = Math.Min(image.Height - model.Height, (int)Math.Floor(bounds.Bottom - model.ReferencePoint.Y));
            var interiorWidth = Math.Min(2048, Math.Max(1, lastX - firstX + 1));
            var interiorHeight = Math.Min(2048, (CorrelationTemplate.MaximumSearchPixels / (model.Width + interiorWidth - 1)) - model.Height + 1);
            if (interiorHeight < 1)
            {
                throw new ArgumentException("템플릿과 검색 창이 작업 메모리 예산을 초과합니다.");
            }
            for (var y = firstY; y <= lastY; y += interiorHeight)
            {
                for (var x = firstX; x <= lastX; x += interiorWidth)
                {
                    token.ThrowIfCancellationRequested();
                    var width = Math.Min(interiorWidth, lastX - x + 1) + model.Width - 1;
                    var height = Math.Min(interiorHeight, lastY - y + 1) + model.Height - 1;
                    var readTimer = Stopwatch.StartNew();
                    var pixels = reader.Read(x, y, width, height, token, CorrelationTemplate.MaximumSearchPixels);
                    readMilliseconds += readTimer.Elapsed.TotalMilliseconds;
                    var result = CorrelationMatcher.FindCandidates(pixels, model, options with
                    {
                        Region = ShapeMatchingService.ToSearchRegion(region, x, y),
                        MaximumMatches = Math.Min(1000, options.MaximumCandidates)
                    }, token);
                    windows++;
                    truncated |= result.Truncated;
                    foreach (var match in result.Matches)
                    {
                        var global = match with
                        {
                            ReferencePoint = new(match.ReferencePoint.X + x, match.ReferencePoint.Y + y)
                        };
                        matches.Enqueue(global, (global.Score, -global.ReferencePoint.Y, -global.ReferencePoint.X));
                        if (matches.Count > options.MaximumCandidates)
                        {
                            matches.Dequeue();
                            truncated = true;
                        }
                    }
                }
            }
            var resultSummary = CorrelationMatcher.SuppressWithDiagnostics(model, matches.UnorderedItems.Select(value => value.Element), options);
            return new TemplateSearchOutput(resultSummary.Matches, windows, truncated,
                readMilliseconds, timer.Elapsed.TotalMilliseconds, resultSummary.MatchLimitExceeded);
        }, token);
}
