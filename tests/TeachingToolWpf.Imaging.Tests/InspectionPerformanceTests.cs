using System.Diagnostics;
using System.Text.Json;
using TeachingToolWpf.Diagnostics;
using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Presentation;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionAlgorithm.Tests;
using VisionCanvas.Core.Editing;
using VisionCanvas.Rendering;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Tests;

public sealed class InspectionPerformanceTests
{
    [Fact]
    public async Task ProfilesChangeSearchWorkWithoutChangingAcceptanceOrLearnedModel()
    {
        var service = new Service();
        using var model = Create(service);
        await model.Train.ExecuteAsync();
        var trained = model.Model;
        model.MinimumScore = .81;
        model.MaximumMatches = 73;
        model.SpeedProfile = InspectionSpeedProfile.Balanced;
        await model.Search.ExecuteAsync();
        var standard = service.LastOptions!;
        model.SpeedProfile = InspectionSpeedProfile.Precise;
        await model.Search.ExecuteAsync();
        var precise = service.LastOptions!;
        Assert.True(precise.MaximumCandidates > standard.MaximumCandidates);
        Assert.True(precise.MaximumVerifiedCandidates > standard.MaximumVerifiedCandidates);
        Assert.True(standard.Refine);
        Assert.True(precise.Refine);
        Assert.Equal(.81, precise.MinimumScore);
        Assert.Equal(73, precise.MaximumMatches);
        Assert.Same(trained, model.Model);
        Assert.False(model.IsTrainingDirty);
        model.MaximumCandidates = 1777;
        Assert.Equal(InspectionSpeedProfile.Custom, model.SpeedProfile);
    }

    [Fact]
    public async Task RemovedFastPresetIsNotOfferedAndLegacyValueUsesStandardBudget()
    {
        var service = new Service();
        using var model = Create(service);
        await model.Train.ExecuteAsync();
        Assert.DoesNotContain(model.SpeedProfiles, option => option.Profile == InspectionSpeedProfile.Fast);
        model.SpeedProfile = InspectionSpeedProfile.Fast;
        await model.Search.ExecuteAsync();
        Assert.Equal(InspectionSpeedProfile.Balanced, model.SpeedProfile);
        Assert.Equal(2048, service.LastOptions!.MaximumCandidates);
        Assert.Equal(64, service.LastOptions.MaximumVerifiedCandidates);
        Assert.Equal(256, service.LastOptions.MaximumCoarseVerifiedCandidates);
        Assert.True(service.LastOptions.Refine);
    }

    [Fact]
    public void ExecutionSettingsRemainIndependentPerMethodAndRejectInvalidInputs()
    {
        using var model = Create(new Service());
        var defaultParallelism = model.MaximumParallelism;
        Assert.Equal(Math.Clamp(Environment.ProcessorCount, 1, 16), defaultParallelism);
        model.MaximumParallelism = 3;
        model.SearchTimeoutSeconds = 12;
        model.SpeedProfile = InspectionSpeedProfile.Precise;
        model.Method = PatternMatchingMethod.GeometricShape;
        Assert.Equal(defaultParallelism, model.MaximumParallelism);
        Assert.Equal(0, model.SearchTimeoutSeconds);
        model.Method = PatternMatchingMethod.Shape;
        Assert.Equal(3, model.MaximumParallelism);
        Assert.Equal(12, model.SearchTimeoutSeconds);
        Assert.Equal(InspectionSpeedProfile.Precise, model.SpeedProfile);
        model.MaximumParallelism = 0;
        Assert.NotEmpty(model.OptionsError);
        Assert.False(model.Search.CanExecute(null));
        model.MaximumParallelism = 2;
        model.SearchTimeoutSeconds = double.NaN;
        Assert.NotEmpty(model.OptionsError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimeoutCancelsSearchWithoutPublishingLateSuccess(bool errorAfterCancellation)
    {
        var service = new Service { WaitForCancellation = true, ErrorAfterCancellation = errorAfterCancellation };
        using var model = Create(service);
        await model.Train.ExecuteAsync();
        model.SearchTimeoutSeconds = .02;
        await model.Search.ExecuteAsync();
        Assert.Equal(InspectionVerdict.Warning, model.Verdict);
        Assert.Contains("시간 한도", model.Status);
        Assert.Empty(model.Markers);
        Assert.True(model.IsIdle);
        var log = Assert.Single(model.InspectionLogs);
        Assert.Equal(errorAfterCancellation ? "failed" : "timeout", log.Outcome);
        Assert.Null(log.MatchCount);
        Assert.Null(log.RendererPresentationMilliseconds);
    }

    [Fact]
    public async Task GpuPreparationIsAwaitedAndReportedSeparatelyBeforeInspection()
    {
        var service = new Service();
        using var model = Create(service);
        await model.Train.ExecuteAsync();
        model.ComputeMode = InspectionComputeMode.GpuPreprocessing;
        await model.Search.ExecuteAsync();
        Assert.Equal(1, service.PreparationCalls);
        Assert.True(service.PreparedBeforeSearch);
        Assert.Contains("준비 123.4 ms", model.Results);
        Assert.Contains("검사 시간과 별도", model.Results);
        Assert.Contains("0.0 ms", model.Status);
    }

    [Fact]
    public async Task CpuInspectionDoesNotPrepareOrAllocateTheGpuSession()
    {
        var service = new Service();
        using var model = Create(service);
        await model.Train.ExecuteAsync();
        await model.Search.ExecuteAsync();
        Assert.Equal(0, service.PreparationCalls);
        Assert.NotNull(service.LastOptions);
    }

    [Fact]
    public async Task InspectionLogCorrelatesPresentationBoundsHistoryAndExportsStructuredFailure()
    {
        using var model = Create(new Service());
        await model.Train.ExecuteAsync();
        for (var index = 0; index < 22; index++)
        {
            await model.Search.ExecuteAsync();
        }
        Assert.Equal(20, model.InspectionLogs.Count);
        Assert.Equal(20, model.InspectionLogs.Select(log => log.RunId).Distinct().Count());
        var recent = model.InspectionLogs[^1];
        Assert.Equal("completed", recent.Outcome);
        Assert.Equal(0, recent.MatchCount);
        Assert.True(recent.CommandMilliseconds >= recent.ResultPublishedMilliseconds);
        Assert.Null(recent.RendererPresentationMilliseconds);
        model.RecordOverlayPresentation(new(Guid.NewGuid(), Stopwatch.GetTimestamp()));
        Assert.Null(model.InspectionLogs[^1].RendererPresentationMilliseconds);
        model.RecordOverlayPresentation(new(recent.PresentationId!.Value, Stopwatch.GetTimestamp()));
        Assert.NotNull(model.InspectionLogs[^1].RendererPresentationMilliseconds);
        using var exported = JsonDocument.Parse(InspectionRunLog.Serialize(model.InspectionLogs));
        Assert.Equal(20, exported.RootElement.GetProperty("Runs").GetArrayLength());
        Assert.Equal("wpf_inspection_command", exported.RootElement.GetProperty("Runs")[0].GetProperty("EntryPoint").GetString());
        Assert.DoesNotContain("fixture", exported.RootElement.GetRawText());
        Assert.Contains("합산 금지", model.InspectionLogText);
        using var failed = Create(new Service { FailPreparation = true });
        await failed.Train.ExecuteAsync();
        failed.ComputeMode = InspectionComputeMode.GpuPreprocessing;
        await failed.Search.ExecuteAsync();
        var error = Assert.Single(failed.InspectionLogs);
        Assert.Equal("failed", error.Outcome);
        Assert.Equal("device_preparation", error.Stage);
        Assert.Equal(typeof(InvalidOperationException).FullName, error.ErrorType);
        Assert.Null(error.ServiceMilliseconds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledGpuPreparationDoesNotStartInspection(bool cancel)
    {
        var service = new Service { FailPreparation = !cancel, WaitDuringPreparation = cancel };
        using var model = Create(service);
        await model.Train.ExecuteAsync();
        model.ComputeMode = InspectionComputeMode.GpuPreprocessing;
        model.SearchTimeoutSeconds = .02;
        await model.Search.ExecuteAsync();
        Assert.Equal(InspectionVerdict.Warning, model.Verdict);
        Assert.Null(service.LastOptions);
        Assert.Empty(model.Markers);
        Assert.True(model.IsIdle);
    }

    private static ShapeMatchingViewModel Create(Service service)
    {
        var session = new EditorSession();
        session.InitializeImage(new("fixture", "fixture", 256, 256), ViewportTransform.Identity);
        session.Add(new(new(100, 100), 70, 60, 0));
        var model = new ShapeMatchingViewModel(service);
        model.Update(session.State, 1, false, true);
        return model;
    }

    private sealed class Service : IShapeMatchingService, IShapeComputePreparation
    {
        public int PreparationCalls
        {
            get; private set;
        }
        public bool PreparedBeforeSearch
        {
            get; private set;
        }
        public bool FailPreparation
        {
            get; init;
        }
        public bool WaitDuringPreparation
        {
            get; init;
        }

        public async Task<double> PrepareGpuAsync(CancellationToken cancellationToken)
        {
            PreparationCalls++;
            if (FailPreparation)
            {
                throw new InvalidOperationException("장치 준비 실패");
            }
            if (WaitDuringPreparation)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            return 123.4;
        }

        public SearchOptions? LastOptions
        {
            get; private set;
        }
        public bool WaitForCancellation
        {
            get; init;
        }
        public bool ErrorAfterCancellation
        {
            get; init;
        }

        public Task<ShapeTrainingOutput> TrainAsync(ImageReference image, bool live, RoiGeometry roi, TrainingOptions options, CancellationToken token, RoiExclusion? exclusion = null)
            => Task.FromResult(new ShapeTrainingOutput(ShapeTrainer.Train(SyntheticScene.Create(96, 96, new MatchPose(48, 48, 0)),
                new(ReadOnlyMemory<byte>.Empty, new(48, 48)), options), 0, 0));

        public async Task<ShapeSearchOutput> SearchAsync(ImageReference image, bool live, ShapeModel model, RoiGeometry? region, SearchOptions options, CancellationToken token, RoiExclusion? exclusion = null)
        {
            LastOptions = options;
            PreparedBeforeSearch = PreparationCalls > 0;
            if (WaitForCancellation)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                catch (OperationCanceledException) when (ErrorAfterCancellation)
                {
                    throw new InvalidOperationException("장치 종료 중 오류");
                }
            }
            return new([], 1, false, 0, 0, []);
        }
    }
}
