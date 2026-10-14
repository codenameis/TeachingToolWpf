using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using VisionImaging.Fixtures;
using TeachingToolWpf.Imaging.Matching;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionCanvas.Core.Editing;
using VisionGeometry;
using VisionImaging;
using VisionImaging.Abstractions;

/// <summary>실제 파일·큰 ROI·회전/배율/잡음에서 두 형상 경로의 검색 비용과 결과를 함께 기록한다.</summary>
internal static class ShapeRegressionBenchmark
{
    private const int Dimension = 8192;
    private static readonly TimeSpan s_runLimit = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    private sealed record Fixture(string Name, string Path, bool Color, bool NoisyRotation,
        RoiGeometry Region, ImagePoint[] Expected, bool Pins = false);

    public static async Task<bool> RunAsync(string outputPath, int warmRepetitions, string filter)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(warmRepetitions);
        outputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var fixtureDirectory = Path.GetFullPath("artifacts/shape-regression-inputs-v1");
        Directory.CreateDirectory(fixtureDirectory);
        var filters = filter.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var fixtures = CreateFixtures(fixtureDirectory)
            .Where(fixture => filter == "all" || filters.Any(value => fixture.Name.Contains(value, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (fixtures.Length == 0)
        {
            throw new ArgumentException($"일치하는 검사 사례가 없습니다: {filter}");
        }
        var rows = new List<object>();
        var importRows = new List<object>();
        var passed = true;
        foreach (var file in fixtures.DistinctBy(fixture => fixture.Path))
        {
            var existed = File.Exists(file.Path);
            var timer = Stopwatch.StartNew();
            if (!existed)
            {
                using ITileSource source = file.NoisyRotation ? new RotatedNoiseSource() : new ProceduralTileSource();
                WritePng(file.Path, source, file.Color);
            }
            Console.WriteLine($"fixture {System.IO.Path.GetFileName(file.Path)}: {(existed ? "reused" : "created")}, {timer.Elapsed.TotalMilliseconds:F1} ms");
        }

        var loader = new CountingImageLoader(new ImageLoader(Path.GetFullPath("artifacts/shape-regression-image-cache-v1")));
        var service = new ShapeMatchingService(loader, () => null);
        foreach (var fixture in fixtures)
        {
            var reference = new ImageReference(fixture.Path, fixture.Name, Dimension, Dimension, DiskPyramidSource.Version(fixture.Path));
            using (var importing = new CancellationTokenSource(s_runLimit))
            {
                var timer = Stopwatch.StartNew();
                try
                {
                    // PNG 생성/디스크 피라미드 구축은 검사와 분리한다. OS 파일 캐시를 비웠다고 주장하지 않는다.
                    using var source = await loader.LoadAsync(reference, new Progress<string>(), importing.Token);
                    importRows.Add(new
                    {
                        fixture.Name,
                        Status = "IMPORTED",
                        Milliseconds = timer.Elapsed.TotalMilliseconds,
                        source.Info.Pixels
                    });
                }
                catch (Exception error)
                {
                    passed = false;
                    importRows.Add(new
                    {
                        fixture.Name,
                        Status = "IMPORT_FAILED",
                        Milliseconds = timer.Elapsed.TotalMilliseconds,
                        Error = error.ToString()
                    });
                    Save();
                    continue;
                }
            }
            foreach (var geometric in new[] { false, true })
            {
                ShapeTrainingOutput training;
                var trainingOptions = new TrainingOptions
                {
                    MinimumAngleDegrees = fixture.NoisyRotation ? -45 : -180,
                    MaximumAngleDegrees = fixture.NoisyRotation ? 45 : 180,
                    AngleStepDegrees = 10,
                    MinimumScale = fixture.NoisyRotation ? .85 : 1,
                    MaximumScale = fixture.NoisyRotation ? 1.15 : 1,
                    ScaleStep = .05,
                    BuildGeometricModel = geometric,
                    BuildClutterModel = true
                };
                using (var trainingCancellation = new CancellationTokenSource(s_runLimit))
                {
                    try
                    {
                        training = await service.TrainAsync(reference, false, fixture.Region, trainingOptions, trainingCancellation.Token);
                    }
                    catch (Exception error)
                    {
                        passed = false;
                        rows.Add(new
                        {
                            fixture.Name,
                            Geometric = geometric,
                            Status = "TRAINING_FAILED",
                            Error = error.ToString()
                        });
                        Console.WriteLine($"{fixture.Name} geometric={geometric}: training failed {error.Message}");
                        Save();
                        continue;
                    }
                }
                ShapeSearchOutput? first = null;
                // 방식별 순서를 바꿔 항상 CPU 4만 뒤에서 실행하는 편향을 줄인다.
                foreach (var parallelism in geometric ? new[] { 4, 1 } : new[] { 1, 4 })
                {
                    var options = new SearchOptions
                    {
                        MaximumMatches = 1000,
                        MaximumParallelism = parallelism,
                        EvaluateGeometry = geometric,
                        ClutterWeight = 1,
                        ComputeMode = InspectionComputeMode.Cpu
                    };
                    for (var repetition = 0; repetition <= warmRepetitions; repetition++)
                    {
                        loader.Reset();
                        var allocated = GC.GetTotalAllocatedBytes();
                        var collections = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
                        var timer = Stopwatch.StartNew();
                        using var cancellation = new CancellationTokenSource(s_runLimit);
                        try
                        {
                            var result = await service.SearchAsync(reference, false, training.Model, null, options, cancellation.Token);
                            var elapsed = timer.Elapsed.TotalMilliseconds;
                            first ??= result;
                            var parity = first.Matches.SequenceEqual(result.Matches) && first.Truncated == result.Truncated &&
                                first.MatchLimitExceeded == result.MatchLimitExceeded;
                            object validation = fixture.Pins
                                ? PinGroundTruth.Validate(PinGroundTruth.Create(), result.Matches)
                                : fixture.NoisyRotation
                                    ? ValidatePoses(CreateNoisyPoses(), result.Matches, fixture.Region.Width / 2)
                                    : ValidatePositions(fixture.Expected, result.Matches, fixture.Region.Width / 2);
                            var correct = validation switch
                            {
                                PinValidation pins => pins.Passed,
                                PoseValidation poses => poses.Passed,
                                PositionValidation positions => positions.AllKnownPositionsMatched,
                                _ => throw new InvalidOperationException("알 수 없는 검증 결과입니다.")
                            };
                            passed &= correct && parity && !result.Truncated && !result.MatchLimitExceeded;
                            rows.Add(new
                            {
                                fixture.Name,
                                Input = fixture.Path,
                                fixture.Region,
                                Geometric = geometric,
                                Parallelism = parallelism,
                                Repetition = repetition,
                                Phase = repetition == 0 ? "FIRST_SEARCH_FOR_SETTING" : "WARM_SEARCH",
                                Status = "COMPLETED",
                                WallMilliseconds = elapsed,
                                result.TotalMilliseconds,
                                result.ReadMilliseconds,
                                result.Windows,
                                OriginalCrops = result.Diagnostics.Count(value => value.TemplatesEvaluated == 0),
                                CandidatesAfterContinuousRefinement = result.Diagnostics.Sum(value => value.Candidates),
                                BudgetExceededWindows = result.Diagnostics.Count(value => value.CandidateBudgetExceeded),
                                VerificationBudgetExceededWindows = result.Diagnostics.Count(value => value.VerificationBudgetExceeded),
                                PreprocessingWorkMilliseconds = result.Diagnostics.Sum(value => value.PreprocessingMilliseconds),
                                CandidateWorkMilliseconds = result.Diagnostics.Sum(value => value.CandidateMilliseconds),
                                VerificationWorkMilliseconds = result.Diagnostics.Sum(value => value.VerificationMilliseconds),
                                RefinementWorkMilliseconds = result.Diagnostics.Sum(value => value.RefinementMilliseconds),
                                result.Truncated,
                                result.MatchLimitExceeded,
                                Count = result.Matches.Length,
                                Validation = validation,
                                ExactParityWithFirstRun = parity,
                                Reads = loader.Snapshot(),
                                AllocatedBytes = GC.GetTotalAllocatedBytes() - allocated,
                                Collections = new[] { GC.CollectionCount(0) - collections[0], GC.CollectionCount(1) - collections[1], GC.CollectionCount(2) - collections[2] },
                                Options = options,
                                TrainingOptions = trainingOptions,
                                TrainingReadMilliseconds = training.ReadMilliseconds,
                                training.TrainingMilliseconds,
                                TrainingLevels = training.Model.Levels.Length,
                                training.Model.TemplateCount,
                                ModelBytes = training.Model.EstimatedBytes,
                                MatchDetails = result.Matches
                            });
                            Console.WriteLine($"{fixture.Name} geometry={geometric} CPU{parallelism} run{repetition}: {elapsed:F1} ms, read {result.ReadMilliseconds:F1}, {result.Matches.Length} matches, crops {result.Diagnostics.Count(value => value.TemplatesEvaluated == 0)}, complete={!result.Truncated && !result.MatchLimitExceeded}, knownRecall={correct}, parity={parity}");
                        }
                        catch (Exception error)
                        {
                            passed = false;
                            rows.Add(new
                            {
                                fixture.Name,
                                Geometric = geometric,
                                Parallelism = parallelism,
                                Repetition = repetition,
                                Status = error is OperationCanceledException ? "TIMEOUT_INCOMPLETE" : "FAILED",
                                WallMilliseconds = timer.Elapsed.TotalMilliseconds,
                                Reads = loader.Snapshot(),
                                Error = error.ToString()
                            });
                            Console.WriteLine($"{fixture.Name} geometry={geometric} CPU{parallelism} run{repetition}: {timer.Elapsed.TotalMilliseconds:F1} ms, {error.GetType().Name}: {error.Message}");
                        }
                        Save();
                    }
                }
            }
        }
        return passed;

        void Save()
        {
            File.WriteAllText(outputPath, JsonSerializer.Serialize(new
            {
                Timestamp = DateTimeOffset.Now,
                Filter = filter,
                WarmRepetitions = warmRepetitions,
                TimeLimitSeconds = s_runLimit.TotalSeconds,
                Input = "Actual 8192×8192 Gray8/RGB PNG files assembled from original 512px source tiles; synthetic, not field data",
                Timing = "First search follows file import and training; not process/OS-cache cold. Work phase sums overlap under parallel execution. File creation/import/training excluded from search wall time.",
                Accuracy = "Pin fixture uses complete pin assignment. Controlled noisy rotation fixture requires unique complete assignment, position <=1px, wrapped angle <=1 degree, and absolute scale error <=0.01. PCB crops report known positions at 1px plus unassigned matches for review; repeated crop symmetries are not a complete false-positive oracle.",
                Imports = importRows,
                Results = rows
            }, s_json));
        }
    }

    private static IEnumerable<Fixture> CreateFixtures(string directory)
    {
        foreach (var color in new[] { false, true })
        {
            var prefix = color ? "rgb" : "gray8";
            var file = Path.Combine(directory, $"pcb-{prefix}.png");
            yield return new($"{prefix}-pin128", file, color, false, new(new(759.518, 546.178), 105.827, 129.459, 0), [], true);
            yield return new($"{prefix}-corner512", file, color, false, new(new(448, 704), 512, 512, 0), Grid(448, 704));
            yield return new($"{prefix}-chip1600", file, color, false, new(new(1024, 1024), 1600, 1600, 0), Grid(1024, 1024));
            yield return new($"{prefix}-rotation-noise512", Path.Combine(directory, $"rotation-noise-{prefix}.png"),
                color, true, new(new(1024, 1024), 512, 512, 0), Grid(1024, 1024));
        }
    }

    private static ImagePoint[] Grid(int left, int top)
        => Enumerable.Range(0, 16).Select(index => new ImagePoint(left + (index % 4 * 2048), top + (index / 4 * 2048))).ToArray();

    private static MatchPose[] CreateNoisyPoses()
    {
        double[] degrees = [0, 17, -23, 31];
        double[] scales = [1, .9, 1.1, 1];
        return Enumerable.Range(0, 16).Select(index => new MatchPose(
            1024 + (index % 4 * 2048), 1024 + (index / 4 * 2048),
            degrees[index % degrees.Length] * Math.PI / 180, scales[index % scales.Length])).ToArray();
    }

    private sealed record PositionValidation(bool AllKnownPositionsMatched, int Expected, int Mapped, int Duplicate,
        int Unassigned, int Inaccurate, double? MaximumPositionError, int[] Missing);

    private sealed record PoseAssignment(int ExpectedIndex, MatchPose Expected, MatchPose Actual,
        double PositionError, double AngleErrorDegrees, double ScaleError);

    private sealed record PoseValidation(bool Passed, PositionValidation Positions, int AngleInaccurate, int ScaleInaccurate,
        double? MaximumAngleErrorDegrees, double? MaximumScaleError, PoseAssignment[] Assignments);

    private static PoseValidation ValidatePoses(MatchPose[] expected, IEnumerable<ShapeMatch> matches, double assignmentRadius)
    {
        var records = matches.ToArray();
        var positions = ValidatePositions(expected.Select(pose => new ImagePoint(pose.X, pose.Y)).ToArray(), records, assignmentRadius);
        var assignments = new List<PoseAssignment>();
        foreach (var match in records)
        {
            var nearest = expected.Select((pose, index) => new
            {
                Index = index,
                Pose = pose,
                Error = Math.Sqrt(Math.Pow(pose.X - match.Pose.X, 2) + Math.Pow(pose.Y - match.Pose.Y, 2))
            }).MinBy(value => value.Error)!;
            if (nearest.Error <= assignmentRadius)
            {
                var angleError = Math.Abs(Math.IEEERemainder(match.Pose.Angle - nearest.Pose.Angle, Math.Tau)) * 180 / Math.PI;
                assignments.Add(new(nearest.Index, nearest.Pose, match.Pose, nearest.Error,
                    angleError, Math.Abs(match.Pose.Scale - nearest.Pose.Scale)));
            }
        }
        var angleInaccurate = assignments.Count(value => value.AngleErrorDegrees > 1);
        var scaleInaccurate = assignments.Count(value => value.ScaleError > .01);
        return new(positions.AllKnownPositionsMatched && positions.Duplicate == 0 && positions.Unassigned == 0 &&
            angleInaccurate == 0 && scaleInaccurate == 0, positions, angleInaccurate, scaleInaccurate,
            assignments.Count == 0 ? null : assignments.Max(value => value.AngleErrorDegrees),
            assignments.Count == 0 ? null : assignments.Max(value => value.ScaleError), assignments.ToArray());
    }

    private static PositionValidation ValidatePositions(ImagePoint[] expected, IEnumerable<ShapeMatch> matches, double assignmentRadius)
    {
        var found = new HashSet<int>();
        var duplicate = 0;
        var unassigned = 0;
        var inaccurate = 0;
        double? maximumError = null;
        foreach (var match in matches)
        {
            var nearest = expected.Select((point, index) => new
            {
                Index = index,
                Error = Math.Sqrt(Math.Pow(point.X - match.Pose.X, 2) + Math.Pow(point.Y - match.Pose.Y, 2))
            }).MinBy(value => value.Error)!;
            if (nearest.Error > assignmentRadius)
            {
                unassigned++;
                continue;
            }
            if (!found.Add(nearest.Index))
            {
                duplicate++;
            }
            if (nearest.Error > 1)
            {
                inaccurate++;
            }
            maximumError = Math.Max(maximumError ?? 0, nearest.Error);
        }
        var missing = Enumerable.Range(0, expected.Length).Where(index => !found.Contains(index)).ToArray();
        return new(missing.Length == 0 && inaccurate == 0, expected.Length, found.Count, duplicate,
            unassigned, inaccurate, maximumError, missing);
    }

    private static void WritePng(string path, ITileSource source, bool color)
    {
        var channels = color ? 3 : 1;
        // 파일 fixture 생성에만 사용한다. 검사 입력 경로는 실제 PNG와 독립 타일 reader다.
        var output = new byte[Dimension * Dimension * channels];
        for (var top = 0; top < Dimension; top += 512)
        {
            for (var left = 0; left < Dimension; left += 512)
            {
                using var tile = source.Read(new(0, left / 512, top / 512), default);
                var pixels = tile.Span;
                for (var y = 0; y < tile.Height; y++)
                {
                    for (var x = 0; x < tile.Width; x++)
                    {
                        var from = (y * tile.Stride) + (x * 4);
                        var to = (((top + y) * Dimension) + left + x) * channels;
                        if (color)
                        {
                            output[to] = pixels[from];
                            output[to + 1] = pixels[from + 1];
                            output[to + 2] = pixels[from + 2];
                        }
                        else
                        {
                            output[to] = (byte)(((77 * pixels[from]) + (150 * pixels[from + 1]) + (29 * pixels[from + 2]) + 128) >> 8);
                        }
                    }
                }
            }
        }
        ImageFixtureWriter.Write(path, Dimension, Dimension, channels, 8, output);
    }

    private sealed class RotatedNoiseSource : ITileSource
    {
        public ImageInfo Info { get; } = new("rotated-noise", "controlled rotated/noisy patterns", Dimension, Dimension, PixelKind.Rgba32, 1);
        private readonly (double Cosine, double Sine, double Scale)[] _poses = CreateNoisyPoses()
            .Select(pose => (Math.Cos(pose.Angle), Math.Sin(pose.Angle), pose.Scale)).ToArray();

        public PixelBuffer Read(TileKey key, CancellationToken cancellationToken)
        {
            var output = new PixelBuffer(512, 512, PixelKind.Rgba32);
            var left = key.X * 512;
            var top = key.Y * 512;
            var pixels = output.Span;
            for (var y = 0; y < 512; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceY = top + y;
                for (var x = 0; x < 512; x++)
                {
                    var sourceX = left + x;
                    var column = sourceX / 2048;
                    var row = sourceY / 2048;
                    var pose = _poses[(row * 4) + column];
                    var dx = sourceX + .5 - ((column * 2048) + 1024);
                    var dy = sourceY + .5 - ((row * 2048) + 1024);
                    var localX = ((pose.Cosine * dx) + (pose.Sine * dy)) / pose.Scale;
                    var localY = ((-pose.Sine * dx) + (pose.Cosine * dy)) / pose.Scale;
                    var inside = (localX >= -180 && localX <= -100 && localY >= -190 && localY <= 180) ||
                        (localX >= -100 && localX <= 170 && localY >= 100 && localY <= 180) ||
                        (localX >= -100 && localX <= 100 && localY >= -190 && localY <= -120) ||
                        (((localX - 100) * (localX - 100)) + (localY * localY) <= 45 * 45);
                    var hash = unchecked(((uint)(sourceX / 6) * 73856093) ^ ((uint)(sourceY / 6) * 19349663));
                    var noise = (int)(hash % 41) - 20;
                    var value = Math.Clamp((inside ? 210 : 45) + noise, 0, 255);
                    var target = (y * output.Stride) + (x * 4);
                    pixels[target] = (byte)Math.Clamp(value + 8, 0, 255);
                    pixels[target + 1] = (byte)value;
                    pixels[target + 2] = (byte)Math.Clamp(value - 8, 0, 255);
                    pixels[target + 3] = 255;
                }
            }
            return output;
        }

        public void Dispose()
        {
        }
    }
}
