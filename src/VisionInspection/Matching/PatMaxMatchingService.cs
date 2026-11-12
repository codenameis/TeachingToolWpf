using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using VisionAlgorithm.OpenSrc.PatMax;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Matching;

public sealed class PatMaxMatchingService(
    IImageLoader loader,
    Func<VideoFrame?> retainFrame,
    IPreInspectionFilterPipeline? preFilterPipeline = null) : IPatMaxMatchingService, IDisposable
{
    private const int MaxPixelsBudget = 67_108_864;
    private const int ReaderWindowBudgetPixels = VisionAlgorithm.OpenSrc.PatternMatching.PreparedSearchImage.MaximumPixels;
    private const int SearchCropSafetyMarginPixels = 8;
    private const int MinTemplateCropPaddingPixels = 4;
    private const int MaxRequestedMatchesLimit = 1024;
    private const int MaxPreviewAtlasDimension = 1024;
    private const double PixelCenterOffset = 0.5;
    private const double DegreesToRadians = Math.PI / 180.0;
    private const double RadiansToDegrees = 180.0 / Math.PI;

    private readonly ConcurrentBag<PatMaxEngine> _enginePool = [];
    private readonly MatchingPreviewCache _previewCache = new();
    private readonly SearchImageCache _searchImageCache = new();
    private bool _disposed;

    public Task<PatMaxTrainedTemplate> TrainAsync(
        ImageReference image,
        bool live,
        RoiGeometry roi,
        PatMaxTrainParams trainParams,
        CancellationToken token,
        RoiExclusion? exclusion = null)
        => Task.Run(() => BuildTemplateAsync(image, live, roi, trainParams, token, exclusion, isPreviewOnly: false), token);

    public Task<PatMaxTrainedTemplate?> PreviewAsync(
        ImageReference image,
        bool live,
        RoiGeometry roi,
        PatMaxTrainParams trainParams,
        CancellationToken token,
        RoiExclusion? exclusion = null)
        => Task.Run<PatMaxTrainedTemplate?>(async () =>
        {
            var template = await BuildTemplateAsync(image, live, roi, trainParams, token, exclusion, isPreviewOnly: true).ConfigureAwait(false);
            return template;
        }, token);

    private async Task<PatMaxTrainedTemplate> BuildTemplateAsync(
        ImageReference image,
        bool live,
        RoiGeometry roi,
        PatMaxTrainParams trainParams,
        CancellationToken token,
        RoiExclusion? exclusion,
        bool isPreviewOnly)
    {
        MatchingRegionValidator.ValidateTeachingRegion(image, roi);
        var sw = Stopwatch.StartNew();

        var pad = Math.Max(MinTemplateCropPaddingPixels, trainParams.CoarseGranularity * 2);
        var crop = MatchingCropWindow.Expand(image, roi.Bounds, pad);
        var left = crop.Left;
        var top = crop.Top;
        var cropW = crop.Width;
        var cropH = crop.Height;

        VisionAlgorithm.ShapeGrayImage gray;
        if ((long)cropW * cropH <= ReaderWindowBudgetPixels)
        {
            gray = await _previewCache.ReadAsync(
                image,
                live,
                loader,
                retainFrame,
                left,
                top,
                cropW,
                cropH,
                null,
                token,
                ReaderWindowBudgetPixels,
                preferDirectGray: true,
                preFilterPipeline: preFilterPipeline).ConfigureAwait(false);
        }
        else
        {
            using var reader = await MatchingImageReader.OpenAsync(image, live, loader, retainFrame, token, preFilterPipeline).ConfigureAwait(false);
            reader.PreferDirectGray = true;
            gray = ReadChunkedGrayImage(reader, left, top, cropW, cropH, token);
        }

        var excludedMask = BuildExcludedMaskFast(cropW, cropH, left, top, roi, exclusion, token);
        var readMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        var originX = (float)(roi.Center.X - left);
        var originY = (float)(roi.Center.Y - top);
        var model = isPreviewOnly
            ? PatMaxTrainer.TrainPreview(
                gray.Pixels.Span,
                gray.Width,
                gray.Height,
                gray.Stride,
                in trainParams,
                originX,
                originY,
                excludedMask,
                token)
            : PatMaxTrainer.Train(
                gray.Pixels.Span,
                gray.Width,
                gray.Height,
                gray.Stride,
                in trainParams,
                originX,
                originY,
                excludedMask,
                token);

        var (filteredPreviewBytes, previewW, previewH, previewMask) = BuildRectifiedFilterPreview(
            gray.Pixels.Span,
            gray.Width,
            gray.Height,
            gray.Stride,
            left,
            top,
            roi,
            in trainParams,
            token);
        var rawPreviewPatch = RoiPreviewPatchExtractor.ExtractRectifiedPatch(
            gray.Pixels.Span,
            gray.Width,
            gray.Height,
            gray.Stride,
            left,
            top,
            roi,
            exclusion);
        var trainMs = sw.Elapsed.TotalMilliseconds;

        var roiAngleRad = roi.Angle * DegreesToRadians;
        var (previewPoints, previewNormals) = ExtractUnrotatedPreviewGeometry(model.FineLevel.Dipoles, roiAngleRad);
        var (coarsePreviewPoints, _) = ExtractUnrotatedPreviewGeometry(
            model.CoarseLevel.Dipoles,
            roiAngleRad);
        return new PatMaxTrainedTemplate(
            model,
            roi,
            trainParams,
            previewPoints,
            previewNormals,
            coarsePreviewPoints,
            readMs,
            trainMs,
            filteredPreviewBytes,
            previewW,
            previewH,
            previewMask,
            rawPreviewPatch.Pixels,
            rawPreviewPatch.Width,
            rawPreviewPatch.Height);
    }

    public Task<PatMaxSearchOutput> SearchAsync(
        ImageReference image,
        bool live,
        PatMaxTrainedTemplate template,
        RoiGeometry? searchRegion,
        PatMaxSearchParams searchParams,
        CancellationToken token,
        RoiExclusion? exclusion = null)
        => Task.Run(async () =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (searchRegion is not null)
            {
                MatchingRegionValidator.ValidateSearchRegion(image, searchRegion);
            }

            var totalSw = Stopwatch.StartNew();
            var readSw = Stopwatch.StartNew();

            var bounds = searchRegion?.Bounds ?? new BoundsD(0, 0, image.Width, image.Height);
            var w = template.TeachingRegion.Width;
            var h = template.TeachingRegion.Height;
            var pad = (int)Math.Ceiling(0.5 * Math.Sqrt((w * w) + (h * h)) * Math.Max(1.0, searchParams.ScaleMax)) + SearchCropSafetyMarginPixels;
            var searchWindow = MatchingCropWindow.Expand(image, bounds, pad);
            var left = searchWindow.Left;
            var top = searchWindow.Top;
            var searchW = searchWindow.Width;
            var searchH = searchWindow.Height;

            VisionAlgorithm.ShapeGrayImage gray;
            ImmutableArray<PreInspectionFilterStep> activeFilterSteps = preFilterPipeline?.ActiveSteps ?? [];
            if (!live && _searchImageCache.TryGet(image, activeFilterSteps, searchWindow, out var cachedGray))
            {
                gray = cachedGray;
            }
            else if (!live && (long)searchW * searchH <= ReaderWindowBudgetPixels)
            {
                gray = await _previewCache.ReadAsync(
                    image,
                    live,
                    loader,
                    retainFrame,
                    left,
                    top,
                    searchW,
                    searchH,
                    null,
                    token,
                    ReaderWindowBudgetPixels,
                    preferDirectGray: true,
                    preFilterPipeline: preFilterPipeline).ConfigureAwait(false);
            }
            else
            {
                using var reader = await MatchingImageReader.OpenAsync(image, live, loader, retainFrame, token, preFilterPipeline).ConfigureAwait(false);
                reader.PreferDirectGray = true;
                gray = ReadChunkedGrayImage(reader, left, top, searchW, searchH, token);
                if (!live)
                {
                    _searchImageCache.Store(image, activeFilterSteps, searchWindow, gray);
                }
            }

            var readMs = readSw.Elapsed.TotalMilliseconds;

            var searchSw = Stopwatch.StartNew();
            var hasSpatialFilter = searchRegion is not null || exclusion is not null;
            var maxReq = hasSpatialFilter
                ? Math.Clamp(Math.Max(searchParams.MaxResults * 2, searchParams.MaxCandidates), 1, MaxRequestedMatchesLimit)
                : Math.Clamp(searchParams.MaxResults + 1, 1, MaxRequestedMatchesLimit);
            var engineSearchParams = searchParams.WithCapacity(maxReq);
            var rawResults = new PatMaxResult[maxReq];
            var foundCount = ExecutePooledSearch(
                template.Model,
                gray.Pixels.Span,
                gray.Width,
                gray.Height,
                gray.Stride,
                in engineSearchParams,
                rawResults,
                token);
            var searchMs = searchSw.Elapsed.TotalMilliseconds;

            var baseAngleDeg = template.TeachingRegion.Angle;
            var matches = ImmutableArray.CreateBuilder<PatMaxSearchMatch>(Math.Min(foundCount, searchParams.MaxResults));
            var limitExceeded = false;

            for (var i = 0; i < foundCount; i++)
            {
                var r = rawResults[i];
                var worldX = left + r.X;
                var worldY = top + r.Y;
                var pt = new PointD(worldX, worldY);
                if (searchRegion is not null && !searchRegion.Contains(pt))
                {
                    continue;
                }

                if (exclusion?.Contains(pt) == true)
                {
                    continue;
                }

                if (matches.Count >= searchParams.MaxResults)
                {
                    limitExceeded = true;
                    break;
                }

                var absAngle = RoiGeometry.NormalizeAngle(baseAngleDeg + (r.AngleRadians * RadiansToDegrees));
                matches.Add(new PatMaxSearchMatch(
                    X: worldX,
                    Y: worldY,
                    AngleDegrees: absAngle,
                    Scale: r.Scale,
                    AspectRatio: r.AspectRatio,
                    Score: r.Score,
                    Coverage: r.Coverage,
                    Clutter: r.Clutter,
                    MatchedDipoles: r.MatchedDipoleCount,
                    Iterations: r.IterationCount));
            }

            return new PatMaxSearchOutput(
                matches.ToImmutable(),
                readMs,
                searchMs,
                totalSw.Elapsed.TotalMilliseconds,
                limitExceeded);
        }, token);

    private static (byte[]? Gray8, int Width, int Height, byte[]? Mask) BuildRectifiedFilterPreview(
        ReadOnlySpan<byte> cropPixels,
        int cropWidth,
        int cropHeight,
        int cropStride,
        int cropLeft,
        int cropTop,
        RoiGeometry roi,
        in PatMaxTrainParams trainParams,
        CancellationToken token)
    {
        if (trainParams.PreprocessingFilter == PatMaxPreprocessingFilterKind.None)
        {
            var rawPatch = RoiPreviewPatchExtractor.ExtractRectifiedPatch(
                cropPixels,
                cropWidth,
                cropHeight,
                cropStride,
                cropLeft,
                cropTop,
                roi);
            return (rawPatch.Pixels, rawPatch.Width, rawPatch.Height, null);
        }

        var maxDim = Math.Max(roi.Width, roi.Height);
        var scale = Math.Min(1.0, MaxPreviewAtlasDimension / Math.Max(1.0, maxDim));
        var dstW = Math.Clamp((int)Math.Round(roi.Width * scale), 1, MaxPreviewAtlasDimension);
        var dstH = Math.Clamp((int)Math.Round(roi.Height * scale), 1, MaxPreviewAtlasDimension);
        var totalCrop = checked(cropWidth * cropHeight);
        var filteredCropPool = ArrayPool<byte>.Shared.Rent(totalCrop);

        try
        {
            var filteredCrop = filteredCropPool.AsSpan(0, totalCrop);
            PatMaxInspectionFilter.Apply(
                cropPixels,
                cropWidth,
                cropHeight,
                cropStride,
                filteredCrop,
                trainParams.PreprocessingFilter,
                trainParams.FineGranularity,
                trainParams.FilterSigma,
                trainParams.EdgeThreshold,
                forDisplayPreview: true);

            var rectified = new byte[checked(dstW * dstH)];
            var mask = roi.Shape.Kind == RoiShapeKind.Rectangle ? null : new byte[checked(dstW * dstH)];

            var cx = roi.Center.X - cropLeft;
            var cy = roi.Center.Y - cropTop;
            var rad = roi.Angle * DegreesToRadians;
            var cos = Math.Cos(rad);
            var sin = Math.Sin(rad);
            var stepX = roi.Width / dstW;
            var stepY = roi.Height / dstH;
            var startLocalX = (-0.5 * roi.Width) + (0.5 * stepX);
            var startLocalY = (-0.5 * roi.Height) + (0.5 * stepY);

            for (var y = 0; y < dstH; y++)
            {
                token.ThrowIfCancellationRequested();
                var ly = startLocalY + (y * stepY);
                var rowOffset = y * dstW;
                for (var x = 0; x < dstW; x++)
                {
                    var lx = startLocalX + (x * stepX);
                    if (mask is not null && roi.Shape.ContainsLocal(new PointD(lx, ly), roi.Width, roi.Height, 0))
                    {
                        mask[rowOffset + x] = byte.MaxValue;
                    }

                    var sx = cx + (lx * cos) - (ly * sin) - PixelCenterOffset;
                    var sy = cy + (lx * sin) + (ly * cos) - PixelCenterOffset;
                    var ix = Math.Clamp((int)Math.Floor(sx), 0, cropWidth - 1);
                    var iy = Math.Clamp((int)Math.Floor(sy), 0, cropHeight - 1);
                    var ix1 = Math.Min(cropWidth - 1, ix + 1);
                    var iy1 = Math.Min(cropHeight - 1, iy + 1);
                    var fx = Math.Clamp(sx - ix, 0.0, 1.0);
                    var fy = Math.Clamp(sy - iy, 0.0, 1.0);

                    var p00 = filteredCrop[(iy * cropWidth) + ix];
                    var p10 = filteredCrop[(iy * cropWidth) + ix1];
                    var p01 = filteredCrop[(iy1 * cropWidth) + ix];
                    var p11 = filteredCrop[(iy1 * cropWidth) + ix1];

                    var topVal = p00 + ((p10 - p00) * fx);
                    var botVal = p01 + ((p11 - p01) * fx);
                    var val = (int)Math.Round(topVal + ((botVal - topVal) * fy));
                    rectified[rowOffset + x] = (byte)Math.Clamp(val, byte.MinValue, byte.MaxValue);
                }
            }

            return (rectified, dstW, dstH, mask);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(filteredCropPool);
        }
    }

    private static VisionAlgorithm.ShapeGrayImage ReadChunkedGrayImage(
        MatchingImageReader reader,
        int left,
        int top,
        int width,
        int height,
        CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);
        if ((long)width * height > MaxPixelsBudget)
        {
            throw new ArgumentException($"PatMax 검색 창이 {MaxPixelsBudget:N0} pixel 작업 예산을 초과했습니다.", nameof(width));
        }

        if ((long)width * height <= ReaderWindowBudgetPixels)
        {
            return reader.Read(left, top, width, height, token, ReaderWindowBudgetPixels);
        }

        var pixels = new byte[width * height];
        var chunkHeight = Math.Max(1, ReaderWindowBudgetPixels / width);
        for (var y = 0; y < height; y += chunkHeight)
        {
            token.ThrowIfCancellationRequested();
            var rows = Math.Min(chunkHeight, height - y);
            var chunk = reader.Read(left, top + y, width, rows, token, ReaderWindowBudgetPixels);
            for (var r = 0; r < rows; r++)
            {
                chunk.Pixels.Span.Slice(r * chunk.Stride, width).CopyTo(pixels.AsSpan((y + r) * width, width));
            }
        }

        return new VisionAlgorithm.ShapeGrayImage(pixels, width, height, width);
    }

    internal static byte[] BuildExcludedMaskFast(
        int width,
        int height,
        int left,
        int top,
        RoiGeometry roi,
        RoiExclusion? exclusion,
        CancellationToken token)
    {
        var excluded = new byte[width * height];
        var cx = roi.Center.X;
        var cy = roi.Center.Y;
        var halfW = roi.Width * PixelCenterOffset;
        var halfH = roi.Height * PixelCenterOffset;
        var angleRad = -roi.Angle * DegreesToRadians;
        var cos = Math.Cos(angleRad);
        var sin = Math.Sin(angleRad);
        var hasExclusion = exclusion is not null;
        var isNonRectangularShape = roi.Shape.Kind != RoiShapeKind.Rectangle;

        for (var y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            var wy = top + y + PixelCenterOffset;
            var dy = wy - cy;
            var rowOffset = y * width;
            for (var x = 0; x < width; x++)
            {
                var wx = left + x + PixelCenterOffset;
                var dx = wx - cx;
                var localX = (dx * cos) - (dy * sin);
                var localY = (dx * sin) + (dy * cos);
                if (Math.Abs(localX) > halfW || Math.Abs(localY) > halfH)
                {
                    excluded[rowOffset + x] = byte.MaxValue;
                    continue;
                }

                if (isNonRectangularShape && !roi.Shape.ContainsLocal(new PointD(localX, localY), roi.Width, roi.Height, 0))
                {
                    excluded[rowOffset + x] = byte.MaxValue;
                    continue;
                }

                if (hasExclusion && exclusion!.Contains(new PointD(wx, wy)))
                {
                    excluded[rowOffset + x] = byte.MaxValue;
                }
            }
        }

        return excluded;
    }

    private static (ImmutableArray<PointD> Points, ImmutableArray<PointD> Normals) ExtractUnrotatedPreviewGeometry(
        EdgeDipoleSoA dipoles,
        double roiAngleRadians,
        double positionScale = 1.0)
    {
        if (dipoles.IsEmpty)
        {
            return ([], []);
        }

        var cos = Math.Cos(-roiAngleRadians);
        var sin = Math.Sin(-roiAngleRadians);
        var xSpan = dipoles.X;
        var ySpan = dipoles.Y;
        var cosSpan = dipoles.CosTheta;
        var sinSpan = dipoles.SinTheta;
        var points = ImmutableArray.CreateBuilder<PointD>(dipoles.Count);
        var normals = ImmutableArray.CreateBuilder<PointD>(dipoles.Count);
        for (var i = 0; i < dipoles.Count; i++)
        {
            var dx = xSpan[i] * positionScale;
            var dy = ySpan[i] * positionScale;
            points.Add(new PointD((dx * cos) - (dy * sin), (dx * sin) + (dy * cos)));

            var nx = cosSpan[i];
            var ny = sinSpan[i];
            normals.Add(new PointD((nx * cos) - (ny * sin), (nx * sin) + (ny * cos)));
        }

        return (points.MoveToImmutable(), normals.MoveToImmutable());
    }

    private int ExecutePooledSearch(
        PatMaxModel model,
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        int stride,
        in PatMaxSearchParams searchParams,
        Span<PatMaxResult> results,
        CancellationToken token)
    {
        if (!_enginePool.TryTake(out var engine))
        {
            engine = new PatMaxEngine();
        }

        try
        {
            return engine.Find(model, pixels, width, height, stride, in searchParams, results, token);
        }
        finally
        {
            if (_disposed)
            {
                engine.Dispose();
            }
            else
            {
                _enginePool.Add(engine);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        while (_enginePool.TryTake(out var engine))
        {
            engine.Dispose();
        }
    }
}
