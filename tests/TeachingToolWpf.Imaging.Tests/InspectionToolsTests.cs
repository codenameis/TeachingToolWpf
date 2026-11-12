using System.Collections.Immutable;
using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Models;
using TeachingToolWpf.ViewModels;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Domain;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionAlgorithm.OpenSrc.ShapeDetection;
using VisionCanvas.Core.Editing;
using VisionGeometry;
using VisionImaging;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Tests;

public sealed class InspectionToolsTests
{
    [Fact]
    public async Task OnlySelectedToolRunsAndSourceChangeRejectsItsLateResult()
    {
        var session = new EditorSession();
        session.InitializeImage(new("fixture", "fixture", 256, 256), ViewportTransform.Identity);
        session.Add(new(new(100, 100), 70, 60, 0));
        var shapeService = new ShapeService();
        using var tools = new InspectionToolsViewModel(new(new PatternService()), new(shapeService));
        tools.Update(session.State, 1, false, true);
        Assert.True(tools.Pattern.Train.CanExecute(null));
        Assert.False(tools.Shapes.Detect.CanExecute(null));
        tools.Selected = InspectionToolKind.PrimitiveShapes;
        Assert.False(tools.Pattern.Train.CanExecute(null));
        Assert.True(tools.Shapes.Detect.CanExecute(null));
        var pending = tools.Shapes.Detect.ExecuteAsync();
        Assert.False(tools.IsIdle);
        tools.Selected = InspectionToolKind.Pattern;
        Assert.Equal(InspectionToolKind.PrimitiveShapes, tools.Selected);
        Assert.False(tools.Pattern.Train.CanExecute(null));
        tools.Update(session.State, 2, false, true);
        Assert.True(shapeService.Token.IsCancellationRequested);
        shapeService.Completion.SetResult(new([], 1, false, 0, 1));
        await pending;
        Assert.True(tools.IsIdle);
        Assert.Null(tools.Shapes.LastOutput);
        tools.Selected = InspectionToolKind.Pattern;
        Assert.True(tools.Pattern.Train.CanExecute(null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedShapeResultsFollowSelectedToolAndDoNotChangeHistory(bool stoppedLiveFrame)
    {
        var session = new EditorSession();
        session.InitializeImage(new("fixture", "fixture", 256, 256), ViewportTransform.Identity);
        var history = session.HistoryBytes;
        var service = new ShapeService();
        service.Completion.SetResult(new([new(PrimitiveShapeKind.Rectangle, new(50, 50), 0, 1, 1600,
            [new(30, 30), new(70, 30), new(70, 70), new(30, 70)])], 1, false, 0, 1));
        using var tools = new InspectionToolsViewModel(new(new PatternService()), new(service));
        tools.Update(session.State, 1, stoppedLiveFrame, true);
        tools.Selected = InspectionToolKind.PrimitiveShapes;
        await tools.Shapes.Detect.ExecuteAsync();
        Assert.Single(tools.Overlay!.Matches);
        tools.Selected = InspectionToolKind.Pattern;
        Assert.Null(tools.Overlay);
        tools.Selected = InspectionToolKind.PrimitiveShapes;
        Assert.Single(tools.Overlay!.Matches);
        Assert.Equal(history, session.HistoryBytes);
    }

    [Fact]
    public async Task EdgeFewShotTool_RegistersSingleBoardPreset_TrainsAndClassifiesAllRois()
    {
        var session = new EditorSession();
        session.InitializeImage(new("single-board", "single-board", 1000, 560), ViewportTransform.Identity);
        for (int i = 0; i < 5; i++)
        {
            var id = session.Add(new(new(110 + i * 155, 96), 64, 64, 0));
            session.SetProperties(id, $"학습-정상-{i + 1}", session.State.Rois[id].Geometry, null, true);
        }
        for (int i = 0; i < 5; i++)
        {
            var id = session.Add(new(new(110 + i * 155, 256), 64, 64, 0));
            session.SetProperties(id, $"학습-불량-{i + 1}", session.State.Rois[id].Geometry, null, true);
        }
        var testId = session.Add(new(new(665, 426), 64, 64, -2.0));
        session.SetProperties(testId, "테스트-미등록오염(OOD)", session.State.Rois[testId].Geometry, null, true);

        var fewShotVm = new EdgeFewShotViewModel(new FakeFewShotService());
        using var tools = new InspectionToolsViewModel(new(new PatternService()), new(new ShapeService()), fewShotVm);
        tools.Selected = InspectionToolKind.EdgeFewShot;
        tools.Update(session.State, 1, false, true);

        fewShotVm.RegisterSingleBoardPresetSamples();
        Assert.True(fewShotVm.Train.CanExecute(null));

        await fewShotVm.Train.ExecuteAsync();
        Assert.True(fewShotVm.HasTrainedModel);
        Assert.True(fewShotVm.ClassifyAll.CanExecute(null));

        await fewShotVm.ClassifyAll.ExecuteAsync();
        Assert.NotNull(fewShotVm.LastClassification);
        Assert.Equal(11, fewShotVm.Overlay!.Matches.Length);
    }

    [Fact]
    public async Task InspectionSuiteTool_ExecutesSelectedRoiAndMaster8kSuite_AndRendersOverlay()
    {
        var session = new EditorSession();
        session.InitializeImage(new("suite-fixture", "suite-fixture", 1024, 1024), ViewportTransform.Identity);
        var roiId = session.Add(new(new(256, 256), 120, 80, 0));
        session.Select([roiId]);

        var suiteVm = new InspectionSuiteViewModel(new FakeInspectionSuiteService());
        using var tools = new InspectionToolsViewModel(new(new PatternService()), new(new ShapeService()), null, suiteVm);
        tools.Selected = InspectionToolKind.InspectionSuite;
        tools.Update(session.State, 1, false, true);

        Assert.True(tools.ShowSuite);
        Assert.Equal(suiteVm.Registry.All.Count, suiteVm.Algorithms.Count);
        Assert.True(suiteVm.ExecuteSelected.CanExecute(null));

        suiteVm.SelectedAlgorithm = InspectionSuiteAlgorithmKind.Metrology;
        await suiteVm.ExecuteSelected.ExecuteAsync();
        Assert.NotNull(suiteVm.LastOutput);
        Assert.Single(tools.Overlay!.Matches);
        Assert.Single(tools.Overlay.Matches[0].DiagnosticGroups);

        await suiteVm.RunMaster8kBenchmark.ExecuteAsync();
        Assert.Equal(7, tools.Overlay!.Matches.Length);
    }

    [Fact]
    public async Task InspectionSuiteSampleButtons_LoadAllSevenGeneratedAlgorithmScenesAndExecuteInspection()
    {
        var loader = new SourceImageLoader(new ThrowingFileLoader());
        var realService = new InspectionSuiteService(loader, () => null);
        using var suiteVm = new InspectionSuiteViewModel(realService);

        var requestedKinds = new List<InspectionSuiteAlgorithmKind>();
        suiteVm.SampleLoadRequested += kind => requestedKinds.Add(kind);

        suiteVm.LoadMetrologySample.Execute(null);
        suiteVm.LoadGoldenVariationSample.Execute(null);
        suiteVm.LoadBlobRleSample.Execute(null);
        suiteVm.LoadCurvilinearSample.Execute(null);
        suiteVm.LoadFewShotSample.Execute(null);
        suiteVm.LoadPatchAnomalySample.Execute(null);
        suiteVm.LoadCalibrationSample.Execute(null);

        Assert.Equal(
            [
                InspectionSuiteAlgorithmKind.Metrology,
                InspectionSuiteAlgorithmKind.GoldenVariation,
                InspectionSuiteAlgorithmKind.BlobRle,
                InspectionSuiteAlgorithmKind.Curvilinear,
                InspectionSuiteAlgorithmKind.FewShot,
                InspectionSuiteAlgorithmKind.PatchAnomaly,
                InspectionSuiteAlgorithmKind.Calibration
            ],
            requestedKinds);

        // Verify that SourceImageLoader + InspectionSuiteSampleTileSource serves all 7 scenes and real InspectionSuiteService runs on them
        (InspectionSuiteAlgorithmKind Kind, string Key, PointD TargetCenter, double W, double H)[] scenes =
        [
            (InspectionSuiteAlgorithmKind.Metrology, "suite-sample:metrology", new(320.35, 400.25), 130, 220),
            (InspectionSuiteAlgorithmKind.GoldenVariation, "suite-sample:golden", new(600.30, 570.25), 136, 136),
            (InspectionSuiteAlgorithmKind.BlobRle, "suite-sample:blob", new(260.5, 400.5), 210, 210),
            (InspectionSuiteAlgorithmKind.Curvilinear, "suite-sample:curvilinear", new(588.0, 250.0), 780, 110),
            (InspectionSuiteAlgorithmKind.FewShot, "suite-sample:fewshot", new(250.0, 630.0), 110, 90),
            (InspectionSuiteAlgorithmKind.PatchAnomaly, "suite-sample:padim", new(600.2, 570.2), 132, 132),
            (InspectionSuiteAlgorithmKind.Calibration, "suite-sample:calibration", new(600.0, 400.0), 760, 560)
        ];

        foreach (var (kind, key, center, w, h) in scenes)
        {
            var imgRef = new ImageReference(key, key, InspectionSuiteSampleTileSource.DefaultWidth, InspectionSuiteSampleTileSource.DefaultHeight);
            var target = new InspectionSuiteRoiInput(
                Guid.NewGuid(),
                key,
                center,
                w,
                h,
                0.0,
                [
                    new(center.X - w * 0.5, center.Y - h * 0.5),
                    new(center.X + w * 0.5, center.Y - h * 0.5),
                    new(center.X + w * 0.5, center.Y + h * 0.5),
                    new(center.X - w * 0.5, center.Y + h * 0.5)
                ],
                0);

            var output = await realService.ExecuteRoiInspectionAsync(
                imgRef,
                live: false,
                kind,
                [target],
                [],
                new InspectionSuiteParameters(),
                CancellationToken.None);

            Assert.Single(output.Items);
        }
    }

    [Fact]
    public async Task MetrologyModesAndLivePreview_GenerateGeometricGuidesAndAccurateSubpixelResults()
    {
        var loader = new SourceImageLoader(new ThrowingFileLoader());
        var realService = new InspectionSuiteService(loader, () => null);
        using var suiteVm = new InspectionSuiteViewModel(realService);

        var session = new EditorSession();
        var imgRef = new ImageReference(
            "suite-sample:metrology",
            "Metrology Sample",
            InspectionSuiteSampleTileSource.DefaultWidth,
            InspectionSuiteSampleTileSource.DefaultHeight);
        session.InitializeImage(imgRef, ViewportTransform.Identity);

        Guid traceRoiId = session.Add(new(new(320.35, 400.25), 130, 220, 10.0));
        session.SetProperties(traceRoiId, "트레이스 폭·직선 계측", session.State.Rois[traceRoiId].Geometry, null, true);
        Guid circleRoiId = session.Add(new(new(820.40, 400.60), 230, 230, 0.0));
        session.SetProperties(circleRoiId, "원형 패드 외경·진원도", session.State.Rois[circleRoiId].Geometry, null, true);
        session.Select([traceRoiId]);

        suiteVm.Update(new(session.State, 1, false, true));

        // 1. Live geometric guide overlay is immediately populated on ROI selection even before clicking Execute
        Assert.True(suiteVm.IsMetrologySelected);
        Assert.False(suiteVm.UsesReferenceRois);
        Assert.NotNull(suiteVm.Overlay);
        Assert.Single(suiteVm.Overlay!.Matches);
        Assert.False(suiteVm.Overlay.Matches[0].InnerContours.IsEmpty);

        // 2. Switching MetrologyMode updates geometric guides immediately
        suiteVm.MetrologyMode = MetrologyMeasurementMode.RakeLineFit;
        suiteVm.CaliperRakeCount = 15;
        Assert.NotNull(suiteVm.Overlay);
        Assert.True(suiteVm.Overlay!.Matches[0].InnerContours.Length >= 15);

        // 3. Execute RakeLineFit on the 10-degree rotated trace and verify single-edge lock (~100 deg line direction)
        await suiteVm.ExecuteSelected.ExecuteAsync();
        Assert.NotNull(suiteVm.LastOutput);
        Assert.True(suiteVm.LastOutput!.Items[0].IsPass);
        Assert.False(suiteVm.LastOutput.Items[0].GuideContours.IsEmpty);

        // 4. Execute SpokeCircleFit on the circular pad ROI
        session.Select([circleRoiId]);
        suiteVm.Update(new(session.State, 1, false, true));
        suiteVm.MetrologyMode = MetrologyMeasurementMode.SpokeCircleFit;
        await suiteVm.ExecuteSelected.ExecuteAsync();
        Assert.NotNull(suiteVm.LastOutput);
        Assert.True(suiteVm.LastOutput!.Items[0].IsPass);
        Assert.Contains("R=96.", suiteVm.LastOutput.Items[0].DetailLine);
    }

    [Fact]
    public void GlobalInspectionSearchFullImageOption_SynchronizesAcrossInspectionTools()
    {
        var session = new EditorSession();
        session.InitializeImage(new("fixture", "fixture", 1024, 768), ViewportTransform.Identity);
        session.Add(new(new(200, 200), 80, 60, 0));

        using var tools = new InspectionToolsViewModel(new(new PatternService()), new(new ShapeService()));
        tools.Update(session.State, 1, false, true);

        Assert.True(tools.UseSelection);
        Assert.False(tools.SearchFullImage);
        Assert.Contains("선택 ROI 검색 영역", tools.SearchScopeSummaryText);

        tools.SearchFullImage = true;
        Assert.False(tools.UseSelection);
        Assert.False(tools.Pattern.UseSelection);
        Assert.False(tools.Shapes.UseSelection);
        Assert.Contains("전체 이미지 전역 검사 (1,024 × 768 px)", tools.SearchScopeSummaryText);
        Assert.Equal("⚡ 전체 이미지 검사 실행", tools.QuickInspectLabel);

        tools.SearchFullImage = false;
        Assert.True(tools.UseSelection);
        Assert.True(tools.Pattern.UseSelection);
        Assert.True(tools.Shapes.UseSelection);
    }

    private sealed class ThrowingFileLoader : IImageLoader
    {
        public Task<ITileSource> LoadAsync(ImageReference reference, IProgress<string> progress, CancellationToken cancellationToken)
            => throw new InvalidOperationException("절차적 샘플 타일 소스는 파일 로더를 호출하면 안 됩니다.");
    }

    private sealed class FakeInspectionSuiteService : IInspectionSuiteService
    {
        public Task<InspectionSuiteExecutionOutput> ExecuteRoiInspectionAsync(
            ImageReference image,
            bool live,
            InspectionSuiteAlgorithmKind algorithm,
            IReadOnlyList<InspectionSuiteRoiInput> targetRois,
            IReadOnlyList<InspectionSuiteRoiInput> referenceRois,
            InspectionSuiteParameters parameters,
            CancellationToken token)
        {
            var items = targetRois.Select(r => new InspectionSuiteItemResult(
                r.RoiId,
                r.Name,
                true,
                "폭=42.000px",
                "1D 캘리퍼 폭=42.000px",
                r.Center,
                r.AngleRadians,
                r.Outline,
                [],
                [r.Center],
                [],
                [],
                150.0)).ToImmutableArray();
            return Task.FromResult(new InspectionSuiteExecutionOutput(algorithm, "Metrology 완료", "상세 결과", items, 0.5));
        }

        public Task<InspectionSuiteExecutionOutput> RunMaster8kSuiteAsync(CancellationToken token)
        {
            var items = Enumerable.Range(1, 7).Select(idx => new InspectionSuiteItemResult(
                Guid.NewGuid(),
                $"Zone-{idx}",
                true,
                "PASS",
                "8K 검증 통과",
                new PointD(idx * 500, idx * 500),
                0.0,
                [new(0, 0), new(100, 0), new(100, 100), new(0, 100)],
                [],
                [new PointD(idx * 500, idx * 500)],
                [],
                [],
                200.0)).ToImmutableArray();
            return Task.FromResult(new InspectionSuiteExecutionOutput(InspectionSuiteAlgorithmKind.Metrology, "8K 7종 PASS", "전수 통과", items, 12.0));
        }
    }

    private sealed class FakeFewShotService : IEdgeFewShotService
    {
        public Task<EdgeFewShotTrainingOutput> TrainAsync(ImageReference image, bool live, IReadOnlyList<EdgeFewShotSampleInput> samples, FewShotTrainOptions options, CancellationToken token)
        {
            var mean = new float[4];
            var classes = new[] { EdgeFewShotViewModel.GoodClass, EdgeFewShotViewModel.DefectClass };
            var prototypes = new float[8];
            var choleskyInv = new float[16];
            choleskyInv[0] = choleskyInv[5] = choleskyInv[10] = choleskyInv[15] = 1f;
            var model = new ClassPrototypeModel(64, 64, 4, options.GemExponentP, options.ShrinkageLambda, 8.5f, 1.0f, mean, classes, prototypes, choleskyInv);
            return Task.FromResult(new EdgeFewShotTrainingOutput(model, samples.Count, 5, 5, 2.4));
        }

        public Task<EdgeFewShotClassificationOutput> ClassifyAsync(ImageReference image, bool live, ClassPrototypeModel model, IReadOnlyList<EdgeFewShotTargetInput> targets, CancellationToken token)
        {
            var items = targets.Select((t, idx) =>
            {
                bool isOod = t.Name.Contains("OOD", StringComparison.OrdinalIgnoreCase);
                var prediction = isOod
                    ? FewShotPredictionResult.Create(EdgeFewShotViewModel.DefectClass, 0.25f, 9.69f, true, 1)
                    : FewShotPredictionResult.Create(idx < 5 ? EdgeFewShotViewModel.GoodClass : EdgeFewShotViewModel.DefectClass, 0.92f, 5.80f, false, idx < 5 ? 0 : 1);
                return new EdgeFewShotItemOutput(t.RoiId, t.Name, t.Pose, t.Outline, prediction, 180.0);
            }).ToImmutableArray();
            return Task.FromResult(new EdgeFewShotClassificationOutput(items, 2.0, 180.0));
        }
    }

    private sealed class ShapeService : IPrimitiveShapeDetectionService
    {
        public CancellationToken Token
        {
            get; private set;
        }
        public TaskCompletionSource<PrimitiveDetectionOutput> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<PrimitiveDetectionOutput> DetectAsync(ImageReference image, bool live, RoiGeometry? region, PrimitiveDetectionOptions options, CancellationToken token, RoiExclusion? exclusion = null)
        {
            Token = token;
            return Completion.Task;
        }
    }

    private sealed class PatternService : IShapeMatchingService
    {
        public Task<ShapeTrainingOutput> TrainAsync(ImageReference image, bool live, RoiGeometry roi, TrainingOptions options, CancellationToken token, RoiExclusion? exclusion = null)
            => throw new InvalidOperationException("패턴 서비스 실행은 이 조정 테스트의 대상이 아닙니다.");

        public Task<ShapeSearchOutput> SearchAsync(ImageReference image, bool live, ShapeModel model, RoiGeometry? region, ShapeSearchOptions options, CancellationToken token, RoiExclusion? exclusion = null)
            => throw new InvalidOperationException("동시 실행되면 안 됩니다.");
    }
}
