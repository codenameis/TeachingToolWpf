using TeachingToolWpf.Imaging.Matching;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionAlgorithm.Tests;
using VisionImaging;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Tests;

public sealed class ShapeSearchPolicyTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task LargeImageRejectsUnsupportedClutterPolicyBeforeOpeningImage(bool contourModel, bool verify)
    {
        var model = ShapeTrainer.Train(SyntheticScene.Create(96, 96, new MatchPose(48, 48, 0)),
            new(ReadOnlyMemory<byte>.Empty, new(48, 48)), new()
            {
                BuildClutterModel = contourModel
            });
        var service = new ShapeMatchingService(new ImageLoader(System.IO.Path.GetTempPath()), () => null);
        var image = new ImageReference("nonexistent-fixture", "fixture", 8192, 8192);
        await Assert.ThrowsAsync<ArgumentException>(() => service.SearchAsync(image, false, model, null,
            new()
            {
                ClutterWeight = 1,
                Verify = verify
            }, CancellationToken.None));
    }
}
