using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using VisionAlgorithm;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionAlgorithm.Tests;

var trainingImage = SyntheticScene.Create(96, 96, new MatchPose(48, 48, 0));
var trainingOptions = new TrainingOptions { MinimumScale = 0.9, MaximumScale = 1.2 };
var trainingTimer = Stopwatch.StartNew();
var model = ShapeTrainer.Train(trainingImage, new(ReadOnlyMemory<byte>.Empty, new(48, 48)), trainingOptions);
var trainingMilliseconds = trainingTimer.Elapsed.TotalMilliseconds;
MatchPose[] expected = [new(103.3, 101.7, 0.234, 1.13), new(313.2, 210.4, -0.21, 0.93), new(508.6, 302.1, 0.08, 1.04)];
var scene = SyntheticScene.Create(641, 385, expected);
var negative = SyntheticScene.Create(641, 385);
var matcher = new ShapeMatcher();
var configurations = new Dictionary<string, ShapeSearchOptions>
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
    var diagnostics = new List<ShapeSearchDiagnostics>();
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
    Results = rows
};
var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
if (args.Length > 0)
{
    File.WriteAllText(args[0], json);
}
Console.WriteLine(json);
