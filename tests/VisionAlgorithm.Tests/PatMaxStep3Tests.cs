// File: tests/VisionAlgorithm.Tests/PatMaxStep3Tests.cs
using VisionAlgorithm.OpenSrc.PatMax;

namespace VisionAlgorithm.Tests;

public sealed class PatMaxStep3Tests
{
    private const int TemplateWidth = 24;
    private const int TemplateHeight = 20;
    private const int TemplateStride = 32;
    private const int SceneWidth = 48;
    private const int SceneHeight = 32;
    private const int SceneStride = 64;
    private const byte DarkPixel = 30;
    private const byte BrightPixel = 220;

    [Theory]
    [InlineData(22.0f, 15.0f, false, false, true)]
    [InlineData(26.0f, 17.0f, true, false, false)]
    [InlineData(26.0f, 17.0f, true, true, true)]
    public void PatQuickCoarseSearcher_PaddedMicroScene_HandlesPolarityPolicyAndEarlyExit(
        float targetCenterX,
        float targetCenterY,
        bool invertTargetContrast,
        bool ignorePolarity,
        bool shouldMatch)
    {
        var templateBuffer = new byte[TemplateHeight * TemplateStride];
        RenderLShapePattern(
            templateBuffer,
            TemplateWidth,
            TemplateHeight,
            TemplateStride,
            centerX: TemplateWidth * 0.5f,
            centerY: TemplateHeight * 0.5f,
            invertContrast: false);

        var trainParams = PatMaxTrainParams.Create(
            coarseGranularity: 2,
            fineGranularity: 1,
            edgeThreshold: 12.0f);
        using var model = PatMaxTrainer.Train(templateBuffer, TemplateWidth, TemplateHeight, TemplateStride, in trainParams);

        var sceneBuffer = new byte[SceneHeight * SceneStride];
        RenderLShapePattern(
            sceneBuffer,
            SceneWidth,
            SceneHeight,
            SceneStride,
            centerX: targetCenterX,
            centerY: targetCenterY,
            invertContrast: invertTargetContrast);

        using var runtimePyramid = new GranularityPyramid();
        runtimePyramid.Build(sceneBuffer, SceneWidth, SceneHeight, SceneStride, trainParams.FineGranularity, trainParams.CoarseGranularity);

        var searchParams = PatMaxSearchParams.Create(
            acceptThreshold: 0.70f,
            ignorePolarity: ignorePolarity,
            maxCandidates: 8);

        Span<CoarsePoseCandidate> candidates = stackalloc CoarsePoseCandidate[8];
        var foundCount = PatQuickCoarseSearcher.Search(model, runtimePyramid, in searchParams, candidates);

        if (!shouldMatch)
        {
            Assert.Equal(0, foundCount);
            return;
        }

        Assert.True(foundCount >= 1);
        var best = candidates[0];
        Assert.True(best.IsValid);
        Assert.InRange(best.Score, 0.70f, 1.0f);
        Assert.InRange(MathF.Abs(best.X - targetCenterX), 0.0f, 2.5f);
        Assert.InRange(MathF.Abs(best.Y - targetCenterY), 0.0f, 2.5f);
    }

    [Fact]
    public void PatQuickCoarseSearcher_WhenFirstThreeAnchorsAreOccluded_StillRecoversTargetAboveAcceptThreshold()
    {
        var templateBuffer = new byte[TemplateHeight * TemplateStride];
        RenderLShapePattern(
            templateBuffer,
            TemplateWidth,
            TemplateHeight,
            TemplateStride,
            centerX: TemplateWidth * 0.5f,
            centerY: TemplateHeight * 0.5f,
            invertContrast: false);

        var trainParams = PatMaxTrainParams.Create(
            coarseGranularity: 2,
            fineGranularity: 1,
            edgeThreshold: 12.0f);
        using var model = PatMaxTrainer.Train(templateBuffer, TemplateWidth, TemplateHeight, TemplateStride, in trainParams);

        const float targetCenterX = 24.0f;
        const float targetCenterY = 16.0f;
        var sceneBuffer = new byte[SceneHeight * SceneStride];
        RenderLShapePattern(
            sceneBuffer,
            SceneWidth,
            SceneHeight,
            SceneStride,
            centerX: targetCenterX,
            centerY: targetCenterY,
            invertContrast: false);

        // Occlude the exact locations of the first 3 coarse anchor dipoles in the scene while leaving >80% of the pattern intact
        var coarseDipoles = model.GetLevel(model.LevelCount - 1).Dipoles;
        for (var a = 0; a < Math.Min(3, coarseDipoles.Count); a++)
        {
            var ax = (int)MathF.Round(targetCenterX + coarseDipoles.X[a]);
            var ay = (int)MathF.Round(targetCenterY + coarseDipoles.Y[a]);
            for (var dy = -2; dy <= 2; dy++)
            {
                for (var dx = -2; dx <= 2; dx++)
                {
                    var sx = Math.Clamp(ax + dx, 0, SceneWidth - 1);
                    var sy = Math.Clamp(ay + dy, 0, SceneHeight - 1);
                    sceneBuffer[(sy * SceneStride) + sx] = DarkPixel;
                }
            }
        }

        using var runtimePyramid = new GranularityPyramid();
        runtimePyramid.Build(sceneBuffer, SceneWidth, SceneHeight, SceneStride, trainParams.FineGranularity, trainParams.CoarseGranularity);

        var searchParams = PatMaxSearchParams.Create(
            acceptThreshold: 0.60f,
            ignorePolarity: false,
            maxCandidates: 8);

        Span<CoarsePoseCandidate> candidates = stackalloc CoarsePoseCandidate[8];
        var foundCount = PatQuickCoarseSearcher.Search(model, runtimePyramid, in searchParams, candidates);

        Assert.True(foundCount >= 1);
        Assert.InRange(MathF.Abs(candidates[0].X - targetCenterX), 0.0f, 2.5f);
        Assert.InRange(MathF.Abs(candidates[0].Y - targetCenterY), 0.0f, 2.5f);
    }

    [Fact]
    public void PatQuickCoarseSearcher_WhenFirstEightProbesAreOccluded_StillRecoversTargetWhenRemainingWeightExceedsAcceptThreshold()
    {
        var templateBuffer = new byte[TemplateHeight * TemplateStride];
        RenderLShapePattern(
            templateBuffer,
            TemplateWidth,
            TemplateHeight,
            TemplateStride,
            centerX: TemplateWidth * 0.5f,
            centerY: TemplateHeight * 0.5f,
            invertContrast: false);

        var trainParams = PatMaxTrainParams.Create(
            coarseGranularity: 1,
            fineGranularity: 1,
            edgeThreshold: 12.0f);
        using var model = PatMaxTrainer.Train(templateBuffer, TemplateWidth, TemplateHeight, TemplateStride, in trainParams);

        var coarseDipoles = model.CoarseLevel.Dipoles;
        Assert.True(coarseDipoles.Count >= 16);

        const float targetCenterX = 24.0f;
        const float targetCenterY = 16.0f;
        var sceneBuffer = new byte[SceneHeight * SceneStride];
        RenderLShapePattern(
            sceneBuffer,
            SceneWidth,
            SceneHeight,
            SceneStride,
            centerX: targetCenterX,
            centerY: targetCenterY,
            invertContrast: false);

        // Occlude the exact locations of the first 8 coarse probes in the scene while remaining probes still exceed acceptThreshold = 0.50
        for (var a = 0; a < 8; a++)
        {
            var ax = (int)MathF.Round(targetCenterX + coarseDipoles.X[a]);
            var ay = (int)MathF.Round(targetCenterY + coarseDipoles.Y[a]);
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    var sx = Math.Clamp(ax + dx, 0, SceneWidth - 1);
                    var sy = Math.Clamp(ay + dy, 0, SceneHeight - 1);
                    sceneBuffer[(sy * SceneStride) + sx] = DarkPixel;
                }
            }
        }

        using var runtimePyramid = new GranularityPyramid();
        runtimePyramid.Build(sceneBuffer, SceneWidth, SceneHeight, SceneStride, trainParams.FineGranularity, trainParams.CoarseGranularity);

        var searchParams = PatMaxSearchParams.Create(
            acceptThreshold: 0.45f,
            ignorePolarity: false,
            maxCandidates: 8);

        Span<CoarsePoseCandidate> candidates = stackalloc CoarsePoseCandidate[8];
        var foundCount = PatQuickCoarseSearcher.Search(model, runtimePyramid, in searchParams, candidates);

        Assert.True(foundCount >= 1);
        Assert.InRange(MathF.Abs(candidates[0].X - targetCenterX), 0.0f, 2.5f);
        Assert.InRange(MathF.Abs(candidates[0].Y - targetCenterY), 0.0f, 2.5f);
    }

    private static void RenderLShapePattern(
        Span<byte> buffer,
        int width,
        int height,
        int stride,
        float centerX,
        float centerY,
        bool invertContrast)
    {
        var bg = invertContrast ? BrightPixel : DarkPixel;
        var fg = invertContrast ? DarkPixel : BrightPixel;

        for (var y = 0; y < height; y++)
        {
            var rowOffset = y * stride;
            var dy = (y + 0.5f) - centerY;
            for (var x = 0; x < width; x++)
            {
                var dx = (x + 0.5f) - centerX;
                var inVerticalBar = dx >= -6.0f && dx <= -1.0f && dy >= -6.0f && dy <= 6.0f;
                var inHorizontalBar = dx >= -6.0f && dx <= 6.0f && dy >= 1.0f && dy <= 6.0f;
                buffer[rowOffset + x] = (inVerticalBar || inHorizontalBar) ? fg : bg;
            }
        }
    }
}
