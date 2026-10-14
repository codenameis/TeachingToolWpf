using System.Text.Json;

namespace VisionImaging.Pyramid;

/// <summary>자체 소유 표식과 읽기 lease가 있는 항목만 회수한다. 원본·임의 폴더는 대상이 아니다.</summary>
internal static class RawCacheEntry
{
    private const string Marker = "raw-cache.json";
    private sealed record Ownership(int Format, string Id, int Levels, long ReservedBytes);

    public static void Mark(string directory, string id, int levels, long bytes)
        => File.WriteAllText(Path.Combine(directory, Marker), JsonSerializer.Serialize(new Ownership(1, id, levels, bytes)));

    public static FileStream Lease(string directory)
        => new(directory + ".lease", FileMode.OpenOrCreate, FileAccess.Read, FileShare.Read);

    public static void Touch(string directory) => File.SetLastWriteTimeUtc(Path.Combine(directory, Marker), DateTime.UtcNow);

    public static void MakeSpace(string root, long required, long budget, IProgress<string> progress, CancellationToken token)
    {
        if (required > budget)
        {
            throw new IOException($"영상 캐시 {required}byte가 설정 예산 {budget}byte를 초과합니다.");
        }
        var entries = Directory.EnumerateDirectories(root)
            .Select(path => (Path: path, Owner: ReadOwnership(path)))
            .Where(entry => entry.Owner is not null)
            .OrderBy(entry => File.GetLastWriteTimeUtc(Path.Combine(entry.Path, Marker))).ToArray();
        var used = entries.Sum(entry => entry.Owner!.ReservedBytes);
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            if (used <= budget - required && new DriveInfo(root).AvailableFreeSpace >= required + 64L * 1024 * 1024)
            {
                return;
            }
            if (TryRemove(entry.Path))
            {
                used -= entry.Owner!.ReservedBytes;
                progress.Report("사용하지 않는 자체 영상 캐시 회수");
            }
        }
        if (used > budget - required || new DriveInfo(root).AvailableFreeSpace < required + 64L * 1024 * 1024)
        {
            throw new IOException("사용 중인 캐시를 보존하면서 새 영상을 준비할 디스크/캐시 여유가 부족합니다.");
        }
    }

    public static void RecoverPartials(string root)
    {
        // 같은 root의 import lock 아래에서만 호출하므로 진행 중인 준비 폴더는 없다.
        foreach (var directory in Directory.EnumerateDirectories(root, "*.partial"))
        {
            if (ReadOwnership(directory) is not null)
            {
                TryRemove(directory);
            }
        }
    }

    public static bool TryRemove(string directory)
    {
        var owner = ReadOwnership(directory);
        if (owner is null)
        {
            return false;
        }
        FileStream lease;
        try
        {
            lease = new(directory + ".lease", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error) when ((error.HResult & 0xffff) is 11 or 32 or 33)
        {
            return false;
        }
        using (lease)
        {
            var expected = Enumerable.Range(0, owner.Levels).Select(level => $"{level}.raw").Append("image.json").Append(Marker).ToHashSet(StringComparer.Ordinal);
            var files = Directory.GetFiles(directory);
            if (Directory.EnumerateDirectories(directory).Any() || files.Any(file => !expected.Contains(Path.GetFileName(file)) ||
                File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)))
            {
                return false;
            }
            // 사용자 원본이나 재귀 삭제 없이 검증된 고정 이름의 소유 파일만 삭제한다.
            foreach (var file in files.Where(file => Path.GetFileName(file) != Marker))
            {
                File.Delete(file);
            }
            File.Delete(Path.Combine(directory, Marker));
            Directory.Delete(directory);
            return true;
        }
    }

    private static Ownership? ReadOwnership(string directory)
    {
        if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
        {
            return null;
        }
        var file = Path.Combine(directory, Marker);
        if (!File.Exists(file) || new FileInfo(file).Length > 1024 || File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))
        {
            return null;
        }
        try
        {
            var owner = JsonSerializer.Deserialize<Ownership>(File.ReadAllText(file));
            var name = Path.GetFileName(directory);
            if (owner is not { Format: 1, Levels: > 0 and <= 31, ReservedBytes: > 0, Id.Length: 64 } ||
                owner.Id.Any(c => !char.IsAsciiHexDigit(c)))
            {
                return null;
            }
            return name == owner.Id || (name.Length == 105 && name.StartsWith(owner.Id + ".", StringComparison.Ordinal) &&
                name.EndsWith(".partial", StringComparison.Ordinal) && Guid.TryParseExact(name.Substring(65, 32), "N", out _)) ? owner : null;
        }
        catch (JsonException)
        {
            // 소유권을 확인할 수 없으면 외부 파일로 취급해 보존한다.
            return null;
        }
    }
}
