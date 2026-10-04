using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using VisionAlgorithm;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionAlgorithm.Tests;

internal static class GeometricRobustnessBenchmark
{
    public static void Run(string[] arguments)
    {
        var outputIndex = Array.IndexOf(arguments, "--geometric-output");
        var outputPath = outputIndex >= 0 && outputIndex + 1 < arguments.Length
            ? arguments[outputIndex + 1]
            : "geometric-robustness.json";
        if (arguments.Contains("--geometric-budget-probe", StringComparer.Ordinal))
        {
            RunBudgetProbe(outputPath);
            return;
        }
        var model = RobustGeometricScene.Train();
        var ignorePolarityModel = RobustGeometricScene.Train(ContrastPolarity.IgnoreLocal);
        var precision = arguments.Contains("--geometric-precision", StringComparer.Ordinal);
        var options = precision
            ? RobustGeometricScene.PrecisionOptions()
            : new SearchOptions();
        var rows = new List<object>();
        foreach (var scenario in RobustGeometricScene.PositiveScenarios)
        {
            rows.Add(Measure(scenario, model, options, true));
        }
        foreach (var scenario in RobustGeometricScene.NegativeScenarios)
        {
            rows.Add(Measure(scenario, model, options, false));
        }
        var inverted = new GeometricScenario("inverted", 137.4, 1.07, Inverted: true);
        rows.Add(Measure(inverted, model, options, false));
        rows.Add(Measure(inverted, ignorePolarityModel, options, true));
        var report = new
        {
            RecordedAt = DateTimeOffset.Now,
            AlgorithmAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(ShapeMatcher).Assembly.Location))),
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            Processor = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
            Environment.ProcessorCount,
            ImageWidth = RobustGeometricScene.Width,
            ImageHeight = RobustGeometricScene.Height,
            Model = new
            {
                model.TemplateCount,
                model.Options,
                model.EstimatedBytes
            },
            Search = options,
            Scope = "자체 합성 18건, 1회 워밍업 후 3회. 실제 현장/상용 matcher/8K·24K 성능 근거가 아님.",
            Occlusion = "학습 도형 경계 상자 39×42의 오른쪽 25%; 가려진 모델 특징 비율을 뜻하지 않음.",
            PoseLimits = new
            {
                PositionPixels = 0.75,
                AngleDegrees = 1.5,
                ScaleAbsolute = 0.025
            },
            Rows = rows
        };
        File.WriteAllText(outputPath, JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        }));
        Console.WriteLine($"합성 기하 매칭 검증 JSON: {Path.GetFullPath(outputPath)}");
    }

    private static void RunBudgetProbe(string outputPath)
    {
        var model = RobustGeometricScene.Train();
        var options = new SearchOptions { MaximumCandidates = 100000, MaximumVerifiedCandidates = 4096 };
        var names = new[] { "background-clutter", "occlusion-clutter-half-contrast", "similar-distractor" };
        var scenarios = RobustGeometricScene.PositiveScenarios.Concat(RobustGeometricScene.NegativeScenarios)
            .Where(scenario => names.Contains(scenario.Name, StringComparer.Ordinal));
        var rows = new List<object>();
        foreach (var scenario in scenarios)
        {
            var image = RobustGeometricScene.Create(scenario);
            var rawSeeds = CountCoarseSeeds(image, model, options);
            var timer = Stopwatch.StartNew();
            var result = new ShapeMatcher().Search(image, model, options);
            var elapsed = timer.Elapsed.TotalMilliseconds;
            rows.Add(new
            {
                scenario.Name,
                RawCoarseSeeds = rawSeeds,
                Milliseconds = elapsed,
                result.Diagnostics,
                result.Matches
            });
            Console.WriteLine($"{scenario.Name}: {elapsed:F1} ms, {result.Matches.Length} matches, " +
                $"{rawSeeds} raw seeds, {result.Diagnostics.Candidates} refined candidates, exceeded={result.Diagnostics.CandidateBudgetExceeded}");
        }
        File.WriteAllText(outputPath, JsonSerializer.Serialize(new
        {
            Scope = "기본 합격 기준 변경이 아닌 큰 후보 예산의 별도 병목 계측. 시나리오별1회이며 워밍업 없음.",
            Search = options,
            Rows = rows
        }, new JsonSerializerOptions
        {
            WriteIndented = true,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        }));
    }

    private static int CountCoarseSeeds(GrayImage image, ShapeModel model, SearchOptions options)
    {
        var prepared = PreparedSearchImage.Create(image, model.Options.MinimumGradient, model.Levels.Length);
        var map = prepared.Maps[model.Levels.Length - 1];
        var width = (map.Width + map.Step - 1) / map.Step;
        var height = (map.Height + map.Step - 1) / map.Step;
        var scores = new ushort[width * height];
        var count = 0;
        // 잘림 전 seed 수만 계측한다. 후보 검색과 같은 점수·plateau 대표점 규칙이며 검출 판정에는 사용하지 않는다.
        foreach (var template in model.Templates)
        {
            Array.Clear(scores);
            var features = template.Levels[model.Levels.Length - 1];
            CandidateSearch.Accumulate(map, features, scores, width, height, options.UseSimd, default);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var index = (y * width) + x;
                    if (scores[index] < options.CandidateThreshold * 100 * features.Length)
                    {
                        continue;
                    }
                    var maximum = true;
                    for (var dy = Math.Max(0, y - 1); dy <= Math.Min(height - 1, y + 1); dy++)
                    {
                        for (var dx = Math.Max(0, x - 1); dx <= Math.Min(width - 1, x + 1); dx++)
                        {
                            var neighbor = (dy * width) + dx;
                            if (scores[neighbor] > scores[index] || (scores[neighbor] == scores[index] && neighbor < index))
                            {
                                maximum = false;
                            }
                        }
                    }
                    if (maximum)
                    {
                        count++;
                    }
                }
            }
        }
        return count;
    }

    private static object Measure(GeometricScenario scenario, ShapeModel model, SearchOptions options, bool shouldFind)
    {
        var image = RobustGeometricScene.Create(scenario);
        var matcher = new ShapeMatcher();
        matcher.Search(image, model, options);
        var times = new List<double>();
        var allocations = new List<long>();
        MatchResult? result = null;
        for (var repeat = 0; repeat < 3; repeat++)
        {
            var allocated = GC.GetTotalAllocatedBytes(true);
            var timer = Stopwatch.StartNew();
            result = matcher.Search(image, model, options);
            times.Add(timer.Elapsed.TotalMilliseconds);
            allocations.Add(GC.GetTotalAllocatedBytes(true) - allocated);
        }
        var expected = RobustGeometricScene.ExpectedPose(scenario);
        var match = result!.Matches.Length == 1 ? result.Matches[0] : null;
        var positionError = match is null ? (double?)null : RobustGeometricScene.PositionError(match.Pose);
        var angleError = match is null ? (double?)null : RobustGeometricScene.AngleErrorDegrees(match.Pose, expected);
        var scaleError = match is null ? (double?)null : Math.Abs(match.Pose.Scale - expected.Scale);
        var posePassed = shouldFind
            ? positionError <= 0.75 && angleError <= 1.5 && scaleError <= 0.025
            : result.Matches.Length == 0;
        var complete = !result.Diagnostics.CandidateBudgetExceeded && !result.Diagnostics.VerificationBudgetExceeded &&
            !result.Diagnostics.MatchLimitExceeded;
        Console.WriteLine($"{scenario.Name} / {model.Options.Polarity}: pose={posePassed}, complete={complete}, " +
            $"count={result.Matches.Length}, median={times.Order().ElementAt(1):F1} ms");
        return new
        {
            scenario.Name,
            Scenario = scenario,
            Polarity = model.Options.Polarity.ToString(),
            ShouldFind = shouldFind,
            Passed = posePassed && complete,
            PosePassed = posePassed,
            Complete = complete,
            Expected = shouldFind ? expected : (MatchPose?)null,
            MatchCount = result.Matches.Length,
            PositionErrorPixels = positionError,
            AngleErrorDegrees = angleError,
            ScaleAbsoluteError = scaleError,
            Milliseconds = times,
            MedianMilliseconds = times.Order().ElementAt(1),
            AllocatedBytes = allocations,
            result.Diagnostics,
            Matches = result.Matches.Select(value => new
            {
                value.Pose,
                value.CandidateScore,
                value.Score,
                value.Support,
                value.SupportedQuadrants,
                value.Geometry,
                RefinementStatus = value.Refinement.Status.ToString(),
                value.Refinement.Correspondences,
                value.Refinement.AngleObservable
            })
        };
    }
}
