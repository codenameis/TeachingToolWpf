using System.Diagnostics;
using System.Text.Json;
using VisionAnomaly.Monitoring;

namespace VisionAnomaly.Tests;

[Collection("SampleVideoFiles")]
public sealed class SessionTests
{
    [Fact]
    public async Task FailedInputIsFaultedAndCanBeStoppedAndReconnected()
    {
        var output = Path.Combine(VideoFixtures.Root, "artifacts", "anomaly-monitor", "invalid-input", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var invalid = Path.Combine(output, "not-video.avi");
        File.WriteAllText(invalid, "not a video");
        await using var session = new CameraSession("test", FeatureTests.ModelPath, new(output));
        await session.ConnectAsync(new(VideoSourceKind.File, invalid));
        var clock = Stopwatch.StartNew();
        while (session.Snapshot.Mode != MonitorMode.Faulted && clock.Elapsed.TotalSeconds < 10)
        {
            await Task.Delay(20);
        }

        Assert.Equal(MonitorMode.Faulted, session.Snapshot.Mode);
        Assert.Null(session.Snapshot.Prediction);
        await session.StopAsync();
        Assert.Equal(MonitorMode.Disconnected, session.Snapshot.Mode);
        await session.ConnectAsync(new(VideoSourceKind.File, Path.Combine(VideoFixtures.Root, "samples", "AnomalyMonitor.Data", "normal.avi")));
        clock.Restart();
        while (session.Snapshot.Width == 0 && clock.Elapsed.TotalSeconds < 10)
        {
            await Task.Delay(20);
        }

        Assert.Equal(MonitorMode.Preview, session.Snapshot.Mode);
        await session.BeginLearningAsync(AnalysisRegion.Full, 200);
        await session.StopAsync();
        Assert.Equal(MonitorMode.Disconnected, session.Snapshot.Mode);
        Assert.Null(session.Snapshot.ModelId);
    }

    [Fact]
    public void AlarmStoreRejectsEvidencePathsOutsideItsOwnDirectory()
    {
        var output = Path.Combine(VideoFixtures.Root, "artifacts", "anomaly-monitor", "record-validation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var record = new AlarmRecord(Guid.NewGuid(), "CAM01", Guid.NewGuid(), 1, DateTimeOffset.Now, 1,
            AnomalyKind.Appearance, 2, [], "C:/outside.exe");
        File.WriteAllText(Path.Combine(output, record.Id + ".json"), JsonSerializer.Serialize(record));
        Assert.Throws<InvalidDataException>(() => new AlarmStore(output).LoadRecent());
    }
}
