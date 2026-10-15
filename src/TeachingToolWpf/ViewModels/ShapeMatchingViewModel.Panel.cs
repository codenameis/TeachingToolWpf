using TeachingToolWpf.Models;

namespace TeachingToolWpf.ViewModels;

public sealed partial class ShapeMatchingViewModel
{
    private string? _inspectionNotice;

    public string TeachingSelectionText => _selection is { } roi
        ? $"선택: {roi.ShapeName} · {roi.Width:F0} × {roi.Height:F0} px · {roi.Angle:F1}°"
        : "이미지에서 학습할 ROI 한 개를 선택하세요.";

    public string SearchScopeText => UseSelection
        ? _searchSelection is { } roi ? $"검색: {roi.Width:F0} × {roi.Height:F0} px · 선택 영역" : "검색할 ROI가 선택되지 않았습니다."
        : _image is { } image ? $"검색: {image.Width:N0} × {image.Height:N0} px · 전체 이미지" : "검색할 이미지를 열어 주세요.";

    public string PanelTitle => OptionsError.Length > 0 ? "설정 확인 필요" : IsBusy ? _training ? "학습 중" : "검사 중" :
        IsTrainingDirty ? "다시 학습하세요" : Verdict switch
        {
            InspectionVerdict.Warning => "검사 확인 필요 · 불량 판정 아님",
            InspectionVerdict.Review => Markers.IsEmpty ? "검출 없음 · 조건 확인" : $"{Markers.Length}개 검출 · 위치 확인",
            InspectionVerdict.Pass => $"{Markers.Length}개 검출 · 정상",
            InspectionVerdict.Fail => VerdictText,
            _ => HasModel ? "검사 준비" : "학습 대기"
        };

    public string PanelHint
    {
        get
        {
            if (HasInputErrors)
            {
                return "숫자 입력을 확인하세요. ‘기본’ 또는 ‘상세 설정’의 잘못 입력한 값을 수정하면 실행할 수 있습니다.";
            }
            if (OptionsError.Length > 0)
            {
                return OptionsError;
            }
            if (IsBusy)
            {
                return "원본 이미지에서 처리하고 있습니다. 아래 ‘취소’로 중단할 수 있습니다.";
            }
            if (_image is null)
            {
                return "이미지를 열고, 찾을 패턴에 ROI를 그려 주세요.";
            }
            if (IsTrainingDirty)
            {
                return "학습 조건이나 마스크가 바뀌었습니다. ‘선택 ROI 학습’을 다시 누르세요.";
            }
            if (_inspectionNotice is not null)
            {
                return _inspectionNotice;
            }
            if (!HasModel)
            {
                return "ROI 한 개를 선택한 뒤 ‘선택 ROI 학습’을 누르세요.";
            }
            if (UseSelection && _selection is null)
            {
                return "검사할 ROI를 선택하거나 ‘선택 ROI 범위에서 찾기’를 끄세요.";
            }
            if (!_canExecute)
            {
                return "현재 이미지 작업을 마친 뒤 다시 시도하세요.";
            }
            return "검색 범위와 점수를 확인한 뒤 아래 ‘검사 실행’을 누르세요.";
        }
    }

    private static string DetectionNotice(bool candidatesTruncated, bool matchLimitExceeded, int count, bool tdcm = false)
    {
        var messages = new List<string>();
        if (candidatesTruncated)
        {
            messages.Add(tdcm
                ? "일부 영역을 검색하지 못했습니다. ‘상세 설정’에서 최대 검색 창을 늘리거나 검색 ROI를 줄이세요."
                : "일부 후보를 확인하지 못했습니다. ‘상세 설정 → 공통 검색 고급’의 최대 후보 수를 늘리거나 검색 ROI를 줄이세요.");
        }
        if (matchLimitExceeded)
        {
            messages.Add("검출 결과 일부가 생략됐습니다. ‘기본’의 최대 결과 수를 늘려 다시 검사하세요.");
        }
        return messages.Count > 0 ? string.Join("\n", messages) : count == 0
            ? "설정 조건에 맞는 패턴이 없습니다. 검색 영역과 학습 패턴·각도 범위를 확인하세요."
            : "‘결과’에서 위치·각도·점수를 확인하세요. 이 방식은 제품 합격/불량을 자동 판정하지 않습니다.";
    }

    private void NotifyPanelState()
    {
        Notify(nameof(PanelTitle));
        Notify(nameof(PanelHint));
        Notify(nameof(TeachingSelectionText));
        Notify(nameof(SearchScopeText));
    }
}
