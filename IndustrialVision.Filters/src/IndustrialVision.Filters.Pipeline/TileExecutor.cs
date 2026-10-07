using IndustrialVision.Filters.Core;

namespace IndustrialVision.Filters.Pipeline;

public sealed record TileExecutionReport(int Tiles, int Halo, int TileWidth, int TileHeight,
    long PeakHostBytes, long PeakGpuBytes, long UploadBytes, long DownloadBytes, IReadOnlyList<StagePlan> Stages);

/// <summary>지역 연산 전용. halo를 역방향 누적하고 실제 영상 경계에서 각 필터의 replicate를 적용한다.</summary>
public sealed class TileExecutor(FilterEngine engine)
{
    public TileExecutionReport Execute(Recipe recipe, IImageTileSource source, IImageTileSink sink,
        ExecutionOptions options, int tileWidth = 512, int tileHeight = 256, ImageRegion? roi = null,
        RoiBoundaryMode boundaryMode = RoiBoundaryMode.SourceContext, CancellationToken token = default)
    {
        var descriptor = source.Descriptor;
        RecipeValidation.Validate(recipe, descriptor, options);
        if (tileWidth < 1 || tileHeight < 1 || !Enum.IsDefined(boundaryMode))
        {
            throw new ArgumentOutOfRangeException(nameof(tileWidth));
        }
        var target = roi ?? new ImageRegion(0, 0, descriptor.Width, descriptor.Height);
        descriptor.Region(target.X, target.Y, target.Width, target.Height);
        var context = boundaryMode == RoiBoundaryMode.Isolated ? target : new ImageRegion(0, 0, descriptor.Width, descriptor.Height);
        var halo = RecipeValidation.RequiredHalo(recipe);
        var retainedCalibrationBytes = RecipeValidation.CalibrationBytes(recipe);
        // 작은 타일도 만족하지 못하면 명시적으로 실패한다. 의미/정확도는 바꾸지 않는다.
        while (!Fits(recipe, descriptor, options, tileWidth, tileHeight, halo, retainedCalibrationBytes))
        {
            if (tileWidth == 1 && tileHeight == 1)
            {
                throw new MemoryBudgetException("The accumulated halo cannot fit even a 1x1 output tile.");
            }
            if (tileWidth >= tileHeight)
            {
                tileWidth = Math.Max(1, tileWidth / 2);
            }
            else
            {
                tileHeight = Math.Max(1, tileHeight / 2);
            }
        }
        var tiles = 0;
        long peakHost = 0;
        long peakGpu = 0;
        long uploaded = 0;
        long downloaded = 0;
        var stages = new List<StagePlan>();
        try
        {
            for (var y = target.Y; y < target.Y + target.Height;)
            {
                var height = Math.Min(tileHeight, target.Y + target.Height - y);
                for (var x = target.X; x < target.X + target.Width;)
                {
                    token.ThrowIfCancellationRequested();
                    var width = Math.Min(tileWidth, target.X + target.Width - x);
                    var left = (int)Math.Max(context.X, (long)x - halo);
                    var top = (int)Math.Max(context.Y, (long)y - halo);
                    var right = (int)Math.Min((long)context.X + context.Width, (long)x + width + halo);
                    var bottom = (int)Math.Min((long)context.Y + context.Height, (long)y + height + halo);
                    var region = new ImageRegion(left, top, right - left, bottom - top);
                    var tileDescriptor = new ImageDescriptor(region.Width, region.Height, descriptor.Format, validBits: descriptor.ValidBits,
                        originX: descriptor.OriginX + left, originY: descriptor.OriginY + top, exposureKey: descriptor.ExposureKey,
                        pixelSizeMicrometers: descriptor.PixelSizeMicrometers);
                    using var input = new HostImage(tileDescriptor);
                    source.Read(region, input, token);
                    var tileRecipe = CropRecipe(recipe, tileDescriptor, region);
                    var retained = retainedCalibrationBytes + input.OwnedBytes;
                    using var plan = engine.Compile(tileRecipe, tileDescriptor, options with
                    {
                        MemoryBudgetBytes = options.MemoryBudgetBytes - retained
                    });
                    using var output = new HostImage(plan.Output);
                    var report = plan.Execute(input.View, output, token);
                    sink.Write(x - target.X, y - target.Y, output.View.Region(x - left, y - top, width, height), token);
                    foreach (var stage in report.Stages)
                    {
                        if (!stages.Contains(stage))
                        {
                            stages.Add(stage);
                        }
                    }
                    peakHost = Math.Max(peakHost, report.HostWorkingBytes + retained);
                    peakGpu = Math.Max(peakGpu, report.GpuWorkingBytes);
                    uploaded += report.UploadBytes;
                    downloaded += report.DownloadBytes;
                    tiles++;
                    x += width;
                }
                y += height;
            }
            token.ThrowIfCancellationRequested();
            sink.Complete();
        }
        catch
        {
            sink.Abort();
            throw;
        }
        return new(tiles, halo, tileWidth, tileHeight, peakHost, peakGpu, uploaded, downloaded, stages);
    }

    private static bool Fits(Recipe recipe, ImageDescriptor input, ExecutionOptions options, int width, int height, int halo, long retained)
    {
        var w = (int)Math.Min(input.Width, (long)width + (2L * halo));
        var h = (int)Math.Min(input.Height, (long)height + (2L * halo));
        var pixels = (long)w * h;
        if (pixels > 1 << 29)
        {
            return false;
        }
        var mapPlanes = recipe.Filters.OfType<FlatField>().Sum(f => f.Dark is null ? 1 : 2);
        var tile = new ImageDescriptor(w, h, input.Format);
        var cpu = Cpu.CpuProgram.EstimateBytes(recipe, tile, options);
        // CPU fallback까지 보수적으로 예약하고 crop 복사 임시 배열 한 장을 포함한다.
        var host = checked(retained + cpu + (pixels * (input.BytesPerPixel + 1L + 5 + ((mapPlanes + 1) * 4L))));
        var gpu = checked((pixels * (5L + mapPlanes) * 4) + recipe.Filters.OfType<Gaussian>().Sum(g => (long)((g.Radius * 2) + 1) * 4));
        return host <= options.MemoryBudgetBytes && (options.Mode == ExecutionMode.CpuOnly || gpu <= options.GpuMemoryBudgetBytes);
    }

    private static Recipe CropRecipe(Recipe recipe, ImageDescriptor descriptor, ImageRegion region)
    {
        var filters = new FilterSpec[recipe.Filters.Count];
        for (var i = 0; i < filters.Length; i++)
        {
            filters[i] = recipe.Filters[i] is FlatField flat
                ? new FlatField(CropMap(flat.Map, descriptor, region), flat.Kind,
                    flat.Dark is null ? null : CropMap(flat.Dark, descriptor, region), flat.Scale, flat.MinimumDenominator)
                : recipe.Filters[i];
        }
        return new(recipe.Version, filters);
    }

    private static CalibrationMap CropMap(CalibrationMap map, ImageDescriptor descriptor, ImageRegion region)
    {
        using var values = new PooledBuffer<float>(checked(region.Width * region.Height));
        for (var y = 0; y < region.Height; y++)
        {
            map.Values.Slice(((y + region.Y) * map.Descriptor.Width) + region.X, region.Width)
                .CopyTo(values.Array.AsSpan(y * region.Width, region.Width));
        }
        return new(descriptor, values.Array.AsSpan(0, values.Length), map.Version);
    }
}
