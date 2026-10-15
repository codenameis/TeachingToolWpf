using PoseMatch.Tdcm.TorchSharp;

namespace PoseMatch.Tdcm.Tests;

public sealed class TorchModelTests
{
    [Fact]
    public void VerifiedNegativeSceneRequiresNoDetections()
    {
        Assert.True(PoseAccuracy.Evaluate([], [], 18, true).Passed);
        Assert.False(PoseAccuracy.Evaluate([], [new(20, 20, 36, 36, 0, 1)], 18, true).Passed);
        Assert.False(PoseAccuracy.Evaluate([], [], 18, false).Passed);
    }

    private static (PixelImage Image, PixelImage Template) Fixture()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        return (new(File.ReadAllBytes(Path.Combine(directory, "image.bgr")), 152, 156, 152 * 3, 3),
            new(File.ReadAllBytes(Path.Combine(directory, "template.bgr")), 31, 33, 31 * 3, 3));
    }

    [Fact]
    public void InvalidCheckpointIsNotReplacedWithOfficialWeights()
    {
        var path = Path.Combine(Path.GetTempPath(), "tdcm-invalid-" + Guid.NewGuid() + ".tdcm");
        File.WriteAllBytes(path, [1, 2, 3]);
        Assert.Throws<InvalidDataException>(() => new TorchTdcmMatcher(checkpoint: path));
    }

    [Fact]
    public void OfficialWeightsProduceSameFiveMapsAsOnnx()
    {
        var (image, template) = Fixture();
        using var onnx = new TdcmMatcher();
        using var torch = new TorchTdcmMatcher();
        var expected = onnx.Predict(image, template);
        var actual = torch.Predict(image, template);
        float[][] first = [expected.Score, expected.Sign, expected.Cos, expected.ScaleX, expected.ScaleY];
        float[][] second = [actual.Score, actual.Sign, actual.Cos, actual.ScaleX, actual.ScaleY];
        for (var h = 0; h < 5; h++)
        {
            var error = first[h].Zip(second[h], (a, b) => Math.Abs(a - b)).Max();
            Assert.True(error < .0002f, $"head {h}: maximum difference {error}");
        }
    }

    [Fact]
    public void TrainingChangesWeightsAndCheckpointRoundTrips()
    {
        var (image, template) = Fixture();
        var path = Path.Combine(Path.GetTempPath(), "tdcm-training-" + Guid.NewGuid() + ".tdcm");
        using var original = new TorchTdcmMatcher();
        var before = original.Predict(image, template);
        using (var trainer = new TdcmTrainer())
        {
            var steps = new List<double>();
            trainer.Train([new(image, template, [new(45, 95, 32.04, 32.4, 50, 1)])],
                new()
                {
                    Epochs = 3
                }, p => steps.Add(p.Loss));
            Assert.Equal(3, steps.Count);
            Assert.All(steps, loss => Assert.True(double.IsFinite(loss)));
            trainer.SaveCheckpoint(path);
            Assert.Throws<IOException>(() => trainer.SaveCheckpoint(path));
        }
        using var first = new TorchTdcmMatcher(checkpoint: path);
        using var second = new TorchTdcmMatcher(checkpoint: path);
        var after = first.Predict(image, template);
        Assert.True(before.Score.Zip(after.Score, (a, b) => Math.Abs(a - b)).Max() > .0001f);
        Assert.Equal(after.Score, second.Predict(image, template).Score);
        // 실패 진단을 위해 이 테스트의 고유 체크포인트는 임시 디렉터리에 보존한다.
    }

    [Fact]
    public void InvalidCancelledAndDisposedCallsFailExplicitly()
    {
        var (image, template) = Fixture();
        using var matcher = new TorchTdcmMatcher();
        Assert.Throws<OperationCanceledException>(() => matcher.Predict(image, template, new(true)));
        matcher.Dispose();
        Assert.Throws<ObjectDisposedException>(() => matcher.Predict(image, template));
        using var trainer = new TdcmTrainer();
        Assert.Throws<ArgumentException>(() => trainer.Train([new(image, template, [new(45, 95, 0, 32, 0, 1)])], new()));
        Assert.Throws<OperationCanceledException>(() => trainer.Train([new(image, template, [])], new(), token: new(true)));
    }
}
