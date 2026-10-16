using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using VisionImaging.Abstractions;
using VisionImaging.Codecs;
using VisionImaging.Pyramid;

namespace PyramidComparison;

// 계측은 콘솔 호스트에 둔다. 실제 앱/디코더/캐시에는 진단 전용 분기를 넣지 않는다.
internal sealed class LoadProfile : IProgress<string>
{
    private readonly Stopwatch _watch = new();
    private readonly List<(string Name, double At)> _boundaries = [];
    private double _openMilliseconds;
    private double _decodeMilliseconds;
    private double _originalFinished;

    public static int Run(string image, string cache, string report)
    {
        if (Directory.Exists(cache))
        {
            throw new InvalidOperationException("첫 로딩 측정은 새 캐시 폴더를 사용해야 합니다.");
        }
        var profile = new LoadProfile();
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        profile._watch.Start();
        ImageInfo info;
        long buffers;
        using (var source = RawPyramidSource.Open(image, cache, new Decoder(profile), profile, default))
        {
            info = source.Info;
            buffers = source.PreparationBufferPeakBytes;
        }
        var total = profile._watch.Elapsed.TotalMilliseconds;
        profile._boundaries.Add(("Complete", total));
        var cpuMilliseconds = (process.TotalProcessorTime - cpu).TotalMilliseconds;
        var reopen = Stopwatch.StartNew();
        using (var source = RawPyramidSource.Open(image, cache, new NativeRasterDecoder(), new Progress<string>(), default))
        {
            reopen.Stop();
        }
        // 아래 해시 계산은 준비 시간 밖에서 수행한다. 원본과 모든 축소 단계 전체 바이트를 비교할 수 있다.
        var entry = Directory.EnumerateFiles(cache, "image.json", SearchOption.AllDirectories).Single();
        var levels = Directory.EnumerateFiles(Path.GetDirectoryName(entry)!, "*.raw").Order().Select(path => new
        {
            Level = Path.GetFileName(path),
            Bytes = new FileInfo(path).Length,
            Sha256 = Hash(path)
        }).ToArray();
        var result = new
        {
            Image = Path.GetFullPath(image),
            InputSha256 = Hash(image),
            Info = info,
            TotalMilliseconds = total,
            DecodeOpenMilliseconds = profile._openMilliseconds,
            DecodeRowsMilliseconds = profile._decodeMilliseconds,
            // 캐시 잠금/사전 검증/버퍼 준비/원본 파일 쓰기·닫기 등이 포함된 잔여 시간이다.
            OriginalWriteAndSetupMilliseconds = profile._originalFinished - profile._openMilliseconds - profile._decodeMilliseconds,
            Pyramid = profile._boundaries.Zip(profile._boundaries.Skip(1), (start, end) => new
            {
                Stage = start.Name,
                Milliseconds = end.At - start.At
            }).ToArray(),
            ReopenMilliseconds = reopen.Elapsed.TotalMilliseconds,
            CpuMilliseconds = cpuMilliseconds,
            PreparationBufferPeakBytes = buffers,
            Levels = levels,
            PyramidBinarySha256 = Hash(typeof(RawPyramidSource).Assembly.Location),
            DecoderBinarySha256 = Hash(typeof(NativeRasterDecoder).Assembly.Location),
            NativeBinarySha256 = Hash(Path.Combine(AppContext.BaseDirectory,
                OperatingSystem.IsWindows() ? "VisionImageCodecs.dll" : "libVisionImageCodecs.so")),
            Framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            LogicalProcessors = Environment.ProcessorCount,
            Avx2 = System.Runtime.Intrinsics.X86.Avx2.IsSupported,
            Note = "Fresh application cache; OS file cache was not cleared. Hashing excluded. Original write includes setup. Final level includes publication/disposal."
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
        File.WriteAllText(report, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"prepare={total:F1}ms decode={profile._decodeMilliseconds:F1}ms original/setup={result.OriginalWriteAndSetupMilliseconds:F1}ms pyramid={total - profile._originalFinished:F1}ms reopen={result.ReopenMilliseconds:F2}ms");
        return 0;
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public void Report(string value)
    {
        if (value.StartsWith("자체 피라미드", StringComparison.Ordinal))
        {
            _boundaries.Add((value, _watch.Elapsed.TotalMilliseconds));
        }
    }

    private sealed class Decoder(LoadProfile profile) : IRasterDecoder
    {
        private readonly NativeRasterDecoder _decoder = new();
        public string CacheIdentity => _decoder.CacheIdentity;
        public IRasterReader Open(string path, CancellationToken token)
        {
            var start = Stopwatch.GetTimestamp();
            var reader = _decoder.Open(path, token);
            profile._openMilliseconds += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            return new Reader(reader, profile);
        }
    }

    private sealed class Reader(IRasterReader reader, LoadProfile profile) : IRasterReader
    {
        public int Width => reader.Width;
        public int Height => reader.Height;
        public PixelKind Kind => reader.Kind;
        public void ReadRows(int top, int height, Span<byte> destination, CancellationToken token)
        {
            var start = Stopwatch.GetTimestamp();
            reader.ReadRows(top, height, destination, token);
            profile._decodeMilliseconds += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        public void Dispose()
        {
            reader.Dispose();
            profile._originalFinished = profile._watch.Elapsed.TotalMilliseconds;
        }
    }
}
