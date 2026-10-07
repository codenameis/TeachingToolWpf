using System.Runtime.InteropServices;
using IndustrialVision.Filters.Gpu;
using VisionCanvas.Core.Editing;
using VisionCanvas.Imaging.Filters;
using VisionImaging;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Tests;

public sealed class IndustrialFilterCudaTests
{
    [FilterCudaFact]
    public async Task EveryIndustrialRoiFilterRunsOnActualCudaWithOneUploadAndDownload()
    {
        var pixels = new PixelBuffer(63, 47, PixelKind.Gray16);
        var values = MemoryMarshal.Cast<byte, ushort>(pixels.Span);
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = (ushort)(i * 127L % 65536);
        }
        using var frame = new VideoFrame(1, 1, pixels);
        using var gpu = new CudaBackend();
        using var service = new RoiFilterPreview(new ImageLoader("unused"), frame.Retain, () => gpu);
        foreach (var kind in Enum.GetValues<RoiFilterKind>().Where(kind => new FilterSettings(kind).IsIndustrial))
        {
            var roi = new Roi(Guid.NewGuid(), "CUDA", new(new(31.5, 23.5), 63, 47, 0), null, false, 1)
            {
                Filter = new(kind, Threshold: 2000, Radius: 2, Sigma: 1.2)
            };
            var request = new FilterRequest(new("live", "live", 63, 47), true, [roi]);
            var cpu = Assert.Single((await service.ProcessAsync(request, default)).Regions);
            roi = roi with
            {
                Filter = roi.Filter with
                {
                    Execution = RoiFilterExecution.CpuGpu
                }
            };
            request = request with
            {
                Rois = [roi]
            };
            var cuda = Assert.Single((await service.ProcessAsync(request, default)).Regions);
            Assert.All(cuda.Execution!.Stages, stage => Assert.Equal(gpu.GetCapabilities().Device, stage.Device));
            Assert.Equal(63L * 47 * 4, cuda.Execution.UploadBytes);
            Assert.Equal(63L * 47 * 4, cuda.Execution.DownloadBytes);
            var tolerance = kind == RoiFilterKind.Gaussian ? 1 : 0;
            for (var i = 0; i < cpu.Gray8.Length; i++)
            {
                Assert.InRange(Math.Abs(cpu.Gray8[i] - cuda.Gray8[i]), 0, tolerance);
            }
            var repeated = Assert.Single((await service.ProcessAsync(request, default)).Regions);
            Assert.True(repeated.ReusedPlan);
            Assert.Equal(cuda.Gray8, repeated.Gray8);
            if (kind == RoiFilterKind.GaussianBlackHatThreshold)
            {
                Assert.Equal(3, cuda.Execution.Stages.Count);
                Assert.Contains((byte)255, cuda.Gray8);
                Assert.Contains((byte)0, cuda.Gray8);
            }
        }
    }
}

public sealed class FilterCudaFactAttribute : FactAttribute
{
    public FilterCudaFactAttribute()
    {
        if (CudaSmoke.Run().Status != "PASS")
        {
            Skip = "No actual CUDA device; emulator is not GPU validation.";
        }
    }
}
