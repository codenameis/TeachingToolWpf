namespace TeachingToolWpf.Presentation;

public sealed partial class ShapeMatchingViewModel
{
    private bool _hasInputErrors;

    /// <summary>문자열을 숫자로 변환할 수 없는 WPF 입력은 typed 옵션에 도달하지 않으므로 UI 경계에서 알린다.</summary>
    public bool HasInputErrors
    {
        get => _hasInputErrors;
        set
        {
            if (Set(ref _hasInputErrors, value))
            {
                Invalidate();
                if (value)
                {
                    ResetResult("숫자 입력 오류 · 표시된 설정을 수정한 뒤 다시 실행하세요.");
                }
                RefreshCommands();
            }
        }
    }

    public string OptionsError
    {
        get
        {
            if (HasInputErrors)
            {
                return "숫자로 해석할 수 없는 입력이 있습니다. 표시된 설정을 수정하세요.";
            }
            if (!double.IsFinite(SearchTimeoutSeconds) || SearchTimeoutSeconds is < 0 or > 600)
            {
                return "검사 시간 한도는 0~600초입니다. 0은 시간 제한 없음입니다.";
            }
            if (IsShapeMethod)
            {
                try
                {
                    ActiveRotationAcceptance?.Validate();
                    ActiveShapeState.Training.ToOptions(IsGeometricMethod).Validate();
                }
                catch (ArgumentException error)
                {
                    return "학습 설정 범위와 회전·배율 템플릿 예산을 확인하세요. " + error.Message;
                }
            }
            try
            {
                (IsShapeMethod ? CreateShapeSearchOptions() : CreateTemplateSearchOptions()).Validate();
            }
            catch (ArgumentException error)
            {
                return "검색 설정 범위를 확인하세요. " + error.Message;
            }
            return "";
        }
    }
}
