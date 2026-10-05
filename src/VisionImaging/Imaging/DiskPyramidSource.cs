using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NetVips;
using VisionImaging.Abstractions;

namespace VisionImaging;

/// <summary>Imports sequentially to disk-backed native VIPS levels, then reads bounded tiles.</summary>
public sealed class DiskPyramidSource : ITileSource
{
    private readonly Image[] _levels;

    public ImageInfo Info
    {
        get;
    }

    private DiskPyramidSource(string directory, ImageInfo info)
    {
        Info = info;
        var opened = new List<Image>();
        try
        {
            for (var i = 0;
i < info.Levels;
i++)
            {
                opened.Add(Image.NewFromFile(Path.Combine(directory, $"{i}.v"), access: Enums.Access.Random));
            }

            _levels = opened.ToArray();
        }
        catch
        {
            foreach (var image in opened)
            {
                image.Dispose();
            }
            throw;
        }
    }

    public static string Version(string path)
    {
        var file = new FileInfo(path);
        return $"{file.Length}:{file.LastWriteTimeUtc.Ticks}";
    }

    public static DiskPyramidSource Open(string path, string cacheRoot, IProgress<string> progress, CancellationToken cancellationToken)
    {
        VipsRuntime.Configure();
        cancellationToken.ThrowIfCancellationRequested();
        path = Path.GetFullPath(path);
        var stamp = Version(path);
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path + stamp)));
        var directory = Path.Combine(cacheRoot, id);
        var manifest = Path.Combine(directory, "image.json");
        if (File.Exists(manifest))
        {
            return new(directory, JsonSerializer.Deserialize<ImageInfo>(File.ReadAllText(manifest))!);
        }

        Directory.CreateDirectory(cacheRoot);
        var staging = Path.Combine(cacheRoot, id + "." + Guid.NewGuid().ToString("N") + ".partial");
        Directory.CreateDirectory(staging);
        try
        {
            using var input = Image.NewFromFile(path, access: Enums.Access.Sequential, failOn: Enums.FailOn.Error);
            using var normalized = Normalize(input);
            var kind = normalized.Bands == 1 ? normalized.Format == Enums.BandFormat.Ushort ? PixelKind.Gray16 : PixelKind.Gray8 : PixelKind.Rgba32;
            var info = new ImageInfo(path, Path.GetFileName(path), normalized.Width, normalized.Height, kind, ImageInfo.CalculateLevelCount(normalized.Width, normalized.Height));
            var required = checked(((long)info.Width * info.Height * info.BytesPerPixel * 3 / 2) + (64 * 1024 * 1024));
            var drive = new DriveInfo(Path.GetPathRoot(staging)!);
            if (drive.AvailableFreeSpace < required)
            {
                throw new IOException($"이미지 캐시에 약 {required / 1073741824d:F1} GiB의 디스크 여유가 필요합니다.");
            }

            normalized.SetProgress(new Progress<int>(p => progress.Report($"원본 읽기 {p}%")), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            normalized.WriteToFile(Path.Combine(staging, "0.v"));
            for (var level = 1;
level < info.Levels;
level++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress.Report($"확대/축소용 이미지 준비 {level}/{info.Levels - 1}");
                using var previous = Image.NewFromFile(Path.Combine(staging, $"{level - 1}.v"), access: Enums.Access.Sequential);
                using var reduced = previous.Resize(0.5, kernel: Enums.Kernel.Linear);
                reduced.SetProgress(new Progress<int>(), cancellationToken);
                reduced.WriteToFile(Path.Combine(staging, $"{level}.v"));
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (Version(path) != stamp)
            {
                throw new IOException("이미지를 읽는 중 원본 파일이 변경되었습니다. 다시 열어 주세요.");
            }

            File.WriteAllText(Path.Combine(staging, "image.json"), JsonSerializer.Serialize(info));
            Directory.Move(staging, directory);
            return new(directory, info);
        }
        catch (VipsException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, true);
            }
        }
    }

    private static Image Normalize(Image input)
    {
        if (input.Bands == 1 && input.Format is Enums.BandFormat.Uchar or Enums.BandFormat.Ushort)
        {
            return input.Copy();
        }

        using var rgb = input.Colourspace(Enums.Interpretation.Srgb);
        using var bytes = rgb.Cast(Enums.BandFormat.Uchar);
        if (bytes.Bands == 4)
        {
            return bytes.Copy();
        }

        using var three = bytes.ExtractBand(0, n: 3);
        return three.Bandjoin(255);
    }

    public PixelBuffer Read(TileKey key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var image = _levels[key.Level];
        var left = key.X * 512;
        var top = key.Y * 512;
        var width = Math.Min(512, image.Width - left);
        var height = Math.Min(512, image.Height - top);
        var output = new PixelBuffer(width, height, Info.Pixels);
        try
        {
            // Region은 생성·사용·해제를 같은 호출 스레드에서 한다. 다음 Read는 다른 worker여도 안전하다.
            using var region = Region.New(image);
            VipsRegionPixels.Copy(region, left, top, width, height, output.Bytes, output.Length);
            cancellationToken.ThrowIfCancellationRequested();
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var image in _levels)
        {
            image.Dispose();
        }
    }
}
