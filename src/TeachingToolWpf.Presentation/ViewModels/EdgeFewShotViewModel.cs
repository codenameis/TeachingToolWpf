using System.Collections.Immutable;
using System.Numerics;
using TeachingToolWpf.Commands;
using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Infrastructure;
using TeachingToolWpf.Models;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Domain;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionCanvas.Rendering;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.ViewModels;

public sealed class EdgeFewShotViewModel : ObservableObject, IVisionAlgorithmPreview
{
    public static readonly FewShotClassId GoodClass = FewShotClassId.Create(1, "Good");
    public static readonly FewShotClassId DefectClass = FewShotClassId.Create(2, "BackBad");

    private static readonly Vector4 GoodOverlayColor = new(0.06f, 0.72f, 0.51f, 1.0f);
    private static readonly Vector4 DefectOverlayColor = new(0.96f, 0.62f, 0.04f, 1.0f);
    private static readonly Vector4 OodOverlayColor = new(0.94f, 0.27f, 0.27f, 1.0f);

    private readonly IEdgeFewShotService _service;
    private readonly List<EdgeFewShotSampleInput> _samples = [];
    private float _gemExponentP = FewShotTrainOptions.DefaultGemExponentP;
    private float _shrinkageLambda = FewShotTrainOptions.DefaultShrinkageLambda;
    private int _subPrototypesPerClass = FewShotTrainOptions.DefaultSubPrototypesPerClass;
    private float _oodThresholdMultiplier = FewShotTrainOptions.DefaultOodThresholdMultiplier;
    private double _manualOodThreshold;
    private DocumentState _state = DocumentState.Empty;
    private ImageReference? _image;
    private long _generation;
    private long _epoch;
    private bool _live;
    private bool _canExecute;
    private bool _hasInputErrors;
    private bool _showOverlay = true;
    private bool _disposed;
    private CancellationTokenSource? _cancellation;
    private ResultOverlay? _overlay;
    private ClassPrototypeModel? _model;

    public EdgeFewShotViewModel(IEdgeFewShotService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;

        RegisterGood = new(() => RegisterSelectedRois(GoodClass),
            () => IsIdle && _canExecute && _image is not null && GetSelectedLeafRois().Count > 0);
        RegisterDefect = new(() => RegisterSelectedRois(DefectClass),
            () => IsIdle && _canExecute && _image is not null && GetSelectedLeafRois().Count > 0);
        RegisterBoardPreset = new(RegisterSingleBoardPresetSamples,
            () => IsIdle && _canExecute && _image is not null && GetAllLeafRois().Count >= 2);
        ClearSamples = new(ClearAllSamples,
            () => IsIdle && (_samples.Count > 0 || _model is not null));
        Train = new(TrainAsync, error => PresentWarning(error.Message),
            () => IsIdle && _canExecute && _image is not null && _samples.Count >= 2 && OptionsError.Length == 0);
        ClassifySelected = new(() => ClassifyAsync(selectedOnly: true), error => PresentWarning(error.Message),
            () => IsIdle && _canExecute && _image is not null && _model is not null && (GetSelectedLeafRois().Count > 0 || GetAllLeafRois().Count > 0));
        ClassifyAll = new(() => ClassifyAsync(selectedOnly: false), error => PresentWarning(error.Message),
            () => IsIdle && _canExecute && _image is not null && _model is not null && GetAllLeafRois().Count > 0);
        Cancel = new(() => _cancellation?.Cancel(), () => IsBusy);
        Locate = new(() => LocateRequested?.Invoke(BoundsD.FromPoints(_overlay!.Matches.SelectMany(marker => marker.Bounds))),
            () => IsIdle && _canExecute && _overlay?.Matches.IsEmpty == false);
    }

    public event Action? Changed;
    public event Action<BoundsD>? LocateRequested;
    public event Action<RoiAlgorithmAssignment>? RoiAssignmentRecorded;
    public event Action<StructuredInspectionLogEntry>? StructuredLogRecorded;

    public RelayCommand RegisterGood { get; }
    public RelayCommand RegisterDefect { get; }
    public RelayCommand RegisterBoardPreset { get; }
    public RelayCommand ClearSamples { get; }
    public AsyncCommand Train { get; }
    public AsyncCommand ClassifySelected { get; }
    public AsyncCommand ClassifyAll { get; }
    public RelayCommand Cancel { get; }
    public RelayCommand Locate { get; }

    public bool IsBusy => _cancellation is not null;
    public bool IsIdle => !_disposed && !IsBusy;
    public bool HasTrainedModel => _model is not null;

    public ResultOverlay? Overlay => ShowOverlay ? _overlay : null;

    public string Status { get; private set; } = "단일 보드 이미지에서 정상(Good)·불량(BackBad) ROI를 등록한 뒤 학습 및 분류를 실행하세요.";
    public string SampleSummary { get; private set; } = "등록된 학습 샘플 없음 (최소 2개 이상 등록 필요)";
    public string ModelSummary { get; private set; } = "학습 모델 없음";
    public string Results { get; private set; } = "분류 결과 없음";

    public EdgeFewShotTrainingOutput? LastTraining { get; private set; }
    public EdgeFewShotClassificationOutput? LastClassification { get; private set; }

    public float GemExponentP
    {
        get => _gemExponentP;
        set
        {
            if (Set(ref _gemExponentP, value))
            {
                InvalidateTrainedModel();
            }
        }
    }

    public float ShrinkageLambda
    {
        get => _shrinkageLambda;
        set
        {
            if (Set(ref _shrinkageLambda, value))
            {
                InvalidateTrainedModel();
            }
        }
    }

    public int SubPrototypesPerClass
    {
        get => _subPrototypesPerClass;
        set
        {
            if (Set(ref _subPrototypesPerClass, value))
            {
                InvalidateTrainedModel();
            }
        }
    }

    public float OodThresholdMultiplier
    {
        get => _oodThresholdMultiplier;
        set
        {
            if (Set(ref _oodThresholdMultiplier, value))
            {
                InvalidateTrainedModel();
            }
        }
    }

    public double ManualOodThreshold
    {
        get => _manualOodThreshold;
        set
        {
            if (Set(ref _manualOodThreshold, value))
            {
                InvalidateTrainedModel();
            }
        }
    }

    public bool HasInputErrors
    {
        get => _hasInputErrors;
        set
        {
            if (Set(ref _hasInputErrors, value))
            {
                Publish();
            }
        }
    }

    public bool ShowOverlay
    {
        get => _showOverlay;
        set
        {
            if (Set(ref _showOverlay, value))
            {
                Changed?.Invoke();
            }
        }
    }

    public string OptionsError
    {
        get
        {
            if (HasInputErrors)
            {
                return "숫자 입력을 확인하세요.";
            }

            if (!double.IsFinite(_manualOodThreshold) || _manualOodThreshold < 0.0 || _manualOodThreshold > 1000.0)
            {
                return "수동 OOD 임계치는 0~1000 범위여야 합니다 (0은 자동 보정).";
            }

            try
            {
                _ = BuildValidatedOptions();
                return "";
            }
            catch (ArgumentException ex)
            {
                return ex.Message;
            }
        }
    }

    public string ParameterPreviewSummary
    {
        get
        {
            if (OptionsError.Length > 0)
            {
                return "설정 범위 확인 필요: " + OptionsError;
            }

            var oodText = _manualOodThreshold > 0.0
                ? $"수동 OOD 임계치 {_manualOodThreshold:F2}"
                : $"자동 OOD 임계 배수 ×{OodThresholdMultiplier:F2}";
            return $"[파이프라인 요약] ROI {FewShotTrainOptions.DefaultRoiWidth}×{FewShotTrainOptions.DefaultRoiHeight} · GeM p={GemExponentP:F1} · K-Means++ K={SubPrototypesPerClass} · Ledoit-Wolf λ={ShrinkageLambda:F2} · {oodText}";
        }
    }

    public void Update(InspectionUpdate update)
    {
        var state = update.State;
        var canExecute = update.CanExecute && update.ToolAvailable;
        var imageChanged = _image != state.Image || _generation != update.Generation;

        _state = state;
        _image = state.Image;
        _generation = update.Generation;
        _live = update.Live;
        _canExecute = canExecute;

        if (imageChanged)
        {
            ClearAllSamples();
            return;
        }

        Refresh();
    }

    public void RegisterSingleBoardPresetSamples()
    {
        var leaves = GetAllLeafRois();
        if (leaves.Count < 2)
        {
            PresentWarning("단일 보드에서 학습할 ROI가 부족합니다. 먼저 '소량 분류 샘플' 버튼을 누르거나 ROI를 추가하세요.");
            return;
        }

        _samples.Clear();
        _model = null;
        LastTraining = null;
        LastClassification = null;

        foreach (var roi in leaves)
        {
            var pose = ToFixturePose(roi.Geometry, FewShotTrainOptions.DefaultRoiWidth, FewShotTrainOptions.DefaultRoiHeight);
            if (roi.Name.Contains("학습-정상", StringComparison.OrdinalIgnoreCase) ||
                roi.Name.Contains("Train-Good", StringComparison.OrdinalIgnoreCase))
            {
                _samples.Add(new EdgeFewShotSampleInput(roi.Id, roi.Name, pose, GoodClass));
            }
            else if (roi.Name.Contains("학습-불량", StringComparison.OrdinalIgnoreCase) ||
                     roi.Name.Contains("Train-BackBad", StringComparison.OrdinalIgnoreCase))
            {
                _samples.Add(new EdgeFewShotSampleInput(roi.Id, roi.Name, pose, DefectClass));
            }
        }

        if (_samples.Count < 2)
        {
            var nonTestLeaves = leaves
                .Where(roi => !roi.Name.Contains("테스트", StringComparison.OrdinalIgnoreCase) &&
                              !roi.Name.Contains("Test", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var candidates = nonTestLeaves.Count >= 2 ? nonTestLeaves : leaves;
            int half = candidates.Count / 2;
            for (int i = 0; i < candidates.Count; i++)
            {
                var roi = candidates[i];
                var pose = ToFixturePose(roi.Geometry, FewShotTrainOptions.DefaultRoiWidth, FewShotTrainOptions.DefaultRoiHeight);
                _samples.Add(new EdgeFewShotSampleInput(roi.Id, roi.Name, pose, i < half ? GoodClass : DefectClass));
            }
        }

        UpdateSampleSummary();
        BuildSamplePreviewOverlay();
        foreach (var sample in _samples)
        {
            EmitFewShotSampleAssignment(sample);
        }
        Status = $"학습용 ROI {_samples.Count}개를 등록했습니다. ‘소량 샘플 모델 학습 실행’ 버튼을 눌러 프로토타입과 공분산을 학습하세요.";
        Publish();
    }

    private void RegisterSelectedRois(FewShotClassId classId)
    {
        var selected = GetSelectedLeafRois();
        if (selected.Count == 0)
        {
            PresentWarning("캔버스에서 등록할 ROI를 먼저 선택하세요.");
            return;
        }

        foreach (var roi in selected)
        {
            var pose = ToFixturePose(roi.Geometry, FewShotTrainOptions.DefaultRoiWidth, FewShotTrainOptions.DefaultRoiHeight);
            int existingIndex = _samples.FindIndex(s => s.RoiId == roi.Id);
            var item = new EdgeFewShotSampleInput(roi.Id, roi.Name, pose, classId);
            if (existingIndex >= 0)
            {
                _samples[existingIndex] = item;
            }
            else
            {
                _samples.Add(item);
            }
            EmitFewShotSampleAssignment(item);
        }

        _model = null;
        LastTraining = null;
        LastClassification = null;
        UpdateSampleSummary();
        BuildSamplePreviewOverlay();
        Status = $"선택 ROI {selected.Count}개를 ‘{classId.Name}’ 클래스로 등록했습니다. (총 {_samples.Count}개)";
        Publish();
    }

    private void ClearAllSamples()
    {
        _epoch++;
        _cancellation?.Cancel();
        _samples.Clear();
        _model = null;
        _overlay = null;
        LastTraining = null;
        LastClassification = null;
        SampleSummary = "등록된 학습 샘플 없음 (최소 2개 이상 등록 필요)";
        ModelSummary = "학습 모델 없음";
        Status = "샘플과 학습 모델을 초기화했습니다. ROI를 선택해 정상(Good)/불량(BackBad)으로 등록하세요.";
        Results = "분류 결과 없음";
        Publish();
    }

    private async Task TrainAsync()
    {
        var image = _image!;
        var live = _live;
        var options = BuildValidatedOptions();
        var snapshotSamples = _samples.ToArray();

        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        var epoch = ++_epoch;

        Status = "소량 샘플 특징 추출 및 Ledoit-Wolf 수축 공분산 학습 중…";
        Publish();

        try
        {
            var output = await _service.TrainAsync(image, live, snapshotSamples, options, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_disposed || epoch != _epoch)
            {
                return;
            }

            _model = output.Model;
            LastTraining = output;
            ModelSummary = $"학습 완료 ({output.Milliseconds:F2} ms) · 특징 {output.Model.FeatureDimension}차원 · 프로토타입 {output.Model.TotalPrototypeCount}개 · OOD 마할라노비스 임계치 {output.Model.OodMahalanobisThreshold:F2}";
            Status = $"소량 샘플 {output.SampleCount}장(Good {output.GoodCount}, BackBad {output.DefectCount}) 학습 완료 · {output.Milliseconds:F2} ms 소요";
            BuildSamplePreviewOverlay();
            Publish();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!_disposed && epoch == _epoch)
            {
                PresentWarning("학습이 취소되었습니다.");
            }
        }
        catch (Exception ex)
        {
            if (!_disposed && epoch == _epoch)
            {
                PresentWarning("학습 실패 · " + ex.Message);
            }
        }
        finally
        {
            _cancellation = null;
            Publish();
        }
    }

    private async Task ClassifyAsync(bool selectedOnly)
    {
        var image = _image!;
        var live = _live;
        var model = _model!;
        var selectedRois = selectedOnly ? GetSelectedLeafRois() : [];
        var targetRois = selectedRois.Count > 0 ? selectedRois : GetAllLeafRois();
        if (targetRois.Count == 0)
        {
            PresentWarning("분류할 ROI가 없습니다.");
            return;
        }

        var targets = targetRois.Select(roi => new EdgeFewShotTargetInput(
            roi.Id,
            roi.Name,
            ToFixturePose(roi.Geometry, model.RoiWidth, model.RoiHeight),
            roi.Geometry.Outline.ToImmutableArray())).ToArray();

        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        var epoch = ++_epoch;

        Status = $"대상 ROI {targets.Length}개 마할라노비스 거리 및 OOD 이상치 판정 중…";
        Publish();

        try
        {
            var output = await _service.ClassifyAsync(image, live, model, targets, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_disposed || epoch != _epoch)
            {
                return;
            }

            LastClassification = output;
            int goodCount = output.Items.Count(item => !item.Prediction.IsOutOfDistribution && item.Prediction.PredictedClass == GoodClass);
            int defectCount = output.Items.Count(item => !item.Prediction.IsOutOfDistribution && item.Prediction.PredictedClass == DefectClass);
            int oodCount = output.Items.Count(item => item.Prediction.IsOutOfDistribution);

            Status = $"분류 완료 ({output.Items.Length}개 ROI) · 정상(Good) {goodCount} · 불량(BackBad) {defectCount} · 미등록 OOD {oodCount} · 평균 추론 {output.MeanMicroseconds:F1} µs/ROI";
            Results = string.Join("\n", output.Items.Select((item, idx) =>
            {
                var p = item.Prediction;
                var verdictTag = p.IsOutOfDistribution
                    ? $"[미등록 이상치 OOD · 최근접 {p.NearestInDistributionClass.Name}]"
                    : $"[{p.PredictedClass.Name}]";
                return $"#{idx + 1} {item.Name} → {verdictTag} · 마할라노비스 d_M={p.MahalanobisDistance:F2} (임계 {model.OodMahalanobisThreshold:F2}) · 신뢰도 {p.Confidence:P1} · {item.Microseconds:F1} µs";
            }));

            var boundary = ImmutableArray.Create(
                new PointD(0, 0),
                new PointD(image.Width, 0),
                new PointD(image.Width, image.Height),
                new PointD(0, image.Height));

            var markers = output.Items.Select((item, idx) =>
            {
                var p = item.Prediction;
                var markerColor = p.IsOutOfDistribution
                    ? OodOverlayColor
                    : p.PredictedClass == GoodClass ? GoodOverlayColor : DefectOverlayColor;
                var label = p.IsOutOfDistribution
                    ? $"#{idx + 1} OOD ({p.MahalanobisDistance:F2})"
                    : $"#{idx + 1} {p.PredictedClass.Name} ({p.MahalanobisDistance:F2})";
                return new MatchMarker(
                    item.Outline,
                    new PointD(item.Pose.CenterX, item.Pose.CenterY),
                    item.Pose.AngleRad,
                    label,
                    markerColor,
                    []);
            }).ToImmutableArray();

            var summaryColor = oodCount > 0 ? OodOverlayColor : defectCount > 0 ? DefectOverlayColor : GoodOverlayColor;
            _overlay = new ResultOverlay(Status, summaryColor, boundary, markers);
            foreach (var item in output.Items)
            {
                EmitFewShotClassificationResult(item, model);
            }
            Publish();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!_disposed && epoch == _epoch)
            {
                PresentWarning("분류가 취소되었습니다.");
            }
        }
        catch (Exception ex)
        {
            if (!_disposed && epoch == _epoch)
            {
                PresentWarning("분류 실패 · " + ex.Message);
            }
        }
        finally
        {
            _cancellation = null;
            Publish();
        }
    }

    private void EmitFewShotSampleAssignment(EdgeFewShotSampleInput sample)
    {
        bool isGood = sample.ClassId == GoodClass;
        RoiAssignmentRecorded?.Invoke(new RoiAlgorithmAssignment(
            RoiId: sample.RoiId,
            RoiName: sample.Name,
            Tool: InspectionToolKind.EdgeFewShot,
            PatternMethod: null,
            SuiteAlgorithm: null,
            AlgorithmBadge: RoiAlgorithmAssignment.FormatAlgorithmBadge(InspectionToolKind.EdgeFewShot),
            AlgorithmAccent: RoiAlgorithmAssignment.GetAlgorithmAccent(InspectionToolKind.EdgeFewShot),
            ParameterSummary: ParameterPreviewSummary,
            VerdictBadge: isGood ? "학습:정상" : "학습:불량",
            VerdictAccent: isGood ? "#34D399" : "#FBBF24",
            LastResultSummary: $"Few-Shot 학습 샘플 등록 ({sample.ClassId.Name})"));
    }

    private void EmitFewShotClassificationResult(EdgeFewShotItemOutput item, ClassPrototypeModel model)
    {
        var p = item.Prediction;
        bool isPass = !p.IsOutOfDistribution && p.PredictedClass == GoodClass;
        var verdict = p.IsOutOfDistribution
            ? InspectionVerdict.Warning
            : isPass ? InspectionVerdict.Pass : InspectionVerdict.Fail;
        string verdictBadge = p.IsOutOfDistribution ? "OOD" : isPass ? "PASS" : "NG";
        string verdictAccent = p.IsOutOfDistribution ? "#FBBF24" : isPass ? "#34D399" : "#F87171";
        string badge = RoiAlgorithmAssignment.FormatAlgorithmBadge(InspectionToolKind.EdgeFewShot);
        string accent = RoiAlgorithmAssignment.GetAlgorithmAccent(InspectionToolKind.EdgeFewShot);
        string scoreSummary = $"d_M={p.MahalanobisDistance:F2} (임계 {model.OodMahalanobisThreshold:F2}) · 신뢰도 {p.Confidence:P0}";
        string poseSummary = $"X={item.Pose.CenterX:F1}, Y={item.Pose.CenterY:F1}";
        double elapsedMs = item.Microseconds / 1000.0;

        RoiAssignmentRecorded?.Invoke(new RoiAlgorithmAssignment(
            RoiId: item.RoiId,
            RoiName: item.Name,
            Tool: InspectionToolKind.EdgeFewShot,
            PatternMethod: null,
            SuiteAlgorithm: null,
            AlgorithmBadge: badge,
            AlgorithmAccent: accent,
            ParameterSummary: ParameterPreviewSummary,
            VerdictBadge: verdictBadge,
            VerdictAccent: verdictAccent,
            LastResultSummary: $"{verdictBadge} ({(p.IsOutOfDistribution ? "OOD" : p.PredictedClass.Name)}) · {scoreSummary}"));

        StructuredLogRecorded?.Invoke(new StructuredInspectionLogEntry(
            EntryId: Guid.NewGuid(),
            Timestamp: DateTimeOffset.Now,
            RoiId: item.RoiId,
            RoiName: item.Name,
            Tool: InspectionToolKind.EdgeFewShot,
            AlgorithmBadge: badge,
            Verdict: verdict,
            VerdictBadge: verdictBadge,
            VerdictAccent: verdictAccent,
            ScoreSummary: scoreSummary,
            PoseSummary: poseSummary,
            ElapsedMilliseconds: elapsedMs,
            TargetBounds: item.Outline.IsEmpty ? null : BoundsD.FromPoints(item.Outline)));
    }

    private FewShotTrainOptions BuildValidatedOptions()
    {
        return FewShotTrainOptions.Create(
            roiWidth: FewShotTrainOptions.DefaultRoiWidth,
            roiHeight: FewShotTrainOptions.DefaultRoiHeight,
            gemExponentP: _gemExponentP,
            shrinkageLambda: _shrinkageLambda,
            subPrototypesPerClass: _subPrototypesPerClass,
            oodThresholdMultiplier: _oodThresholdMultiplier,
            explicitOodThreshold: _manualOodThreshold > 0.0 ? (float)_manualOodThreshold : 0f);
    }

    private void UpdateSampleSummary()
    {
        if (_samples.Count == 0)
        {
            SampleSummary = "등록된 학습 샘플 없음 (최소 2개 이상 등록 필요)";
            return;
        }

        int goodCount = _samples.Count(s => s.ClassId == GoodClass);
        int defectCount = _samples.Count(s => s.ClassId == DefectClass);
        SampleSummary = $"총 {_samples.Count}개 등록됨 · 정상(Good) {goodCount}개 · 불량(BackBad) {defectCount}개";
    }

    private void BuildSamplePreviewOverlay()
    {
        if (_image is not { } image || _samples.Count == 0)
        {
            _overlay = null;
            return;
        }

        var boundary = ImmutableArray.Create(
            new PointD(0, 0),
            new PointD(image.Width, 0),
            new PointD(image.Width, image.Height),
            new PointD(0, image.Height));

        var markers = ImmutableArray.CreateBuilder<MatchMarker>(_samples.Count);
        for (int i = 0; i < _samples.Count; i++)
        {
            var sample = _samples[i];
            ImmutableArray<PointD> outline;
            if (_state.Rois.TryGetValue(sample.RoiId, out var roi))
            {
                outline = roi.Geometry.Outline.ToImmutableArray();
            }
            else
            {
                double hw = FewShotTrainOptions.DefaultRoiWidth * 0.5;
                double hh = FewShotTrainOptions.DefaultRoiHeight * 0.5;
                outline =
                [
                    new(sample.Pose.CenterX - hw, sample.Pose.CenterY - hh),
                    new(sample.Pose.CenterX + hw, sample.Pose.CenterY - hh),
                    new(sample.Pose.CenterX + hw, sample.Pose.CenterY + hh),
                    new(sample.Pose.CenterX - hw, sample.Pose.CenterY + hh)
                ];
            }

            var color = sample.ClassId == GoodClass ? GoodOverlayColor : DefectOverlayColor;
            markers.Add(new MatchMarker(
                outline,
                new PointD(sample.Pose.CenterX, sample.Pose.CenterY),
                sample.Pose.AngleRad,
                $"학습:{sample.ClassId.Name}",
                color,
                []));
        }

        _overlay = new ResultOverlay(SampleSummary, GoodOverlayColor, boundary, markers.MoveToImmutable());
    }

    private List<Roi> GetSelectedLeafRois()
    {
        var list = new List<Roi>();
        foreach (var id in _state.Selection)
        {
            if (_state.Rois.TryGetValue(id, out var roi) && !roi.IsGroup)
            {
                list.Add(roi);
            }
        }
        return list;
    }

    private List<Roi> GetAllLeafRois()
    {
        return _state.Rois.Values
            .Where(roi => !roi.IsGroup)
            .OrderBy(roi => System.Math.Round(roi.Geometry.Center.Y / 80.0))
            .ThenBy(roi => roi.Geometry.Center.X)
            .ToList();
    }

    private static RoiFixturePose ToFixturePose(RoiGeometry geometry, int targetWidth, int targetHeight)
    {
        float scaleX = System.Math.Clamp(
            (float)(geometry.Width / System.Math.Max(1, targetWidth)),
            RoiFixturePose.MinScale,
            RoiFixturePose.MaxScale);
        float scaleY = System.Math.Clamp(
            (float)(geometry.Height / System.Math.Max(1, targetHeight)),
            RoiFixturePose.MinScale,
            RoiFixturePose.MaxScale);
        float angleRad = (float)(geometry.Angle * System.Math.PI / 180.0);
        return RoiFixturePose.CreateAnisotropic((float)geometry.Center.X, (float)geometry.Center.Y, angleRad, scaleX, scaleY);
    }

    private void InvalidateTrainedModel()
    {
        _model = null;
        LastTraining = null;
        LastClassification = null;
        ModelSummary = "파라미터 변경됨 · 다시 ‘소량 샘플 모델 학습 실행’을 누르세요.";
        Publish();
    }

    private void PresentWarning(string message)
    {
        Status = message;
        Results = message;
        _overlay = new ResultOverlay(message, InspectionResultPresentation.Color(InspectionVerdict.Warning), [], []);
        Publish();
    }

    private void Refresh()
    {
        Notify(nameof(IsBusy));
        Notify(nameof(IsIdle));
        Notify(nameof(HasTrainedModel));
        Notify(nameof(OptionsError));
        Notify(nameof(ParameterPreviewSummary));
        RegisterGood.Refresh();
        RegisterDefect.Refresh();
        RegisterBoardPreset.Refresh();
        ClearSamples.Refresh();
        Train.Refresh();
        ClassifySelected.Refresh();
        ClassifyAll.Refresh();
        Cancel.Refresh();
        Locate.Refresh();
    }

    private void Publish()
    {
        Notify(nameof(Status));
        Notify(nameof(SampleSummary));
        Notify(nameof(ModelSummary));
        Notify(nameof(Results));
        Refresh();
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _disposed = true;
        _cancellation?.Cancel();
    }
}
