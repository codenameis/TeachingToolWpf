using System.Diagnostics;
using TeachingToolWpf.Core.Imaging;

namespace TeachingToolWpf.Imaging.Filters;

public sealed class RoiFilterPreview(IImageLoader loader, Func<VideoFrame?> retainFrame) : IRoiFilterPreview
{
    public const int MaximumEdge = 1024;
    public const int PixelBudget = 4 * 1024 * 1024;

    public Task<FilterPreviewResult> ProcessAsync(FilterRequest request, CancellationToken cancellationToken)
        => Task.Run(async () =>
        {
            request.Settings.Validate();
            var watch = Stopwatch.StartNew();
            using var frame = request.IsLive ? retainFrame() ?? throw new InvalidOperationException("라이브 프레임을 기다리고 있습니다.") : null;
            using var source = request.IsLive ? null : await loader.LoadAsync(request.Image, new Progress<string>(), cancellationToken).ConfigureAwait(false);
            var output = new List<FilteredRoi>();
            var edge = Math.Min(MaximumEdge, Math.Max(1, (int)Math.Sqrt(PixelBudget / (double)Math.Max(1, request.Rois.Count))));
            foreach (var roi in request.Rois)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var scale = Math.Min(1, edge / Math.Max(roi.Geometry.Width, roi.Geometry.Height));
                var width = Math.Max(1, (int)Math.Floor(roi.Geometry.Width * scale));
                var height = Math.Max(1, (int)Math.Floor(roi.Geometry.Height * scale));
                using var pixels = source is not null ? RoiPixelExtractor.FromTiles(source, roi.Geometry, width, height, cancellationToken)
                    : RoiPixelExtractor.FromFrame(frame!.Pixels, roi.Geometry, width, height);
                var bytes = OpenCvRoiFilter.Apply(pixels, source?.Info.Pixels ?? frame!.Pixels.Kind, request.Settings);
                output.Add(new(roi.Id, roi.Geometry, width, height, bytes));
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new FilterPreviewResult(output, watch.Elapsed.TotalMilliseconds);
        }, cancellationToken);
}
