using System.Text.Json;
using VisionAnomaly.Imaging;

namespace VisionAnomaly.Tests;

[Collection("SampleVideoFiles")]
public sealed class LearningTests
{
    [Fact]
    public void RealFeaturesLearnNormalsSeparateVisibleDefectAndRoundTripModel()
    {
        using var processor = new AnomalyProcessor(FeatureTests.ModelPath);
        processor.BeginLearning(640, 360, AnalysisRegion.Full);
        for (var index = 0; index < 40; index++)
        {
            using var frame = VideoFixtures.Frame(index);
            processor.Learn(frame, default);
        }
        var model = processor.CompleteLearning(default);
        using var normal = VideoFixtures.Frame(20);
        using var defect = VideoFixtures.Frame(21, true);
        var good = processor.Predict(normal, default);
        var bad = processor.Predict(defect, default);
        var output = Path.Combine(VideoFixtures.Root, "artifacts", "anomaly-monitor");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "learning.json"), JsonSerializer.Serialize(new
        {
            good,
            bad,
            model.TrainingFrames,
            model.MotionSamples
        }));
        Assert.True(good.AppearanceRatio < 1, $"normal={good.AppearanceRatio}");
        Assert.True(bad.AppearanceRatio > 1, $"defect={bad.AppearanceRatio}");
        Assert.NotEmpty(bad.Areas);
        var path = Path.Combine(output, "normal-model.json");
        model.Save(path);
        var loaded = NormalModel.Load(path);
        using var second = new AnomalyProcessor(FeatureTests.ModelPath);
        second.UseModel(loaded);
        Assert.Equal(bad.AppearanceRatio, second.Predict(defect, default).AppearanceRatio);
        Assert.Throws<InvalidDataException>(() => second.UseModel(loaded with { FeatureHash = new('0', 64) }));
    }

    [Fact]
    public void InvalidTrainingAndCancelledFitCannotPublishAModel()
    {
        using var processor = new AnomalyProcessor(FeatureTests.ModelPath);
        using var frame = VideoFixtures.Frame(0);
        Assert.Throws<InvalidOperationException>(() => processor.Predict(frame, default));
        processor.BeginLearning(640, 360, AnalysisRegion.Full);
        Assert.Throws<InvalidOperationException>(() => processor.CompleteLearning(default));
        Assert.Throws<OperationCanceledException>(() => processor.Learn(frame, new(true)));
        Assert.Null(processor.Model);
        Assert.Throws<ArgumentException>(() => new AnalysisRegion(.9, 0, .2, 1).Validate());
    }

    [Fact]
    public void FileInputDecodesRealPixelsAndEndsWithoutPretendingConnectionFailure()
    {
        var path = VideoFixtures.WriteVideo("normal", false);
        VideoFixtures.WriteVideo("inspection", true);
        using var reader = new VideoReader(new(VideoSourceKind.File, path));
        var count = 0;
        while (reader.Read(default) is { } frame)
        {
            using (frame)
            {
                Assert.Equal(640, frame.Pixels.Width);
                Assert.True(frame.Pixels.Span.Contains((byte)255));
                count++;
            }
        }
        Assert.Equal(240, count);
        Assert.InRange(reader.MediaSeconds, 23.8, 24.1);
    }
}
