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
    private readonly ShapeMatchingModeState _geometricState = new();
    private readonly TemplateMatchingModeState _templateState = new();
    private PatternMatchingMethod _method;

    public IReadOnlyList<PatternMethodOption> Methods
    {
        get;
    } =
    [
        new(PatternMatchingMethod.OpenCvTemplate, "OpenCV · 템플릿 매칭"),
        new(PatternMatchingMethod.Shape, "논문 기반 · 형상 매칭"),
        new(PatternMatchingMethod.GeometricShape, "기하 형상 · 자체 구현")
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
    public bool IsShapeMethod => Method is PatternMatchingMethod.Shape or PatternMatchingMethod.GeometricShape;
    public bool IsGeometricMethod => Method == PatternMatchingMethod.GeometricShape;
    public bool IsTemplateMethod => Method == PatternMatchingMethod.OpenCvTemplate;
    public CorrelationTemplate? TemplateModel => _templateState.Model;
    public bool HasModel => IsShapeMethod ? Model is not null : TemplateModel is not null;
    public bool IsTrainingDirty => IsShapeMethod
        ? Model is not null && (ActiveShapeState.Training != ActiveShapeState.TrainedSettings || ActiveShapeState.IsMaskDirty)
        : TemplateModel is not null && _templateState.IsMaskDirty;
    public string MethodDescription => Method switch
    {
        PatternMatchingMethod.Shape => "방향 후보 검색 → 원본 윤곽 검증·자세 정밀화. 학습 영역에 추가된 윤곽을 최종 점수에서 감점합니다. 회전/배율을 지원합니다.",
        PatternMatchingMethod.GeometricShape => "논문 기반 후보 검색을 공유하는 자체 기하 엔진. 윤곽 대응비율·위치 오차·불필요 윤곽을 추가 검증합니다. Cognex PatMax 구현은 아닙니다.",
        _ => "CCOEFF_NORMED · 원본 명암 비교 · 평행이동만 검색 (각도/배율 고정, 특징점 없음)"
    };
    private ShapeMatchingModeState ActiveShapeState => IsGeometricMethod ? _geometricState : _paperState;
    private MatchingSearchSettings ActiveSearchSettings
    {
        get => IsShapeMethod ? ActiveShapeState.Search : _templateState.Search;
        set
        {
            if (IsShapeMethod)
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
        if (IsShapeMethod)
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
            Verdict = decision.Verdict;
            var boundary = SearchBoundary(image, roi);
            _result = new($"{InspectionResultPresentation.Name(decision.Verdict)} · OpenCV · {output.Matches.Length}개",
                InspectionResultPresentation.Color(decision.Verdict), boundary, InspectionResultPresentation.Markers(teachingRegion, output), false);
            Results = decision.Reason + "\n\n" + string.Join("\n\n", output.Matches.Select((match, index) =>
                $"{index + 1} · X {match.ReferencePoint.X:F3} / Y {match.ReferencePoint.Y:F3}\nNCC 점수 {match.Score:F3}"));
            Results += "\n원본 픽셀 좌표 · 각도/배율은 측정하지 않고 학습값을 유지합니다.";
            Status = $"OpenCV {output.Matches.Length}개 · {output.TotalMilliseconds:F1} ms (읽기 {output.ReadMilliseconds:F1}) · 창 {output.Windows}개" +
                (output.Truncated ? " · 후보 예산 초과: 검색 불완전" : "") +
                (output.MatchLimitExceeded ? " · 최대 결과 수 도달: 일부 검출 결과 생략" : "");
            PublishResult();
        });
    }
}
