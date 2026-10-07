using System.Runtime.InteropServices;
using IndustrialVision.Filters.Core;
using VisionCanvas.Core.Editing;
using VisionCanvas.Imaging.Filters;
using VisionImaging;
using VisionImaging.Abstractions;

namespace VisionCanvas.Tests;

public sealed class IndustrialRoiFilterTests
{
    [Theory]
    [InlineData(PixelKind.Rgba32, 0)]
    [InlineData(PixelKind.Bgra32, 2)]
    public async Task ColorChannelOrderIsRespected(PixelKind kind, int redOffset)
    {
        var pixels = new PixelBuffer(2, 1, kind);
        pixels.Span.Clear();
        pixels.Span[redOffset] = 255;
        pixels.Span[4 + (2 - redOffset)] = 255;
        using var frame = new VideoFrame(1, 1, pixels);
        using var service = new RoiFilterPreview(new ImageLoader("unused"), frame.Retain);
        var roi = CreateRoi(2, 1, new(RoiFilterKind.NativeThreshold, Threshold: 50));
        var output = Assert.Single((await service.ProcessAsync(new(new("live", "live", 2, 1), true, [roi]), default)).Regions);
        Assert.Equal(new byte[] { 255, 0 }, output.Gray8);
    }

    [Fact]
    public async Task RotatedGray16CirclePreservesNativeIntensityAndOutputMask()
    {
        var pixels = new PixelBuffer(8, 8, PixelKind.Gray16);
        var values = MemoryMarshal.Cast<byte, ushort>(pixels.Span);
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = (ushort)(i % 8 < 4 ? 4095 : 4096);
        }
        using var frame = new VideoFrame(1, 1, pixels);
        using var service = new RoiFilterPreview(new ImageLoader("unused"), frame.Retain);
        var roi = CreateRoi(8, 8, new(RoiFilterKind.NativeThreshold, Threshold: 4095)) with
        {
            Geometry = new(new(4, 4), 8, 8, 90, VisionGeometry.RoiShape.Circle)
        };
        var output = Assert.Single((await service.ProcessAsync(new(new("live", "live", 8, 8), true, [roi]), default)).Regions);
        Assert.Equal(roi.Geometry, output.Geometry);
        Assert.All(output.Gray8.Take(8), value => Assert.Equal(255, value));
        Assert.All(output.Gray8.TakeLast(8), value => Assert.Equal(0, value));
        Assert.Equal(0, output.Mask![0]);
        Assert.Equal(255, output.Mask[(3 * 8) + 3]);
    }

    [Fact]
    public async Task Gray16ThresholdPreservesOneLevelDifferenceAndPlanReusesOnlyMatchingSettings()
    {
        var pixels = new PixelBuffer(9, 7, PixelKind.Gray16);
        var values = MemoryMarshal.Cast<byte, ushort>(pixels.Span);
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = (ushort)(4095 + (i % 2));
        }
        var before = pixels.Span.ToArray();
        using var frame = new VideoFrame(1, 1, pixels);
        var gpuCalls = 0;
        using var service = new RoiFilterPreview(new ImageLoader("unused"), frame.Retain, () =>
        {
            gpuCalls++;
            throw new GpuUnavailableException("test: no CUDA hardware");
        });
        var roi = CreateRoi(9, 7, new(RoiFilterKind.NativeThreshold, Threshold: 4095));
        var request = new FilterRequest(new("live", "live", 9, 7), true, [roi]);
        var first = Assert.Single((await service.ProcessAsync(request, default)).Regions);
        Assert.Equal(Enumerable.Range(0, 63).Select(i => (byte)(i % 2 == 0 ? 0 : 255)), first.Gray8);
        Assert.Equal(before, pixels.Span.ToArray());
        Assert.False(first.ReusedPlan);
        var second = Assert.Single((await service.ProcessAsync(request, default)).Regions);
        Assert.True(second.ReusedPlan);
        Assert.Equal(0, gpuCalls);
        roi = roi with
        {
            Filter = roi.Filter with
            {
                Threshold = 4096,
                Execution = RoiFilterExecution.CpuGpu
            }
        };
        var fallback = Assert.Single((await service.ProcessAsync(request with
        {
            Rois = [roi]
        }, default)).Regions);
        Assert.All(fallback.Gray8, value => Assert.Equal(0, value));
        Assert.False(fallback.ReusedPlan);
        Assert.Contains("no CUDA hardware", Assert.Single(fallback.Execution!.Stages).Reason);
        Assert.Equal("CPU", fallback.Execution.Stages[0].Device);
        Assert.Equal(1, gpuCalls);
    }

    [Theory]
    [InlineData(RoiFilterKind.Erosion)]
    [InlineData(RoiFilterKind.Dilation)]
    [InlineData(RoiFilterKind.Opening)]
    [InlineData(RoiFilterKind.Closing)]
    [InlineData(RoiFilterKind.WhiteTopHat)]
    [InlineData(RoiFilterKind.BlackHat)]
    public async Task MorphologyMatchesIndependentSquareReference(RoiFilterKind kind)
    {
        var pixels = new PixelBuffer(11, 9, PixelKind.Gray8);
        new Random(127).NextBytes(pixels.Span);
        var original = pixels.Span.ToArray();
        using var frame = new VideoFrame(1, 1, pixels);
        using var service = new RoiFilterPreview(new ImageLoader("unused"), frame.Retain);
        var roi = CreateRoi(11, 9, new(kind, Radius: 2));
        var actual = Assert.Single((await service.ProcessAsync(new(new("live", "live", 11, 9), true, [roi]), default)).Regions);
        var erosionFirst = kind is RoiFilterKind.Erosion or RoiFilterKind.Opening or RoiFilterKind.WhiteTopHat;
        var expected = Extrema(original, 11, 9, 2, erosionFirst);
        if (kind is not (RoiFilterKind.Erosion or RoiFilterKind.Dilation))
        {
            expected = Extrema(expected, 11, 9, 2, !erosionFirst);
        }
        for (var i = 0; i < expected.Length; i++)
        {
            if (kind == RoiFilterKind.WhiteTopHat)
            {
                expected[i] = (byte)(original[i] - expected[i]);
            }
            if (kind == RoiFilterKind.BlackHat)
            {
                expected[i] = (byte)(expected[i] - original[i]);
            }
        }
        Assert.Equal(expected, actual.Gray8);
        Assert.Equal(original, pixels.Span.ToArray());
    }

    [Theory]
    [InlineData(PixelKind.Gray8)]
    [InlineData(PixelKind.Gray16)]
    [InlineData(PixelKind.Rgba32)]
    [InlineData(PixelKind.Bgra32)]
    public async Task GaussianKeepsConstantAndAlphaDoesNotAffectLuminance(PixelKind kind)
    {
        var pixels = new PixelBuffer(7, 5, kind);
        pixels.Span.Fill(87);
        if (kind is PixelKind.Rgba32 or PixelKind.Bgra32)
        {
            for (var i = 3; i < pixels.Length; i += 4)
            {
                pixels.Span[i] = 0;
            }
        }
        using var frame = new VideoFrame(1, 1, pixels);
        using var service = new RoiFilterPreview(new ImageLoader("unused"), frame.Retain);
        var roi = CreateRoi(7, 5, new(RoiFilterKind.Gaussian, Radius: 7, Sigma: 2));
        var output = Assert.Single((await service.ProcessAsync(new(new("live", "live", 7, 5), true, [roi]), default)).Regions);
        Assert.All(output.Gray8, value => Assert.Equal(87, value));
    }

    [Fact]
    public async Task ImplementationErrorsAreNotConvertedToCpuSuccess()
    {
        using var frame = new VideoFrame(1, 1, new PixelBuffer(4, 4, PixelKind.Gray8));
        using var service = new RoiFilterPreview(new ImageLoader("unused"), frame.Retain,
            () => throw new InvalidOperationException("implementation bug"));
        var roi = CreateRoi(4, 4, new(RoiFilterKind.Gaussian, Execution: RoiFilterExecution.CpuGpu));
        var request = new FilterRequest(new("live", "live", 4, 4), true, [roi]);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ProcessAsync(request, default));
        Assert.Equal("implementation bug", error.Message);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ProcessAsync(request, new(true)));
        service.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.ProcessAsync(request, default));
    }

    private static Roi CreateRoi(int width, int height, FilterSettings settings) =>
        new(Guid.NewGuid(), "filter", new(new(width / 2d, height / 2d), width, height, 0), null, false, 1)
        {
            Filter = settings
        };

    private static byte[] Extrema(byte[] input, int width, int height, int radius, bool minimum)
    {
        var output = new byte[input.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = minimum ? 255 : 0;
                for (var dy = -radius; dy <= radius; dy++)
                {
                    for (var dx = -radius; dx <= radius; dx++)
                    {
                        var sample = input[(Math.Clamp(y + dy, 0, height - 1) * width) + Math.Clamp(x + dx, 0, width - 1)];
                        value = minimum ? Math.Min(value, sample) : Math.Max(value, sample);
                    }
                }
                output[(y * width) + x] = (byte)value;
            }
        }
        return output;
    }
}
