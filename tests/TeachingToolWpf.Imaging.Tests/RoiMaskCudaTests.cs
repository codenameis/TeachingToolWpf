using TeachingToolWpf.Imaging.Matching;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionAlgorithm.Tests;
using VisionCompute.ILGPU;
using VisionGeometry;
using VisionImaging;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Tests;

public sealed class RoiMaskCudaTests
{
    [FilterCudaFact]
    public async Task ActualCudaPreservesCpuMaskExclusionAndRemainingPose()
    {
        var scene = SyntheticScene.Create(320, 128, new MatchPose(64, 64, 0), new MatchPose(224, 64, 0));
        var pixels = new PixelBuffer(scene.Width, scene.Height, PixelKind.Gray8);
        scene.Pixels.Span.CopyTo(pixels.Span);
        using var frame = new VideoFrame(1, 1, pixels);
        using var compute = new CudaInspectionSession();
        var service = new ShapeMatchingService(new ImageLoader("unused"), frame.Retain, compute.Rent, compute.PrepareAsync,
            ShapeSearchPipeline.Paper);
        var image = new ImageReference("live", "fixture", scene.Width, scene.Height);
        var trained = await service.TrainAsync(image, true, new(new(64, 64), 64, 64, 0), new()
        {
            MinimumAngleDegrees = 0,
            MaximumAngleDegrees = 0,
            BuildClutterModel = true
        }, default);
        var exclusion = new RoiExclusion(new(new(160, 64), 320, 128, 0), [new(MaskShape.Rectangle, new(0, 0, .5, 1))]);
        var options = new SearchOptions();
        var cpu = await service.SearchAsync(image, true, trained.Model, null, options, default, exclusion);
        await compute.PrepareAsync(default);
        var cuda = await service.SearchAsync(image, true, trained.Model, null, options with
        {
            ComputeMode = InspectionComputeMode.GpuPreprocessing
        }, default, exclusion);
        Assert.True(cuda.CandidateScoring?.Calls > 0 || cuda.Preprocessing?.Calls > 0);
        Assert.False(cuda.Truncated);
        var expected = Assert.Single(cpu.Matches);
        var actual = Assert.Single(cuda.Matches);
        Assert.InRange(Math.Abs(actual.Pose.X - expected.Pose.X), 0, .01);
        Assert.InRange(Math.Abs(actual.Pose.Y - expected.Pose.Y), 0, .01);
        Assert.InRange(Math.Abs(actual.Score - expected.Score), 0, .001);
    }
}
