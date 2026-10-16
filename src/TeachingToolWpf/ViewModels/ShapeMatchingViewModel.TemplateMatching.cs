using System.Collections.Immutable;
using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Models;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionGeometry;

namespace TeachingToolWpf.ViewModels;

public sealed partial class ShapeMatchingViewModel
{
    private readonly ITemplateMatchingService? _templateService;
    private readonly ShapeMatchingModeState _paperState = new();
    private readonly TemplateMatchingModeState _templateState = new();
    private PatternMatchingMethod _method;

    public IReadOnlyList<PatternMethodOption> Methods
    {
        get;
    } =
    [
        new(PatternMatchingMethod.OpenCvTemplate, "OpenCV · 템플릿 매칭"),
        new(PatternMatchingMethod.Shape, "논문 기반 · 형상 매칭"),
        new(PatternMatchingMethod.PoseMatchTdcm, "PoseMatch-TDCM · ONNX"),
        new(PatternMatchingMethod.CpuLine2D, "Line2D 방향 응답 · CPU/CUDA"),
        new(PatternMatchingMethod.CpuSubNcc, "Sub-NCC 선택 픽셀 · CPU/CUDA"),
        new(PatternMatchingMethod.CpuDirectionalChamfer, "Directional Chamfer · CPU/CUDA"),
    ];
    public PatternMatchingMethod Method
    {
        get => _method;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            if (Set(ref _method, value))
            {
                Invalidate();
                ResetResult(IsTrainingDirty ? "학습 설정 변경 · 선택한 방식에서 다시 학습하세요." :
                    HasModel ? "매칭 방식 변경 · 저장된 모델로 검사할 수 있습니다." : "선택한 방식의 모델 없음 · ROI를 선택하고 학습하세요.");
                Notify(null);
                RefreshCommands();
            }
        }
    }
    public bool IsShapeMethod => Method is PatternMatchingMethod.Shape;
    public bool IsTemplateMethod => Method == PatternMatchingMethod.OpenCvTemplate;
    public CorrelationTemplate? TemplateModel => _templateState.Model;
    public bool HasModel => IsCpuMethod ? ActiveCpuState.Template is not null : IsTdcmMethod ? _tdcmTemplate is not null : IsShapeMethod ? Model is not null : TemplateModel is not null;
    public bool IsTrainingDirty => IsCpuMethod ? ActiveCpuState.IsMaskDirty ||
        (ActiveCpuState.Template is { } cpu && cpu.Parameters != ActiveCpuState.Parameters) : IsTdcmMethod ? _tdcmMaskDirty : IsShapeMethod
        ? Model is not null && (ActiveShapeState.Training != ActiveShapeState.TrainedSettings || ActiveShapeState.IsMaskDirty)
        : TemplateModel is not null && _templateState.IsMaskDirty;
    public string MethodDescription => Method switch
    {
        PatternMatchingMethod.CpuLine2D => "희소 윤곽·방향 응답 맵 → 원본 자세 정밀화. CPU 또는 CPU+CUDA 선택. 마스크 없는 사각형 학습.",
        PatternMatchingMethod.CpuSubNcc => "선택 픽셀 ZNCC 후보 → 전체 템플릿 명암 정밀화. CPU 회전·균일 배율 검색. 마스크 없는 사각형 학습.",
        PatternMatchingMethod.CpuDirectionalChamfer => "방향별 거리 변환 → 원본 자세 정밀화·양방향 윤곽 검증. FDCM 참고 구현이며 선분 적분 가속은 미포함. 마스크 없는 사각형 학습.",
        PatternMatchingMethod.Shape => "방향 후보 검색 → 원본 윤곽 검증·자세 정밀화. 학습 영역에 추가된 윤곽을 최종 점수에서 감점합니다. 회전/배율을 지원합니다.",
        PatternMatchingMethod.PoseMatchTdcm => "공식 사전 학습 모델 · CPU/CUDA ONNX · 360° 방향과 독립 X/Y 배율 추정. 신경망 신뢰도는 불량 품질 점수가 아닙니다. 사각형 학습, 마스크 미지원.",
        _ => "CCOEFF_NORMED · 원본 명암 비교 · 평행이동만 검색 (각도/배율 고정, 특징점 없음)"
    };
    private ShapeMatchingModeState ActiveShapeState => _paperState;
    private MatchingSearchSettings ActiveSearchSettings
    {
        get => IsCpuMethod ? ActiveCpuState.Search : IsTdcmMethod ? _tdcmSearch : IsShapeMethod ? ActiveShapeState.Search : _templateState.Search;
        set
        {
            if (IsCpuMethod)
            {
                ActiveCpuState.Search = value;
            }
            else if (IsTdcmMethod)
            {
                _tdcmSearch = value;
            }
            else if (IsShapeMethod)
            {
                ActiveShapeState.Search = value;
            }
            else
            {
                _templateState.Search = value;
            }
        }
    }
    private string TemplateSummary => TemplateModel is { } template
        ? $"OpenCV {template.Width}×{template.Height} px · {template.EstimatedBytes / 1048576d:F1} MiB" + (IsTrainingDirty ? " · 마스크 변경: 다시 학습하세요." : "")
        : "OpenCV 템플릿 없음 · ROI 선택 후 학습하세요.";

    private void ClearModel()
    {
        if (IsCpuMethod)
        {
            ActiveCpuState.Template = null;
            ActiveCpuState.TeachingRoiId = null;
            ActiveCpuState.IsMaskDirty = false;
        }
        else if (IsTdcmMethod)
        {
            _tdcmTemplate = null;
            _tdcmMaskDirty = false;
        }
        else if (IsShapeMethod)
        {
            ActiveShapeState.Model = null;
            ActiveShapeState.TeachingRegion = null;
            ActiveShapeState.TrainedSettings = null;
        }
        else
        {
            _templateState.Model = null;
            _templateState.TeachingRegion = null;
        }
        Invalidate();
        ResetResult("모델 해제 · ROI를 선택하고 다시 학습하세요.");
        Notify(nameof(ModelSummary));
        Notify(nameof(Model));
        Notify(nameof(TemplateModel));
        Notify(nameof(HasModel));
        Notify(nameof(IsTrainingDirty));
        RefreshCommands();
    }

    private async Task TrainTemplateAsync()
    {
        var image = _image!;
        var roi = _selection!;
        var live = _live;
        var method = Method;
        var exclusion = _selectionExclusion;
        var roiId = _selectedRoiId;
        await RunAsync(async (token, epoch) =>
        {
            Status = "원본 명암 템플릿 준비 중…";
            var output = await _templateService!.TrainAsync(image, live, roi, token, exclusion);
            if (!Current(epoch, token, method))
            {
                return;
            }
            _templateState.Model = output.Model;
            _templateState.TeachingRegion = roi;
            _templateState.TeachingRoiId = roiId;
            _templateState.TeachingMasks = exclusion?.Regions ?? [];
            _templateState.IsMaskDirty = false;
            PresentActivity("학습 완료 · 검사 실행을 누르세요.");
            Status = $"OpenCV 학습 {output.Milliseconds:F1} ms · 회전/배율은 학습 상태로 고정";
            Results = "학습 완료 · 검사 실행으로 같은 모양의 위치를 찾으세요.";
            Notify(nameof(ModelSummary));
            Notify(nameof(TemplateModel));
            Notify(nameof(HasModel));
            PublishResult();
        });
    }

    private async Task SearchTemplateAsync()
    {
        var image = _image!;
        var method = Method;
        var template = _templateState.Model!;
        var teachingRegion = _templateState.TeachingRegion!;
        var roi = UseSelection ? _searchSelection : null;
        var exclusion = UseSelection ? _selectionExclusion : null;
        var live = _live;
        var options = CreateTemplateSearchOptions();
        await RunAsync(async (token, epoch) =>
        {
            Status = "OpenCV 원본 템플릿 검색 중… 네이티브 창 처리 후 취소됩니다.";
            var output = await _templateService!.SearchAsync(image, live, template, roi, options, token, exclusion);
            if (!Current(epoch, token, method) || !ReferenceEquals(template, _templateState.Model))
            {
                return;
            }
            var decision = InspectionDecision.Evaluate(output);
            _inspectionNotice = decision.Reason;
            Verdict = decision.Verdict;
            var boundary = SearchBoundary(image, roi);
            _result = new($"{InspectionResultPresentation.Name(decision.Verdict)} · OpenCV · {output.Matches.Length}개",
                InspectionResultPresentation.Color(decision.Verdict), boundary, InspectionResultPresentation.Markers(teachingRegion, output), false);
            SetResultDetails(decision.Reason + "\n원본 픽셀 좌표 · 각도/배율은 측정하지 않고 학습값을 유지합니다.",
                output.Matches.Select((match, index) =>
                    $"{index + 1} · X {match.ReferencePoint.X:F3} / Y {match.ReferencePoint.Y:F3}\nNCC 점수 {match.Score:F3}"));
            Status = $"OpenCV {output.Matches.Length}개 · {output.TotalMilliseconds:F1} ms (읽기 {output.ReadMilliseconds:F1}) · 창 {output.Windows}개" +
                (output.Truncated ? " · 후보 예산 초과: 검색 불완전" : "") +
                (output.MatchLimitExceeded ? " · 최대 결과 수 도달: 일부 검출 결과 생략" : "");
            PublishResult();
        });
    }
}
