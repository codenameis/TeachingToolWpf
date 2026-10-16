using System.Text.Json;
using VisionAnomaly.Imaging;
using VisionImaging.Abstractions;

namespace VisionAnomaly.Monitoring;

public sealed class AlarmStore(string directory)
{
    private readonly object _sync = new();
    public string DirectoryPath { get; } = Path.GetFullPath(directory);

    public AlarmRecord Add(string camera, NormalModel model, VideoFrame frame, AnomalyKind kind, AnomalyPrediction prediction)
    {
        lock (_sync)
        {
            Directory.CreateDirectory(DirectoryPath);
            var id = Guid.NewGuid();
            var image = Path.Combine(DirectoryPath, id + ".jpg");
            FrameImages.SaveEvidence(frame, image);
            var record = new AlarmRecord(id, camera, model.Id, frame.Sequence, DateTimeOffset.Now,
                frame.Timestamp / (double)System.Diagnostics.Stopwatch.Frequency, kind,
                kind == AnomalyKind.Appearance ? prediction.AppearanceRatio : prediction.MotionRatio,
                kind == AnomalyKind.Appearance ? prediction.Areas : prediction.MotionAreas, image);
            Save(record);
            return record;
        }
    }

    public void Save(AlarmRecord record)
    {
        lock (_sync)
        {
            Directory.CreateDirectory(DirectoryPath);
            var file = Path.Combine(DirectoryPath, record.Id + ".json");
            File.WriteAllText(file + ".writing", JsonSerializer.Serialize(record));
            File.Move(file + ".writing", file, true);
        }
    }

    public IReadOnlyList<AlarmRecord> LoadRecent(int count = 200)
    {
        if (!Directory.Exists(DirectoryPath))
        {
            return [];
        }

        return Directory.EnumerateFiles(DirectoryPath, "*.json").Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc).Take(count)
            .Select(ReadRecord)
            .OrderByDescending(record => record.OccurredAt).ToArray();
    }

    private AlarmRecord ReadRecord(FileInfo file)
    {
        if (file.Length > 1024 * 1024)
        {
            throw new InvalidDataException("알람 기록 파일의 크기 한도를 넘었습니다.");
        }
        var record = JsonSerializer.Deserialize<AlarmRecord>(File.ReadAllText(file.FullName))
            ?? throw new InvalidDataException($"알람 기록을 읽지 못했습니다: {file.Name}");
        var expected = Path.Combine(DirectoryPath, record.Id + ".jpg");
        if (record.Id == Guid.Empty || !Enum.IsDefined(record.Kind) || !Enum.IsDefined(record.Review) ||
            !float.IsFinite(record.Ratio) || !double.IsFinite(record.MediaSeconds) ||
            !string.Equals(Path.GetFullPath(record.EvidenceFile), expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"알람 기록의 증거 경로·수치가 유효하지 않습니다: {file.Name}");
        }
        return record;
    }
}
