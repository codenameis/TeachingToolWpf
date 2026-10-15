using System.Diagnostics;
using System.Text.Json;
using PoseMatch.Tdcm;
using PoseMatch.Tdcm.TorchSharp;
using TeachingToolWpf.Imaging.Matching;
using VisionGeometry;
using VisionImaging.Abstractions;

var folder = Path.Combine(AppContext.BaseDirectory, "Fixtures");
using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "golden.json")));
var fixture = json.RootElement;
var width = fixture.GetProperty("width").GetInt32();
var height = fixture.GetProperty("height").GetInt32();
var tw = fixture.GetProperty("templateWidth").GetInt32();
var th = fixture.GetProperty("templateHeight").GetInt32();
var image = File.ReadAllBytes(Path.Combine(folder, "image.bgr"));
var template = File.ReadAllBytes(Path.Combine(folder, "template.bgr"));
if (args.Contains("--backend-switch"))
{
    BackendSwitchProbe.Run(new(image, width, height, width * 3, 3), new(template, tw, th, tw * 3, 3), args[0]);
    return;
}
var rows = new List<object>();
var device = args.Contains("--cuda") ? TdcmDevice.Cuda : TdcmDevice.Cpu;
var timer = Stopwatch.StartNew();
var backend = args.Contains("--torch") ? TdcmBackend.TorchSharp : TdcmBackend.OnnxRuntime;
using ITdcmMatcher matcher = backend == TdcmBackend.TorchSharp ? new TorchTdcmMatcher(4, device)
    : new TdcmMatcher(4, device, args.Contains("--profile") ? "tdcm-profile" : null);
var startup = timer.Elapsed.TotalMilliseconds;
if (args.Contains("--accuracy"))
{
    RotationAccuracy.Run(matcher, device, image, width, height, new(template, tw, th, tw * 3, 3), args[0]);
    return;
}
using var referenceMatcher = device == TdcmDevice.Cuda || backend == TdcmBackend.TorchSharp ? new TdcmMatcher(4) : null;
foreach (var size in new[] { 224, 256, 512, 1024 })
{
    var pixels = new byte[size * size * 3];
    for (var y = 0; y < height; y++)
    {
        image.AsSpan(y * width * 3, width * 3).CopyTo(pixels.AsSpan(y * size * 3));
    }

    var durations = new List<double>();
    MatchOutput? output = null;
    for (var repeat = 0; repeat < 4; repeat++)
    {
        timer.Restart();
        output = matcher.Find(new(pixels, size, size, size * 3, 3), new(template, tw, th, tw * 3, 3), new() { RefinePose = args.Contains("--refine") });
        durations.Add(timer.Elapsed.TotalMilliseconds);
    }
    var nearest = output!.Matches.OrderBy(match => Math.Pow(match.X - 45, 2) + Math.Pow(match.Y - 95, 2)).FirstOrDefault();
    double? maximumHeadDifference = null;
    if (referenceMatcher is not null)
    {
        var input = new PixelImage(pixels, size, size, size * 3, 3);
        var teaching = new PixelImage(template, tw, th, tw * 3, 3);
        var cpu = referenceMatcher.Predict(input, teaching);
        var gpu = matcher.Predict(input, teaching);
        float[][] cpuMaps = [cpu.Score, cpu.Sign, cpu.Cos, cpu.ScaleX, cpu.ScaleY];
        float[][] gpuMaps = [gpu.Score, gpu.Sign, gpu.Cos, gpu.ScaleX, gpu.ScaleY];
        maximumHeadDifference = cpuMaps.Select((map, h) => map.Select((v, i) => Math.Abs(v - gpuMaps[h][i])).Max()).Max();
    }
    var row = new
    {
        kind = "native-window",
        backend = backend.ToString(),
        device = device.ToString(),
        size,
        templateSize = "31x33 -> 36x36",
        sessionMilliseconds = startup,
        firstMilliseconds = durations[0],
        warmMilliseconds = durations.Skip(1).ToArray(),
        matches = output.Matches.Count,
        maximumHeadDifference,
        expectedMatches = 1,
        expectedAngle = 50,
        angleErrorDegrees = nearest is null ? (double?)null : Math.Abs(nearest.AngleDegrees - 50),
        errorPixels = nearest is null ? (double?)null : Math.Sqrt(Math.Pow(nearest.X - 45, 2) + Math.Pow(nearest.Y - 95, 2)),
        nearest
    };
    rows.Add(row);
    Console.WriteLine(JsonSerializer.Serialize(row));
}
if (args.Contains("--profile"))
{
    if (matcher is TdcmMatcher onnx) Console.WriteLine("Profile: " + onnx.EndProfiling());
}
File.WriteAllText(args.FirstOrDefault() ?? "tdcm-benchmark.json", JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
if (args.Contains("--native-only")) return;
// 원본 읽기/세션 생성/타일 전체 탐색/후처리 포함. WPF 표시 시간은 포함하지 않는다.
foreach (var templateSize in new[] { 36, 72, 128, 256, 512, 1024, 2048 })
{
    var scale = templateSize / 36d;
    var loader = new FixtureLoader(image, width, height, template, tw, th, scale);
    using var service = new TdcmMatchingService(loader, () => null, o => o.Backend == TdcmBackend.TorchSharp
        ? new TorchTdcmMatcher(o.Threads, o.Device) : new TdcmMatcher(o.Threads, o.Device));
    var trainWidth = (int)Math.Round(tw * scale);
    var trainHeight = (int)Math.Round(th * scale);
    var trainImage = new ImageReference("template", "template", trainWidth, trainHeight);
    timer.Restart();
    var trained = await service.TrainAsync(trainImage, false, new(new(trainWidth / 2d, trainHeight / 2d), trainWidth, trainHeight, 0), default);
    var trainingMs = timer.Elapsed.TotalMilliseconds;
    var searchSize = Math.Max(512, (int)Math.Pow(2, Math.Ceiling(Math.Log2(130 * scale))));
    if (searchSize > 16384)
    {
        continue;
    }

    foreach (var size in new[] { searchSize, Math.Max(searchSize, 8192) }.Distinct())
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        TdcmSearchOutput output;
        timer.Restart();
        try
        {
            output = await service.SearchAsync(new("scene", "scene", size, size), false, trained, null,
                new()
                {
                    WindowSize = 512,
                    Device = device,
                    Backend = backend,
                    Matching = new() { RefinePose = args.Contains("--refine") }
                }, timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            rows.Add(new
            {
                kind = "original-service",
                size,
                templateSize,
                outcome = "timeout-incomplete",
                elapsedMilliseconds = timer.Elapsed.TotalMilliseconds
            });
            File.WriteAllText(args.FirstOrDefault() ?? "tdcm-benchmark.json", JsonSerializer.Serialize(new
            {
                cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
                threads = 4,
                rows
            }, new JsonSerializerOptions { WriteIndented = true }));
            continue;
        }
        var expectedX = 45 * scale;
        var expectedY = 95 * scale;
        var nearest = output.Result.Matches.OrderBy(match => Math.Pow(match.X - expectedX, 2) + Math.Pow(match.Y - expectedY, 2)).FirstOrDefault();
        var row = new
        {
            kind = "original-service",
            backend = backend.ToString(),
            device = device.ToString(),
            size,
            templateSize,
            actualTemplate = new[] { trainWidth, trainHeight },
            reduction = trained.Reduction,
            trainingMilliseconds = trainingMs,
            output.TotalMilliseconds,
            output.ReadMilliseconds,
            output.InitializationMilliseconds,
            output.InferenceMilliseconds,
            output.Windows,
            matches = output.Result.Matches.Count,
            output.Result.CandidatesTruncated,
            output.Result.MatchLimitExceeded,
            errorPixels = nearest is null ? (double?)null : Math.Sqrt(Math.Pow(nearest.X - expectedX, 2) + Math.Pow(nearest.Y - expectedY, 2)),
            expectedAngle = 50,
            nearest
        };
        rows.Add(row);
        Console.WriteLine(JsonSerializer.Serialize(row));
        File.WriteAllText(args.FirstOrDefault() ?? "tdcm-benchmark.json", JsonSerializer.Serialize(new
        {
            cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
            threads = 4,
            rows
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}

internal sealed class FixtureLoader(byte[] image, int width, int height, byte[] template, int tw, int th, double scale) : IImageLoader
{
    public Task<ITileSource> LoadAsync(ImageReference reference, IProgress<string> progress, CancellationToken cancellationToken)
        => Task.FromResult<ITileSource>(reference.Key == "template"
            ? new FixtureSource(template, tw, th, reference, scale)
            : new FixtureSource(image, width, height, reference, scale));
}

internal sealed class FixtureSource(byte[] pixels, int width, int height, ImageReference reference, double scale) : ITileSource
{
    public ImageInfo Info { get; } = new(reference.Key, reference.Name, reference.Width, reference.Height, PixelKind.Gray8, 1);
    public PixelBuffer Read(TileKey key, CancellationToken token)
    {
        if (key.Level != 0)
        {
            throw new ArgumentException("원본 읽기만 허용합니다.");
        }

        var left = key.X * 512;
        var top = key.Y * 512;
        var result = new PixelBuffer(Math.Min(512, reference.Width - left), Math.Min(512, reference.Height - top), PixelKind.Gray8);
        for (var y = 0; y < result.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (var x = 0; x < result.Width; x++)
            {
                var sx = (int)((left + x) / scale);
                var sy = (int)((top + y) / scale);
                var i = (sy * width + sx) * 3;
                result.Span[y * result.Stride + x] = sx >= width || sy >= height ? (byte)0 :
                    (byte)((pixels[i] * 29 + pixels[i + 1] * 150 + pixels[i + 2] * 77 + 128) >> 8);
            }
        }
        return result;
    }
    public void Dispose()
    {
    }
}
