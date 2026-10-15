using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using PoseMatch.Tdcm;
using PoseMatch.Tdcm.TorchSharp;

if (args.Length < 2)
{
    Console.Error.WriteLine("사용법: PoseMatch.Tdcm.Training dataset.json output.tdcm [--cuda] [--epochs=10] [--learning-rate=0.00001] [--input=previous.tdcm]");
    return 2;
}
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};
try
{
    if (args[0] == "--make-example")
    {
        ExampleDataset.Create(Path.GetFullPath(args[1]));
        return 0;
    }
    var manifestPath = Path.GetFullPath(args[0]);
    if (new FileInfo(manifestPath).Length > 4_194_304)
    {
        throw new InvalidDataException("학습 목록은4MiB 이하여야 합니다.");
    }
    var directory = Path.GetDirectoryName(manifestPath)!;
    var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
    var manifest = JsonSerializer.Deserialize<Dataset>(File.ReadAllText(manifestPath), jsonOptions) ?? throw new InvalidDataException("학습 목록이 없습니다.");
    var all = manifest.Training.Concat(manifest.Validation).ToArray();
    if (all.Length > 10000 || all.Sum(s => ((long)s.Image.Stride * s.Image.Height) + ((long)s.Template.Stride * s.Template.Height)) > 268_435_456)
    {
        throw new InvalidDataException("학습 목록의 픽셀 메모리 예산256MiB를 초과했습니다.");
    }

    if (manifest.Training.Length == 0 || manifest.Validation.Length == 0 ||
        manifest.Training.Concat(manifest.Validation).Any(s => string.IsNullOrWhiteSpace(s.SourceId)) ||
        manifest.Training.Select(s => s.SourceId).Intersect(manifest.Validation.Select(s => s.SourceId)).Any())
    {
        throw new InvalidDataException("학습/검증 SourceId를 분리하고 두 목록 모두 제공하세요. 같은 원본의 증강은 같은 SourceId입니다.");
    }

    var trainingHashes = manifest.Training.Select(s => Hash(Path.Combine(directory, s.Image.Path))).ToHashSet();
    if (manifest.Validation.Any(s => trainingHashes.Contains(Hash(Path.Combine(directory, s.Image.Path)))))
    {
        throw new InvalidDataException("같은 영상이 학습과 검증에 함께 있습니다.");
    }

    var training = manifest.Training.Select(LoadSample).ToArray();
    var validation = manifest.Validation.Select(LoadSample).ToArray();
    string? Option(string name) => args.FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.Ordinal))?.Split('=', 2)[1];
    var options = new TdcmTrainingOptions
    {
        Epochs = int.Parse(Option("--epochs") ?? "1", CultureInfo.InvariantCulture),
        LearningRate = double.Parse(Option("--learning-rate") ?? "0.00001", CultureInfo.InvariantCulture),
        PositiveWeight = double.Parse(Option("--positive-weight") ?? "1", CultureInfo.InvariantCulture)
    };
    var device = args.Contains("--cuda") ? TdcmDevice.Cuda : TdcmDevice.Cpu;
    var threads = int.Parse(Option("--threads") ?? "4", CultureInfo.InvariantCulture);
    var initial = Option("--input");
    var output = Path.GetFullPath(args[1]);
    if (File.Exists(output) || File.Exists(output + ".json"))
    {
        throw new IOException("새 출력 경로를 지정하세요. 기존 가중치/보고서는 덮어쓰지 않습니다.");
    }

    var before = Evaluate(initial);
    var history = new List<TdcmTrainingProgress>();
    var timer = Stopwatch.StartNew();
    using (var trainer = new TdcmTrainer(device, threads, checkpoint: initial))
    {
        trainer.Train(training, options, p =>
        {
            history.Add(p);
            Console.WriteLine(JsonSerializer.Serialize(p));
        }, cancel.Token);
        cancel.Token.ThrowIfCancellationRequested();
        trainer.SaveCheckpoint(output);
    }
    var trainingMilliseconds = timer.Elapsed.TotalMilliseconds;
    var after = Evaluate(output);
    var validationPassed = after.All(row => row.Accuracy.Passed && !row.Result.CandidatesTruncated && !row.Result.MatchLimitExceeded);
    File.WriteAllText(output + ".json", JsonSerializer.Serialize(new
    {
        device = device.ToString(),
        threads,
        validationPassed,
        options,
        trainingMilliseconds,
        dataset = manifestPath,
        datasetSha256 = Hash(manifestPath),
        checkpointSha256 = Hash(output),
        manifest.LabelsVerified,
        manifest.Description,
        trainingSources = manifest.Training.Select(s => s.SourceId).Distinct(),
        validationSources = manifest.Validation.Select(s => s.SourceId).Distinct(),
        before,
        after,
        history,
        note = "BN running 통계 고정, 모든 parameter 미세학습. optimizer 상태 미저장. 전체 장면 검증이며 정답 위치는 검색 입력에 전달하지 않음."
    }, jsonOptions));
    Console.WriteLine($"학습 완료 · 검증 정확도 {(validationPassed ? "PASS" : "FAIL")}: {output}.json");
    return 0;

    TdcmTrainingSample LoadSample(Sample s) => new(Load(s.Image), Load(s.Template), s.Targets);
    PixelImage Load(Pixels p)
    {
        if (p.Width < 1 || p.Height < 1 || (long)p.Width * p.Height > 1_048_576)
        {
            throw new InvalidDataException("학습 영상은 최대1024×1024입니다.");
        }

        var path = Path.Combine(directory, p.Path);
        if (new FileInfo(path).Length > 4_194_304)
        {
            throw new InvalidDataException("픽셀 파일 크기 초과.");
        }

        return new(File.ReadAllBytes(path), p.Width, p.Height, p.Stride, p.Channels);
    }
    ValidationResult[] Evaluate(string? checkpoint)
    {
        using var matcher = new TorchTdcmMatcher(threads, device, checkpoint);
        return validation.Select((sample, i) =>
        {
            cancel.Token.ThrowIfCancellationRequested();
            var watch = Stopwatch.StartNew();
            var result = matcher.Find(sample.Image, sample.Template, new()
            {
                MinimumScore = .5f
            }, cancel.Token);
            return new ValidationResult(manifest.Validation[i].SourceId, watch.Elapsed.TotalMilliseconds,
                PoseAccuracy.Evaluate(sample.Targets, result.Matches, 18, manifest.LabelsVerified), result);
        }).ToArray();
    }
}
catch (Exception error) when (error is not OutOfMemoryException)
{
    Console.Error.WriteLine(error.ToString());
    return 1;
}

static string Hash(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream));
}

internal sealed record Dataset(string Description, bool LabelsVerified, Sample[] Training, Sample[] Validation);
internal sealed record Sample(string SourceId, Pixels Image, Pixels Template, PoseMatchResult[] Targets);
internal sealed record Pixels(string Path, int Width, int Height, int Stride, int Channels = 1);
internal sealed record ValidationResult(string SourceId, double Milliseconds, PoseAccuracy.Report Accuracy, MatchOutput Result);
