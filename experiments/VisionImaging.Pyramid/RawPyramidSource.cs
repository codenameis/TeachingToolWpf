using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VisionImaging.Abstractions;

namespace VisionImaging.Pyramid;

/// <summary>실험용 자체 raw 피라미드. source 읽기/해제는 ITileSource 단일 소유자 계약을 따른다.</summary>
public sealed class RawPyramidSource : ITileSource
{
    private readonly List<RawLevel> _levels = [];
    private FileStream? _lease;
    private bool _disposed;
    /// <summary>이번 준비의 대여 배열 길이 기준 최대 작업량. 재열기는0이며 native/파일/OS 캐시는 제외한다.</summary>
    public long PreparationBufferPeakBytes { get; }
    public ImageInfo Info
    {
        get;
    }

    private RawPyramidSource(string directory, ImageInfo info, long preparationBufferPeakBytes = 0)
    {
        Info = info;
        PreparationBufferPeakBytes = preparationBufferPeakBytes;
        var width = info.Width;
        var height = info.Height;
        try
        {
            _lease = RawCacheEntry.Lease(directory);
            for (var level = 0; level < info.Levels; level++)
            {
                _levels.Add(new(Path.Combine(directory, $"{level}.raw"), width, height, info.BytesPerPixel));
                width = (width + 1) / 2;
                height = (height + 1) / 2;
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public static RawPyramidSource Open(string path, string cacheRoot, IRasterDecoder decoder, IProgress<string> progress, CancellationToken token,
        long cacheBudgetBytes = 32L * 1024 * 1024 * 1024)
    {
        token.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cacheBudgetBytes);
        if (!Environment.Is64BitProcess || !BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException("실험용 대용량 캐시는 64비트 little-endian 프로세스가 필요합니다.");
        }
        path = Path.GetFullPath(path);
        cacheRoot = Path.GetFullPath(cacheRoot);
        var stamp = ImageFileVersion.Read(path);
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"raw-triangle-v3|{decoder.CacheIdentity}|{path}|{stamp}")));
        var directory = Path.Combine(cacheRoot, id);
        var manifest = Path.Combine(directory, "image.json");
        Directory.CreateDirectory(cacheRoot);
        // 다른 파일의 준비도 직렬화하여 사전 여유 확인과 회수 사이의 경합을 막는다. 기존 reader는 계속 읽는다.
        using var imports = CacheWriterLock.Acquire(Path.Combine(cacheRoot, "imports.lock"), token);
        using var writer = CacheWriterLock.Acquire(Path.Combine(cacheRoot, id + ".lock"), token);
        RawCacheEntry.RecoverPartials(cacheRoot);
        if (stamp != ImageFileVersion.Read(path))
        {
            throw new IOException("캐시 준비를 기다리는 동안 원본 파일이 변경되었습니다.");
        }
        if (Directory.Exists(directory) && !File.Exists(manifest) && !RawCacheEntry.TryRemove(directory))
        {
            throw new InvalidDataException("완료 manifest가 없는 캐시의 소유권/사용 상태를 확인할 수 없습니다.");
        }
        if (File.Exists(manifest))
        {
            try
            {
                if (new FileInfo(manifest).Length > 16384)
                {
                    throw new InvalidDataException("캐시 메타데이터가 허용 크기를 초과했습니다.");
                }
                var cached = JsonSerializer.Deserialize<ImageInfo>(File.ReadAllText(manifest)) ?? throw new InvalidDataException("잘못된 캐시 규격입니다.");
                if (cached.Key != path || cached.Width <= 0 || cached.Height <= 0 ||
                    cached.Pixels is not (PixelKind.Gray8 or PixelKind.Gray16 or PixelKind.Rgba32) ||
                    cached.Levels != ImageInfo.CalculateLevelCount(cached.Width, cached.Height))
                {
                    throw new InvalidDataException("지원하지 않는 캐시 규격입니다.");
                }
                RawCacheEntry.Touch(directory);
                return new(directory, cached);
            }
            catch (Exception error) when (error is InvalidDataException or JsonException or FileNotFoundException)
            {
                if (!RawCacheEntry.TryRemove(directory))
                {
                    throw new InvalidDataException("손상 캐시가 사용 중이거나 소유권을 확인할 수 없어 복구할 수 없습니다.", error);
                }
                progress.Report("손상된 자체 영상 캐시를 다시 준비합니다.");
            }
        }

        var staging = Path.Combine(cacheRoot, id + "." + Guid.NewGuid().ToString("N") + ".partial");
        try
        {
            ImageInfo info;
            long preparationBufferPeakBytes;
            using (var raster = decoder.Open(path, token))
            {
                if (raster.Width <= 0 || raster.Height <= 0 || raster.Width == int.MaxValue || raster.Height == int.MaxValue ||
                    raster.Kind is not (PixelKind.Gray8 or PixelKind.Gray16 or PixelKind.Rgba32))
                {
                    throw new NotSupportedException("실험 로더는 Gray8/Gray16/RGBA32만 지원합니다.");
                }
                info = new ImageInfo(path, Path.GetFileName(path), raster.Width, raster.Height, raster.Kind,
                    ImageInfo.CalculateLevelCount(raster.Width, raster.Height));
                if (checked(info.Width * info.BytesPerPixel) > 4 * 1024 * 1024)
                {
                    throw new NotSupportedException("제한 버퍼 파일 엔진의 원본 한 행은4MiB 이하여야 합니다.");
                }
                var required = checked(RequiredCacheBytes(info) + 16384);
                RawCacheEntry.MakeSpace(cacheRoot, required, cacheBudgetBytes, progress, token);
                Directory.CreateDirectory(staging);
                RawCacheEntry.Mark(staging, id, info.Levels, required);
                preparationBufferPeakBytes = WriteOriginal(raster, info, Path.Combine(staging, "0.raw"), token);
            }
            var width = info.Width;
            var height = info.Height;
            for (var level = 1; level < info.Levels; level++)
            {
                token.ThrowIfCancellationRequested();
                progress.Report($"자체 피라미드 {level}/{info.Levels - 1}");
                var levelBufferBytes = TrianglePyramid.Reduce(Path.Combine(staging, $"{level - 1}.raw"), Path.Combine(staging, $"{level}.raw"), width, height, info.Pixels, token);
                preparationBufferPeakBytes = Math.Max(preparationBufferPeakBytes, levelBufferBytes);
                width = (width + 1) / 2;
                height = (height + 1) / 2;
            }
            token.ThrowIfCancellationRequested();
            if (stamp != ImageFileVersion.Read(path))
            {
                throw new IOException("준비 중 원본 파일이 변경되었습니다.");
            }
            using (var output = new FileStream(Path.Combine(staging, "image.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(output, info);
                output.Flush(true);
            }
            Directory.Move(staging, directory);
            return new(directory, info, preparationBufferPeakBytes);
        }
        catch (Exception error)
        {
            if (Directory.Exists(staging))
            {
                try
                {
                    if (!RawCacheEntry.TryRemove(staging))
                    {
                        progress.Report("미완성 캐시를 회수하지 못했습니다: " + staging);
                    }
                }
                catch (IOException cleanupError)
                {
                    error.Data["CacheCleanupFailure"] = cleanupError.ToString();
                    progress.Report("미완성 캐시 회수 실패: " + cleanupError.Message);
                }
            }
            throw;
        }
    }

    private static long WriteOriginal(IRasterReader raster, ImageInfo info, string path, CancellationToken token)
    {
        var stride = checked(info.Width * info.BytesPerPixel);
        var rows = Math.Min(256, Math.Max(1, 8 * 1024 * 1024 / stride));
        var buffer = ArrayPool<byte>.Shared.Rent(checked(rows * stride));
        try
        {
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024);
            for (var y = 0; y < info.Height; y += rows)
            {
                token.ThrowIfCancellationRequested();
                var height = Math.Min(rows, info.Height - y);
                var length = height * stride;
                raster.ReadRows(y, height, buffer.AsSpan(0, length), token);
                output.Write(buffer, 0, length);
            }
            return buffer.LongLength;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static long RequiredCacheBytes(ImageInfo info)
    {
        long bytes = 0;
        var width = info.Width;
        var height = info.Height;
        for (var level = 0; level < info.Levels; level++)
        {
            bytes = checked(bytes + (long)width * height * info.BytesPerPixel);
            width = (width + 1) / 2;
            height = (height + 1) / 2;
        }
        return bytes;
    }

    public PixelBuffer Read(TileKey key, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if ((uint)key.Level >= _levels.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(key));
        }
        var level = _levels[key.Level];
        if (key.X < 0 || key.Y < 0 || key.X > (level.Width - 1) / 512 || key.Y > (level.Height - 1) / 512)
        {
            throw new ArgumentOutOfRangeException(nameof(key));
        }
        var left = key.X * 512;
        var top = key.Y * 512;
        var pixels = new PixelBuffer(Math.Min(512, level.Width - left), Math.Min(512, level.Height - top), Info.Pixels);
        try
        {
            for (var y = 0; y < pixels.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                level.Row(top + y).Slice(left * Info.BytesPerPixel, pixels.Stride).CopyTo(pixels.Span.Slice(y * pixels.Stride, pixels.Stride));
            }
            return pixels;
        }
        catch
        {
            pixels.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        foreach (var level in _levels)
        {
            level.Dispose();
        }
        _lease?.Dispose();
    }
}
