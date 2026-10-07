using IndustrialVision.Filters.Core;
using IndustrialVision.Filters.Pipeline;

namespace IndustrialVision.Filters.Tests;

public sealed class DefectTests
{
    [Fact]
    public void GaussianCanRemoveASmallRealDefectUnderTheSameDecisionRule()
    {
        using var input = new HostImage(new(15, 15, PixelFormat.Gray16));
        var pixels = Enumerable.Repeat(1000f, 225).ToArray();
        pixels[(7 * 15) + 7] = 900;
        input.WriteFloat(pixels);
        using var plan = new FilterEngine().Compile(new(1, new Gaussian(3, 1.5)), input.Descriptor, new());
        using var output = new HostImage(plan.Output);
        plan.Execute(input.View, output);
        var filtered = AccuracyTests.Read(output.View);
        const float DecisionThreshold = 950;
        Assert.Equal(1, pixels.Count(v => v < DecisionThreshold));
        Assert.Equal(0, filtered.Count(v => v < DecisionThreshold));
        var originalContrast = 1000 - pixels.Min();
        var filteredContrast = 1000 - filtered.Min();
        Assert.Equal(100, originalContrast);
        Assert.InRange(filteredContrast, 0, 10);
        // 합성 정답 마스크 기준 동일 후단 판정에서 FN=1. 보기 좋은 결과를 정확도 개선으로 간주하지 않는다.
    }
}
