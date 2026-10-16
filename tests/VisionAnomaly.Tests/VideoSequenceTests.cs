using System.Diagnostics;
using System.Text.Json;
using VisionAnomaly.Imaging;

namespace VisionAnomaly.Tests;

[Collection("SampleVideoFiles")]
public sealed class VideoSequenceTests
{
    [Fact]
    public void DecodedNormalVideoLearnsBothAppearanceAndMotionAndDetectsChangedSegments()
    {
        var normalPath = Path.Combine(VideoFixtures.Root, "samples", "AnomalyMonitor.Data", "normal.avi");
        var inspectionPath = Path.Combine(VideoFixtures.Root, "samples", "AnomalyMonitor.Data", "inspection.avi");
        // 자체 테스트 데이터는 다른 테스트 실행 순서에 의존하지 않는다.
        if (!File.Exists(normalPath))
        {
            VideoFixtures.WriteVideo("normal", false);
        }

        if (!File.Exists(inspectionPath))
        {
            VideoFixtures.WriteVideo("inspection", true);
        }

        using var processor = new AnomalyProcessor(FeatureTests.ModelPath);
        processor.BeginLearning(640, 360, AnalysisRegion.Full);
        using (var normal = new VideoReader(new(VideoSourceKind.File, normalPath)))
        {
            while (normal.Read(default) is { } frame)
            {
                using (frame)
                {
                    if (frame.Sequence % 3 == 1)
                    {
                        processor.Learn(frame, default);
                    }
                }
            }
        }
        processor.CompleteLearning(default).Save(Path.Combine(VideoFixtures.Root, "samples", "AnomalyMonitor.Data", "normal-model.json"));
        var scores = new List<object>();
        var appearanceDetected = false;
        var motionDetected = false;
        var normalAlarms = 0;
        var alarmGate = new AlarmGate();
        using var inspection = new VideoReader(new(VideoSourceKind.File, inspectionPath));
        var eventTimes = new List<(double Time, AnomalyKind Kind)>();
        while (inspection.Read(default) is { } frame)
        {
            using (frame)
            {
                if (frame.Sequence % 3 != 1)
                {
                    continue;
                }

                var seconds = frame.Timestamp / (double)Stopwatch.Frequency;
                var prediction = processor.Predict(frame, default);
                scores.Add(new
                {
                    seconds,
                    prediction.AppearanceRatio,
                    prediction.MotionRatio,
                    prediction.MotionAvailable,
                    prediction.ProcessingMilliseconds
                });
                foreach (var kind in alarmGate.Update(prediction, seconds))
                {
                    eventTimes.Add((seconds, kind));
                    if (seconds is >= 11 and <= 13 && kind == AnomalyKind.Appearance)
                    {
                        appearanceDetected = true;
                    }
                    else if (seconds is >= 16 and <= 20 && kind == AnomalyKind.Motion)
                    {
                        motionDetected = true;
                    }
                    else if (seconds < 11)
                    {
                        normalAlarms++;
                    }
                }
            }
        }
        var output = Path.Combine(VideoFixtures.Root, "artifacts", "anomaly-monitor");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "sequence.json"), JsonSerializer.Serialize(new
        {
            appearanceDetected,
            motionDetected,
            normalAlarms,
            events = eventTimes.Select(item => new { item.Time, item.Kind }),
            scores
        }));
        Assert.True(appearanceDetected, "11~13초 외관 이상 구간");
        Assert.True(motionDetected, "16~20초 움직임 이상 구간");
        Assert.Equal(0, normalAlarms);
    }
}
