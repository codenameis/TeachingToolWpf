using VisionCanvas.Core.Editing;

namespace TeachingToolWpf.ViewModels;

/// <summary>검사 패널이 문서에서 받는 갱신 요청. 프레임마다 만들어도 힙 할당이 없다.</summary>
/// <param name="State">현재 문서 상태.</param>
/// <param name="Generation">이미지 교체 세대. 같은 문서인지 구분한다.</param>
/// <param name="Live">라이브 프레임이 화면을 갱신 중인지 여부.</param>
/// <param name="CanExecute">검사 명령을 실행할 수 있는지 여부.</param>
/// <param name="ToolAvailable">이 패널이 화면에 보이고 다른 도구가 유휴인지 여부.</param>
/// <param name="TransformPreview">ROI 이동·회전 드래그 중인 실시간 미리보기 변환.</param>
public readonly record struct InspectionUpdate(
    DocumentState State,
    long Generation,
    bool Live,
    bool CanExecute,
    bool ToolAvailable = true,
    RoiTransformPreview? TransformPreview = null);
