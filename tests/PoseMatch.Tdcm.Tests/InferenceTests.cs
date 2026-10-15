using System.Text.Json;

namespace PoseMatch.Tdcm.Tests;

public sealed class InferenceTests
{
    [Fact]
    public void OfficialImageMatchesIndependentPytorchHeadsAndLocation()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "golden.json")));
        var root = json.RootElement;
        var w = root.GetProperty("width").GetInt32();
        var h = root.GetProperty("height").GetInt32();
        var tw = root.GetProperty("templateWidth").GetInt32();
        var th = root.GetProperty("templateHeight").GetInt32();
        using var matcher = new TdcmMatcher();
        var prediction = matcher.Predict(new(File.ReadAllBytes(Path.Combine(folder, "image.bgr")), w, h, w * 3, 3),
            new(File.ReadAllBytes(Path.Combine(folder, "template.bgr")), tw, th, tw * 3, 3));
        float[][] maps = [prediction.Score, prediction.Sign, prediction.Cos, prediction.ScaleX, prediction.ScaleY];
        foreach (var probe in root.GetProperty("probes").EnumerateArray())
        {
            var i = probe.GetProperty("index").GetInt32();
            var expected = probe.GetProperty("values").EnumerateArray().Select(value => value.GetSingle()).ToArray();
            for (var head = 0; head < 5; head++)
            {
                Assert.InRange(Math.Abs(maps[head][i] - expected[head]), 0, 2e-4);
            }
        }
        Assert.Equal(root.GetProperty("peak").GetInt32(), Array.IndexOf(prediction.Score, prediction.Score.Max()));
        var result = PoseDecoder.Decode(prediction, new());
        var match = Assert.Single(result.Matches);
        Assert.InRange(match.X, 42, 48);
        Assert.InRange(match.Y, 92, 98);
        // 공식 예제의 회전은 OpenCV -50°, 영상 좌표 시계방향은 +50°다.
        Assert.InRange(match.AngleDegrees, 25, 75);
        var refined = matcher.Find(new(File.ReadAllBytes(Path.Combine(folder, "image.bgr")), w, h, w * 3, 3),
            new(File.ReadAllBytes(Path.Combine(folder, "template.bgr")), tw, th, tw * 3, 3), new()
            {
                RefinePose = true
            });
        var refinedMatch = Assert.Single(refined.Matches);
        Assert.Equal(match.Score, refinedMatch.Score);
        Assert.InRange(refinedMatch.AngleDegrees, 45, 55);
        Assert.InRange(refinedMatch.Width, 28, 36);
        Assert.InRange(refinedMatch.Height, 28, 36);
    }

    [Fact]
    public void DecoderPreservesIndependentScaleAndReportsResultLimit()
    {
        var score = new float[100 * 100];
        score[2020] = .9f;
        score[7070] = .8f;
        var ones = Enumerable.Repeat(1f, score.Length).ToArray();
        var twos = Enumerable.Repeat(2f, score.Length).ToArray();
        var result = PoseDecoder.Decode(new(100, 100, score, ones, new float[score.Length], ones, twos), new()
        {
            MaximumMatches = 1
        });
        Assert.True(result.MatchLimitExceeded);
        Assert.False(result.CandidatesTruncated);
        var match = Assert.Single(result.Matches);
        Assert.Equal(36, match.Width);
        Assert.Equal(72, match.Height);
        Assert.Equal(-90, match.AngleDegrees);
    }

    [Fact]
    public void CancellationAndInvalidInputsDoNotReturnPartialSuccess()
    {
        using var matcher = new TdcmMatcher();
        var image = new PixelImage(new byte[224 * 224], 224, 224, 224);
        Assert.Throws<OperationCanceledException>(() => matcher.Find(image, image, cancellationToken: new(true)));
        Assert.Throws<ArgumentException>(() => matcher.Predict(new(new byte[225 * 224], 225, 224, 225), image));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MatchOptions { MinimumScore = float.NaN }.Validate());
        matcher.Dispose();
        Assert.Throws<ObjectDisposedException>(() => matcher.Predict(image, image));
    }
}
