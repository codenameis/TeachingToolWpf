using System.Diagnostics;
using IndustrialVision.Filters.Core;
using VisionCanvas.Core.Editing;
using VisionImaging.Abstractions;

namespace VisionCanvas.Imaging.Filters;

public sealed class RoiFilterPreview(IImageLoader loader, Func<VideoFrame?> retainFrame,
    Func<IGpuBackend>? createGpuBackend = null) : IRoiFilterPreview, IDisposable
{
    public const int MaximumEdge = 1024;
    public const int PixelBudget = 4 * 1024 * 1024;
    private readonly IndustrialRoiFilter _industrial = new(createGpuBackend);
    private int _busy;
    private bool _disposed;

    public Task<FilterPreviewResult> ProcessAsync(FilterRequest request, CancellationToken cancellationToken)
        => Task.Run(async () =>
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                throw new InvalidOperationException("ROI filter preview allows one worker at a time.");
            }
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                foreach (var roi in request.Rois)
                {
                    request.Settings(roi).Validate();
                }

                var watch = Stopwatch.StartNew();
                using var frame = request.IsLive ? retainFrame() ?? throw new InvalidOperationException("라이브 프레임을 기다리고 있습니다.") : null;
                using var source = request.IsLive ? null : await loader.LoadAsync(request.Image, new Progress<string>(), cancellationToken).ConfigureAwait(false);
                var output = new List<FilteredRoi>();
                var edge = Math.Min(MaximumEdge, Math.Max(1, (int)Math.Sqrt(PixelBudget / (double)Math.Max(1, request.Rois.Count))));
                foreach (var roi in request.Rois)
                {
                    var settings = request.Settings(roi);
                    if (settings.Kind == RoiFilterKind.None)
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
                    var kind = source?.Info.Pixels ?? frame!.Pixels.Kind;
                    var mask = RoiFilterMask.Create(geometry, width, height, cancellationToken);
                    if (settings.IsIndustrial)
                    {
                        var filtered = _industrial.Apply(pixels, kind, settings, cancellationToken);
                        output.Add(new(roi.Id, geometry, width, height, filtered.Pixels, mask)
                        {
                            Execution = filtered.Report,
                            PreparationMilliseconds = filtered.PreparationMilliseconds,
                            ReusedPlan = filtered.ReusedPlan
                        });
                    }
                    else
                    {
                        var bytes = OpenCvRoiFilter.Apply(pixels, kind, settings);
                        output.Add(new(roi.Id, geometry, width, height, bytes, mask));
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                return new FilterPreviewResult(output, watch.Elapsed.TotalMilliseconds);
            }
            finally
            {
                Volatile.Write(ref _busy, 0);
            }
        }, cancellationToken);

    /// <summary>Call only after the preview worker completes; the optional GPU backend remains host-owned.</summary>
    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            throw new InvalidOperationException("Wait for the preview worker before disposing its buffers.");
        }
        try
        {
            _disposed = true;
            _industrial.Dispose();
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }
}
