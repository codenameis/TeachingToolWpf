using System.Diagnostics;
using VisionCanvas.Core.Editing;
using VisionImaging.Abstractions;

namespace VisionCanvas.Imaging.Filters;

public sealed class RoiFilterPreview(IImageLoader loader, Func<VideoFrame?> retainFrame) : IRoiFilterPreview
{
    public const int MaximumEdge = 1024;
    public const int PixelBudget = 4 * 1024 * 1024;

    public Task<FilterPreviewResult> ProcessAsync(FilterRequest request, CancellationToken cancellationToken)
        => Task.Run(async () =>
        {
            foreach (var roi in request.Rois)
            {
                roi.Filter.Validate();
            }

            var watch = Stopwatch.StartNew();
            using var frame = request.IsLive ? retainFrame() ?? throw new InvalidOperationException("라이브 프레임을 기다리고 있습니다.") : null;
            using var source = request.IsLive ? null : await loader.LoadAsync(request.Image, new Progress<string>(), cancellationToken).ConfigureAwait(false);
            var output = new List<FilteredRoi>();
            var edge = Math.Min(MaximumEdge, Math.Max(1, (int)Math.Sqrt(PixelBudget / (double)Math.Max(1, request.Rois.Count))));
            foreach (var roi in request.Rois)
            {
                if (roi.Filter.Kind == RoiFilterKind.None)
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var geometry = request.Geometry(roi);
                var scale = Math.Min(1, edge / Math.Max(geometry.Width, geometry.Height));
                var width = Math.Max(1, (int)Math.Floor(geometry.Width * scale));
                var height = Math.Max(1, (int)Math.Floor(geometry.Height * scale));
                using var pixels = source is not null ? RoiPixelExtractor.FromTiles(source, geometry, width, height, cancellationToken)
                    : RoiPixelExtractor.FromFrame(frame!.Pixels, geometry, width, height);
                var bytes = OpenCvRoiFilter.Apply(pixels, source?.Info.Pixels ?? frame!.Pixels.Kind, roi.Filter);
                output.Add(new(roi.Id, geometry, width, height, bytes, RoiFilterMask.Create(geometry, width, height, cancellationToken)));
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new FilterPreviewResult(output, watch.Elapsed.TotalMilliseconds);
        }, cancellationToken);
}
