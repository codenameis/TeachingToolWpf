using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Models;

namespace TeachingToolWpf.Services;

/// <summary>
/// 모델 파일이 비대해지는 것을 방지하기 위해 등록된 각 패턴의 이미지를 개별 바이너리 파일(<c>.pimg</c>)로 분리 보관하고,
/// 메타데이터 매니페스트(<c>manifest.json</c>)와 함께 다중 패턴 이미지를 병렬 I/O(<see cref="Parallel.ForEachAsync"/>)로
/// 고속 저장·로드하는 파일 기반 패턴 라이브러리 저장소.
/// </summary>
public sealed class PatternLibraryFileStore
{
    public const string ManifestFileName = "manifest.json";
    public const string PatternImageExtension = ".pimg";
    private const uint BinaryMagicHeader = 0x474D4950u; // "PIMG" in little-endian
    private const int BinaryFormatVersion = 1;
    private const int BinaryHeaderSizeBytes = 16;
    private const int MagicFieldOffset = 0;
    private const int VersionFieldOffset = 4;
    private const int WidthFieldOffset = 8;
    private const int HeightFieldOffset = 12;
    private const int FieldByteCount = sizeof(int);
    private const int AsyncStreamBufferSize = 16384;
    private const int MaxParallelIoDegree = 8;

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _storeDirectory;

    public PatternLibraryFileStore(string? storeDirectory = null)
    {
        _storeDirectory = string.IsNullOrWhiteSpace(storeDirectory)
            ? Path.Combine(Path.GetTempPath(), "TeachingToolWpf", "PatternStore", $"session-{Environment.ProcessId}")
            : storeDirectory;
    }

    public string StoreDirectory => _storeDirectory;

    public string GetPatternImageFilePath(Guid patternId)
        => Path.Combine(_storeDirectory, $"{patternId:N}{PatternImageExtension}");

    /// <summary>
    /// 단일 등록 패턴의 그레이스케일 패치를 개별 바이너리 파일(<c>{patternId:N}.pimg</c>)로 비동기 저장한다.
    /// </summary>
    public async Task<string> SavePatternImageAsync(
        Guid patternId,
        RectifiedPatternPatch patch,
        CancellationToken cancellationToken = default)
    {
        if (patch.IsEmpty)
        {
            throw new ArgumentException("Pattern patch must not be empty.", nameof(patch));
        }

        Directory.CreateDirectory(_storeDirectory);
        string filePath = GetPatternImageFilePath(patternId);
        int payloadLength = checked(patch.Width * patch.Height);
        int totalBytes = BinaryHeaderSizeBytes + payloadLength;
        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(totalBytes);

        try
        {
            WriteHeader(rentedBuffer.AsSpan(0, BinaryHeaderSizeBytes), patch.Width, patch.Height, payloadLength);
            patch.Pixels.AsSpan(0, payloadLength).CopyTo(rentedBuffer.AsSpan(BinaryHeaderSizeBytes, payloadLength));

            await using var stream = new FileStream(
                filePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: AsyncStreamBufferSize,
                options: FileOptions.Asynchronous);

            await stream.WriteAsync(rentedBuffer.AsMemory(0, totalBytes), cancellationToken).ConfigureAwait(false);
            return filePath;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedBuffer);
        }
    }

    /// <summary>
    /// 개별 바이너리 패턴 파일(<c>.pimg</c>)에서 8비트 그레이스케일 패치를 비동기로 읽어온다.
    /// </summary>
    public async Task<RectifiedPatternPatch> LoadPatternImageAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            return RectifiedPatternPatch.Empty;
        }

        byte[] headerBuffer = ArrayPool<byte>.Shared.Rent(BinaryHeaderSizeBytes);
        try
        {
            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: AsyncStreamBufferSize,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);

            int headerRead = await ReadExactAsync(stream, headerBuffer.AsMemory(0, BinaryHeaderSizeBytes), cancellationToken).ConfigureAwait(false);
            if (headerRead < BinaryHeaderSizeBytes)
            {
                return RectifiedPatternPatch.Empty;
            }

            ReadOnlySpan<byte> headerSpan = headerBuffer.AsSpan(0, BinaryHeaderSizeBytes);
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(headerSpan.Slice(MagicFieldOffset, FieldByteCount));
            int version = BinaryPrimitives.ReadInt32LittleEndian(headerSpan.Slice(VersionFieldOffset, FieldByteCount));
            int width = BinaryPrimitives.ReadInt32LittleEndian(headerSpan.Slice(WidthFieldOffset, FieldByteCount));
            int height = BinaryPrimitives.ReadInt32LittleEndian(headerSpan.Slice(HeightFieldOffset, FieldByteCount));

            if (magic != BinaryMagicHeader || version != BinaryFormatVersion || width <= 0 || height <= 0)
            {
                return RectifiedPatternPatch.Empty;
            }

            int payloadLength = checked(width * height);
            var pixels = new byte[payloadLength];
            int payloadRead = await ReadExactAsync(stream, pixels.AsMemory(0, payloadLength), cancellationToken).ConfigureAwait(false);
            if (payloadRead < payloadLength)
            {
                return RectifiedPatternPatch.Empty;
            }

            return RectifiedPatternPatch.Create(pixels, width, height);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(headerBuffer);
        }
    }

    /// <summary>
    /// 등록된 모든 패턴 항목의 이미지 파일을 병렬(<see cref="Parallel.ForEachAsync"/>)로 디스크에 기록하고,
    /// 픽셀 데이터를 제외한 가벼운 메타데이터 인덱스(<c>manifest.json</c>)만 별도로 저장한다.
    /// </summary>
    public async Task<IReadOnlyList<PatternAssetEntry>> SaveLibraryInParallelAsync(
        IReadOnlyList<RegisteredPatternItem> patterns,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        Directory.CreateDirectory(_storeDirectory);

        var entryBag = new ConcurrentBag<PatternAssetEntry>();
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, MaxParallelIoDegree),
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(patterns, parallelOptions, async (item, token) =>
        {
            var patch = item.PreviewPatch;
            if (!patch.IsEmpty)
            {
                string savedPath = await SavePatternImageAsync(item.Id, patch, token).ConfigureAwait(false);
                long fileBytes = BinaryHeaderSizeBytes + checked((long)patch.Width * patch.Height);
                item.MarkBackedByFile(savedPath, fileBytes);
            }

            string fileName = $"{item.Id:N}{PatternImageExtension}";
            entryBag.Add(new PatternAssetEntry
            {
                Id = item.Id,
                Index = item.Index,
                Name = item.Name,
                Method = item.Method,
                Role = item.Role,
                IsEnabled = item.IsEnabled,
                TeachingRoiId = item.TeachingRoiId,
                TeachingRoiName = item.TeachingRoiName,
                RoiCenterX = item.TeachingRegion.Center.X,
                RoiCenterY = item.TeachingRegion.Center.Y,
                RoiWidth = item.TeachingRegion.Width,
                RoiHeight = item.TeachingRegion.Height,
                RoiAngleDegrees = item.TeachingRegion.Angle,
                Summary = item.Summary,
                ImageFileName = fileName,
                PreviewWidth = item.PreviewWidth,
                PreviewHeight = item.PreviewHeight,
                ImageByteSize = item.DiskSizeBytes
            });
        }).ConfigureAwait(false);

        var orderedEntries = entryBag
            .OrderBy(e => e.Method)
            .ThenBy(e => e.Index)
            .ToArray();

        string manifestPath = Path.Combine(_storeDirectory, ManifestFileName);
        await using var manifestStream = new FileStream(
            manifestPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: AsyncStreamBufferSize,
            options: FileOptions.Asynchronous);

        await JsonSerializer.SerializeAsync(manifestStream, orderedEntries, ManifestJsonOptions, cancellationToken).ConfigureAwait(false);
        return orderedEntries;
    }

    /// <summary>
    /// 등록된 패턴 목록의 외부 이미지 파일(<c>.pimg</c>)들을 병렬(<see cref="Parallel.ForEachAsync"/>)로 읽어와
    /// 백그라운드 스레드에서 Frozen <see cref="System.Windows.Media.Imaging.BitmapSource"/> 썸네일을 복원한다.
    /// </summary>
    public async Task<int> ReloadPatternImagesInParallelAsync(
        IReadOnlyList<RegisteredPatternItem> patterns,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        if (patterns.Count == 0)
        {
            return 0;
        }

        int loadedCount = 0;
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, MaxParallelIoDegree),
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(patterns, parallelOptions, async (item, token) =>
        {
            string path = !string.IsNullOrWhiteSpace(item.ImageFilePath)
                ? item.ImageFilePath!
                : GetPatternImageFilePath(item.Id);

            if (!File.Exists(path) && !item.PreviewPatch.IsEmpty)
            {
                path = await SavePatternImageAsync(item.Id, item.PreviewPatch, token).ConfigureAwait(false);
            }

            var loadedPatch = await LoadPatternImageAsync(path, token).ConfigureAwait(false);
            if (!loadedPatch.IsEmpty)
            {
                var frozenThumbnail = PatternThumbnailRenderer.CreateFrozenThumbnail(loadedPatch);
                long fileBytes = new FileInfo(path).Length;
                item.ApplyLoadedPatch(loadedPatch, frozenThumbnail, path, fileBytes);
                Interlocked.Increment(ref loadedCount);
            }
        }).ConfigureAwait(false);

        return loadedCount;
    }

    /// <summary>
    /// 저장소 폴더의 <c>manifest.json</c>을 읽고, 참조된 모든 <c>.pimg</c> 패턴 이미지들을 병렬로 읽어 반환한다.
    /// </summary>
    public async Task<IReadOnlyList<(PatternAssetEntry Metadata, RectifiedPatternPatch Patch)>> ReadManifestAndImagesInParallelAsync(
        CancellationToken cancellationToken = default)
    {
        string manifestPath = Path.Combine(_storeDirectory, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return [];
        }

        PatternAssetEntry[]? entries;
        await using (var manifestStream = new FileStream(
            manifestPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: AsyncStreamBufferSize,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            entries = await JsonSerializer.DeserializeAsync<PatternAssetEntry[]>(manifestStream, ManifestJsonOptions, cancellationToken).ConfigureAwait(false);
        }

        if (entries is null || entries.Length == 0)
        {
            return [];
        }

        var results = new (PatternAssetEntry Metadata, RectifiedPatternPatch Patch)[entries.Length];
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, MaxParallelIoDegree),
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(
            Enumerable.Range(0, entries.Length),
            parallelOptions,
            async (index, token) =>
            {
                var entry = entries[index];
                string imagePath = Path.Combine(_storeDirectory, entry.ImageFileName);
                var patch = await LoadPatternImageAsync(imagePath, token).ConfigureAwait(false);
                results[index] = (entry, patch);
            }).ConfigureAwait(false);

        return results;
    }

    public void DeletePatternImageFile(Guid patternId)
    {
        string path = GetPatternImageFilePath(patternId);
        if (File.Exists(path))
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Ignore transient file lock during cleanup
            }
        }
    }

    private static void WriteHeader(Span<byte> header, int width, int height, int payloadLength)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(MagicFieldOffset, FieldByteCount), BinaryMagicHeader);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(VersionFieldOffset, FieldByteCount), BinaryFormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(WidthFieldOffset, FieldByteCount), width);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(HeightFieldOffset, FieldByteCount), height);
        _ = payloadLength;
    }

    private static async ValueTask<int> ReadExactAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[totalRead..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            totalRead += read;
        }
        return totalRead;
    }
}
