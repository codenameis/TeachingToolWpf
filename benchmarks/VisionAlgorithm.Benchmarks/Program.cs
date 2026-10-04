using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using VisionAlgorithm;
using VisionAlgorithm.OpenSrc.Comparisons;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionAlgorithm.Tests;

if (args.Contains("--geometric-robustness", StringComparer.Ordinal) ||
    args.Contains("--geometric-budget-probe", StringComparer.Ordinal))
{
    GeometricRobustnessBenchmark.Run(args);
    return;
}

var trainingImage = SyntheticScene.Create(96, 96, new MatchPose(48, 48, 0));
var trainingOptions = new TrainingOptions { MinimumScale = 0.9, MaximumScale = 1.2 };
var trainingTimer = Stopwatch.StartNew();
var model = ShapeTrainer.Train(trainingImage, new(ReadOnlyMemory<byte>.Empty, new(48, 48)), trainingOptions);
var trainingMilliseconds = trainingTimer.Elapsed.TotalMilliseconds;
MatchPose[] expected = [new(103.3, 101.7, 0.234, 1.13), new(313.2, 210.4, -0.21, 0.93), new(508.6, 302.1, 0.08, 1.04)];
var scene = SyntheticScene.Create(641, 385, expected);
var negative = SyntheticScene.Create(641, 385);
var matcher = new ShapeMatcher();
var configurations = new Dictionary<string, SearchOptions>
{
    ["candidate"] = new() { Verify = false, Refine = false, MaximumVerifiedCandidates = 256 },
    ["verified"] = new() { Refine = false, MaximumVerifiedCandidates = 256 },
    ["refined_scalar"] = new() { MaximumVerifiedCandidates = 256 },
    ["refined_simd"] = new() { UseSimd = true, MaximumVerifiedCandidates = 256 },
    ["refined_grouped"] = new() { UseSimd = true, GroupTemplates = true, MaximumVerifiedCandidates = 256 },
    ["refined_parallel2"] = new() { UseSimd = true, MaximumParallelism = 2, MaximumVerifiedCandidates = 256 },
    ["refined_tiled"] = new() { TiledPreprocessing = true, MaximumVerifiedCandidates = 256 }
};
var rows = new List<object>();
foreach (var (name, options) in configurations)
{
    for (var i = 0; i < 3; i++)
    {
        matcher.Search(scene, model, options);
    }
    var times = new List<double>();
    var allocations = new List<long>();
    var diagnostics = new List<SearchDiagnostics>();
    MatchResult? last = null;
    for (var i = 0; i < 5; i++)
    {
        var allocated = GC.GetTotalAllocatedBytes(true);
        var watch = Stopwatch.StartNew();
        last = matcher.Search(scene, model, options);
        times.Add(watch.Elapsed.TotalMilliseconds);
        allocations.Add(GC.GetTotalAllocatedBytes(true) - allocated);
        diagnostics.Add(last.Diagnostics);
    }
    var orderedTimes = times.Order().ToArray();
    var output = last!.Matches;
    var assigned = new ShapeMatch?[expected.Length];
    var unmatchedExpected = Enumerable.Range(0, expected.Length).ToList();
    var remainingMatches = output.ToList();
    while (unmatchedExpected.Count > 0 && remainingMatches.Count > 0)
    {
        var bestExpected = -1;
        var bestMatch = -1;
        var bestDistance = double.PositiveInfinity;
        foreach (var expectedIndex in unmatchedExpected)
        {
            foreach (var matchIndex in Enumerable.Range(0, remainingMatches.Count))
            {
                var pose = remainingMatches[matchIndex].Pose;
                var dx = expected[expectedIndex].X - pose.X;
                var dy = expected[expectedIndex].Y - pose.Y;
                var distance = Math.Sqrt((dx * dx) + (dy * dy));
                if (distance < bestDistance)
                {
                    bestExpected = expectedIndex;
                    bestMatch = matchIndex;
                    bestDistance = distance;
                }
            }
        }
        if (bestDistance >= 3)
        {
            break;
        }

        assigned[bestExpected] = remainingMatches[bestMatch];
        unmatchedExpected.Remove(bestExpected);
        remainingMatches.RemoveAt(bestMatch);
    }
    var errors = assigned.Select((match, index) => match is null ? (double?)null :
        Math.Sqrt(((expected[index].X - match.Pose.X) * (expected[index].X - match.Pose.X)) +
            ((expected[index].Y - match.Pose.Y) * (expected[index].Y - match.Pose.Y)))).ToArray();
    var detected = errors.Count(error => error.HasValue);
    var angleErrors = assigned.Select((match, index) => match is null ? (double?)null :
        Math.Abs(match.Pose.Angle - expected[index].Angle) * 180 / Math.PI).Where(error => error.HasValue).ToArray();
    var scaleErrors = assigned.Select((match, index) => match is null ? (double?)null :
        Math.Abs(match.Pose.Scale - expected[index].Scale)).Where(error => error.HasValue).ToArray();
    var negativeResult = matcher.Search(negative, model, options);
    rows.Add(new
    {
        Name = name,
        MeanMilliseconds = times.Average(),
        P50Milliseconds = orderedTimes[2],
        P95Milliseconds = orderedTimes[^1],
        MinimumMilliseconds = times.Min(),
        MaximumMilliseconds = times.Max(),
        MeanAllocatedBytes = allocations.Average(),
        Preprocess = diagnostics.Average(d => d.PreprocessingMilliseconds),
        Candidate = diagnostics.Average(d => d.CandidateMilliseconds),
        Verify = diagnostics.Average(d => d.VerificationMilliseconds),
        Refine = diagnostics.Average(d => d.RefinementMilliseconds),
        Suppression = diagnostics.Average(d => d.SuppressionMilliseconds),
        Detected = detected,
        Expected = expected.Length,
        Returned = output.Length,
        FalsePositives = output.Length - detected,
        FalsePositiveNegativeImage = negativeResult.Matches.Length,
        PositionErrors = errors,
        MaximumAngleErrorDegrees = angleErrors.Length == expected.Length ? angleErrors.Max(error => error!.Value) : -1,
        MaximumScaleError = scaleErrors.Length == expected.Length ? scaleErrors.Max(error => error!.Value) : -1,
        last.Diagnostics.Candidates,
        last.Diagnostics.TemplatesEvaluated,
        last.Diagnostics.EstimatedWorkspaceBytes,
        last.Diagnostics.CandidateBudgetExceeded,
        last.Diagnostics.VerificationBudgetExceeded
    });
}

var comparisonTrainingOptions = new TrainingOptions
{
    MinimumScale = 0.9,
    MaximumScale = 1.2,
    ScaleStep = 0.3,
    MinimumAngleDegrees = -15,
    MaximumAngleDegrees = 15,
    AngleStepDegrees = 15
};
var comparisonSearchOptions = new SearchOptions
{
    MaximumCandidates = 512,
    MaximumVerifiedCandidates = 64,
    MaximumMatches = 8
};
var comparisonRegion = new TrainingRegion(ReadOnlyMemory<byte>.Empty, new(48, 48));

var comparisonTimer = Stopwatch.StartNew();
var chamferModel = DirectionalChamferMatcher.Train(trainingImage, comparisonRegion, comparisonTrainingOptions);
var chamferTrainingMilliseconds = comparisonTimer.Elapsed.TotalMilliseconds;
comparisonTimer.Restart();
var fusionModel = GrayGradientFusion.Train(trainingImage, comparisonRegion, comparisonTrainingOptions);
var fusionTrainingMilliseconds = comparisonTimer.Elapsed.TotalMilliseconds;
comparisonTimer.Restart();
var boldModel = BoldTrainer.Train(trainingImage, comparisonRegion, new());
var boldTrainingMilliseconds = comparisonTimer.Elapsed.TotalMilliseconds;
BoldMatchResult? lastBoldResult = null;

var comparisonRows = new List<object>();
AddComparison("E1_directional_chamfer",
    () => DirectionalChamferMatcher.Search(scene, chamferModel, comparisonSearchOptions).Result.Matches
        .Select(match => match.Pose).ToArray(),
    () => DirectionalChamferMatcher.Search(negative, chamferModel, comparisonSearchOptions).Result.Matches.Length,
    chamferTrainingMilliseconds, "line segments", chamferModel.Levels[0].Length, chamferModel.Shape.TemplateCount);
AddComparison("E2_gray_gradient_fusion",
    () => GrayGradientFusion.Search(scene, fusionModel, comparisonSearchOptions).Result.Matches
        .Select(match => match.Pose).ToArray(),
    () => GrayGradientFusion.Search(negative, fusionModel, comparisonSearchOptions).Result.Matches.Length,
    fusionTrainingMilliseconds, "gradient features", fusionModel.Shape.Levels[0].Features.Length,
    fusionModel.Shape.TemplateCount);
AddComparison("E3_bold",
    () =>
    {
        var result = BoldMatcher.Search(scene, boldModel, new());
        lastBoldResult = result;
        return result.Matches.Select(match => match.Pose).ToArray();
    },
    () => BoldMatcher.Search(negative, boldModel, new()).Matches.Length,
    boldTrainingMilliseconds, "BOLD line segments", boldModel.SegmentCount, 0);

void AddComparison(string name, Func<MatchPose[]> search, Func<int> negativeSearch,
    double modelTrainingMilliseconds, string modelComponentType, int modelComponentCount, int templateCount)
{
    search();
    var times = new List<double>();
    var allocations = new List<long>();
    MatchPose[] matches = [];
    for (var iteration = 0; iteration < 3; iteration++)
    {
        var allocated = GC.GetTotalAllocatedBytes(true);
        var watch = Stopwatch.StartNew();
        matches = search();
        times.Add(watch.Elapsed.TotalMilliseconds);
        allocations.Add(GC.GetTotalAllocatedBytes(true) - allocated);
    }

    var negativeTimer = Stopwatch.StartNew();
    var negativeMatches = negativeSearch();
    var negativeMilliseconds = negativeTimer.Elapsed.TotalMilliseconds;
    var assigned = new MatchPose?[expected.Length];
    var remainingExpected = Enumerable.Range(0, expected.Length).ToList();
    var remainingMatches = matches.ToList();
    while (remainingExpected.Count > 0 && remainingMatches.Count > 0)
    {
        var bestExpected = -1;
        var bestMatch = -1;
        var bestDistance = double.PositiveInfinity;
        foreach (var expectedIndex in remainingExpected)
        {
            for (var matchIndex = 0; matchIndex < remainingMatches.Count; matchIndex++)
            {
                var pose = remainingMatches[matchIndex];
                var dx = expected[expectedIndex].X - pose.X;
                var dy = expected[expectedIndex].Y - pose.Y;
                var distance = Math.Sqrt((dx * dx) + (dy * dy));
                if (distance < bestDistance)
                {
                    bestExpected = expectedIndex;
                    bestMatch = matchIndex;
                    bestDistance = distance;
                }
            }
        }

        if (bestDistance >= 10)
        {
            break;
        }

        assigned[bestExpected] = remainingMatches[bestMatch];
        remainingExpected.Remove(bestExpected);
        remainingMatches.RemoveAt(bestMatch);
    }

    var positionErrors = assigned.Select((pose, index) => pose is null ? (double?)null :
        Math.Sqrt(((expected[index].X - pose.Value.X) * (expected[index].X - pose.Value.X)) +
            ((expected[index].Y - pose.Value.Y) * (expected[index].Y - pose.Value.Y)))).ToArray();
    var angleErrors = assigned.Select((pose, index) => pose is null ? (double?)null :
        Math.Abs(pose.Value.Angle - expected[index].Angle) * 180 / Math.PI).Where(error => error.HasValue).ToArray();
    var scaleErrors = assigned.Select((pose, index) => pose is null ? (double?)null :
        Math.Abs(pose.Value.Scale - expected[index].Scale)).Where(error => error.HasValue).ToArray();
    var orderedTimes = times.Order().ToArray();

    comparisonRows.Add(new
    {
        Name = name,
        TrainingMilliseconds = modelTrainingMilliseconds,
        ModelComponentType = modelComponentType,
        ModelComponentCount = modelComponentCount,
        TemplateCount = templateCount,
        MeanMilliseconds = times.Average(),
        P50Milliseconds = orderedTimes[1],
        P95Milliseconds = orderedTimes[^1],
        NegativeMilliseconds = negativeMilliseconds,
        MeanAllocatedBytes = allocations.Average(),
        PositionMatchTolerancePixels = 10,
        Detected = positionErrors.Count(error => error.HasValue),
        Expected = expected.Length,
        Returned = matches.Length,
        FalsePositives = matches.Length - positionErrors.Count(error => error.HasValue),
        FalsePositivesOnNegativeImage = negativeMatches,
        PositionErrors = positionErrors,
        MaximumAngleErrorDegrees = angleErrors.Length == expected.Length ? angleErrors.Max(error => error!.Value) : -1,
        MaximumScaleError = scaleErrors.Length == expected.Length ? scaleErrors.Max(error => error!.Value) : -1
    });
}

using var cpu = OperatingSystem.IsWindows() ? Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0") : null;
var report = new
{
    Timestamp = DateTimeOffset.Now,
    Cpu = OperatingSystem.IsWindows() ? cpu?.GetValue("ProcessorNameString") : null,
    LogicalProcessors = Environment.ProcessorCount,
    Runtime = RuntimeInformation.FrameworkDescription,
    OS = RuntimeInformation.OSDescription,
    Simd = System.Numerics.Vector.IsHardwareAccelerated,
    VectorBytes = System.Numerics.Vector<byte>.Count,
    Width = scene.Width,
    Height = scene.Height,
    Warmup = 3,
    Repetitions = 5,
    TrainingMilliseconds = trainingMilliseconds,
    ModelBytes = model.EstimatedBytes,
    FeatureCounts = model.Levels.Select(l => l.Features.Length),
    model.TemplateCount,
    TrainingOptions = trainingOptions,
    CandidateBudget = configurations["refined_scalar"].MaximumCandidates,
    VerificationBudget = configurations["refined_scalar"].MaximumVerifiedCandidates,
    CandidateThreshold = configurations["refined_scalar"].CandidateThreshold,
    FinalThreshold = configurations["refined_scalar"].MinimumScore,
    PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
    Input = "독립 4x4 supersampling 합성 3물체 + 일정 밝기 음성. 실제 촬영/타사/원본 구현 비교 아님.",
    Results = rows,
    ComparisonTrainingOptions = comparisonTrainingOptions,
    ComparisonSearchOptions = comparisonSearchOptions,
    ComparisonWarmup = 1,
    ComparisonRepetitions = 3,
    ComparisonPositionMatchTolerancePixels = 10,
    ComparisonResults = comparisonRows,
    BoldDiagnostics = lastBoldResult?.Diagnostics
};
var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
if (args.Length > 0)
{
    File.WriteAllText(args[0], json);
}
Console.WriteLine(json);
