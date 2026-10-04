using VisionAlgorithm;
using VisionCompute.ILGPU;
using Xunit.Abstractions;

namespace VisionCompute.Tests;

public sealed class CudaPreprocessorLifetimeTests(ITestOutputHelper output)
{
    [Fact]
    public void ReuseGrowthAndShrinkKeepReturnedArraysIndependent()
    {
        using var preprocessor = new CudaImagePreprocessor();
        var firstImage = CudaPreprocessingTests.RandomImage(17, 13, 5);
        var first = preprocessor.Prepare(firstImage, 3);
        foreach (var (width, height) in new[] { (63, 47), (8, 9), (129, 17), (17, 13) })
        {
            var image = CudaPreprocessingTests.RandomImage(width, height, 7);
            var actual = preprocessor.Prepare(image, 3);
            CudaPreprocessingTests.AssertPlanes(CudaPreprocessingTests.ReferenceGradient(image, 3), actual);
        }
        CudaPreprocessingTests.AssertPlanes(CudaPreprocessingTests.ReferenceGradient(firstImage, 3), first);
        Assert.Equal(5, preprocessor.Diagnostics.Calls);
    }

    [Theory]
    [InlineData(CudaImagePreprocessor.MaximumPixels, 1)]
    [InlineData(1, CudaImagePreprocessor.MaximumPixels)]
    public void MaximumBudgetAndExtremeAspectRatiosExecuteOnCuda(int width, int height)
    {
        var pixels = new byte[checked(width * height)];
        Array.Fill(pixels, (byte)117);
        using var preprocessor = new CudaImagePreprocessor();
        var result = preprocessor.Prepare(new(pixels, width, height, width), 3);
        Assert.Equal(pixels.Length, result.X.Length);
        Assert.True(result.X.AsSpan().IndexOfAnyExcept(0f) < 0);
        Assert.True(result.Y.AsSpan().IndexOfAnyExcept(0f) < 0);
        Assert.True(result.Magnitude.AsSpan().IndexOfAnyExcept(0f) < 0);
        Assert.True(result.Bits.AsSpan().IndexOfAnyExcept((byte)0) < 0);
        Assert.Equal(CudaImagePreprocessor.MaximumPixels, preprocessor.Diagnostics.ProcessedPixels);
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(preprocessor.Diagnostics));
    }

    [Fact]
    public void OverBudgetInputIsRejectedWithoutExecutingCuda()
    {
        using var preprocessor = new CudaImagePreprocessor();
        var image = new GrayImage(new byte[CudaImagePreprocessor.MaximumPixels + 1],
            CudaImagePreprocessor.MaximumPixels + 1, 1, CudaImagePreprocessor.MaximumPixels + 1);
        Assert.Throws<ArgumentException>(() => preprocessor.Prepare(image, 3));
        Assert.Equal(0, preprocessor.Diagnostics.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void InvalidThresholdIsRejectedWithoutExecutingCuda(float threshold)
    {
        using var preprocessor = new CudaImagePreprocessor();
        Assert.Throws<ArgumentOutOfRangeException>(() => preprocessor.Prepare(new(new byte[64], 8, 8, 8), threshold));
        Assert.Equal(0, preprocessor.Diagnostics.Calls);
    }

    [Fact]
    public void PreCanceledCallLeavesTheBackendReusable()
    {
        using var preprocessor = new CudaImagePreprocessor();
        var image = CudaPreprocessingTests.RandomImage(31, 27, 2);
        Assert.Throws<OperationCanceledException>(() => preprocessor.Prepare(image, 3, new(true)));
        Assert.Equal(0, preprocessor.Diagnostics.Calls);
        CudaPreprocessingTests.AssertPlanes(CudaPreprocessingTests.ReferenceGradient(image, 3), preprocessor.Prepare(image, 3));
    }

    [Fact]
    public void CancellationDuringPreparationCompletesBeforeReuse()
    {
        using var preprocessor = new CudaImagePreprocessor();
        var image = CudaPreprocessingTests.RandomImage(4096, 2048, 3);
        using var cancellation = new CancellationTokenSource();
        // 큰 실제 작업 중 취소를 요청한다. CUDA 호출 자체는 중단하지 않고 stream 완료 후 반환한다.
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(5));
        Assert.Throws<OperationCanceledException>(() => preprocessor.Prepare(image, 3, cancellation.Token));
        var small = CudaPreprocessingTests.RandomImage(33, 29, 5);
        CudaPreprocessingTests.AssertPlanes(CudaPreprocessingTests.ReferenceGradient(small, 3), preprocessor.Prepare(small, 3));
    }

    [Fact]
    public void DisposeIsIdempotentAndRejectsFurtherUse()
    {
        var preprocessor = new CudaImagePreprocessor();
        preprocessor.Prepare(new(new byte[64], 8, 8, 8), 3);
        preprocessor.Dispose();
        preprocessor.Dispose();
        Assert.Throws<ObjectDisposedException>(() => preprocessor.Prepare(new(new byte[64], 8, 8, 8), 3));
    }
}
