using VisionGeometry;

namespace VisionCanvas.Core.Interaction;

/// <summary>HWND가 전달하는 포인터·탐색·키보드 입력 계약이다. 문서 종류와 편집 이력은 구현이 소유한다.</summary>
public interface IViewportInput
{
    bool IsEnabled
    {
        get;
        set;
    }

    EditorTool Tool
    {
        get;
    }

    bool IsDragging
    {
        get;
    }

    bool HasActiveState
    {
        get;
    }

    void Down(PointD screen, InputModifiers modifiers, PointerButton button);

    void Down(PointD screen, InputModifiers modifiers = InputModifiers.None, bool middle = false)
        => Down(screen, modifiers, middle ? PointerButton.Middle : PointerButton.Left);

    void DoubleClick(PointD screen, InputModifiers modifiers = InputModifiers.None);

    void Move(PointD screen, InputModifiers modifiers = InputModifiers.None);

    void Up(PointD screen, InputModifiers modifiers = InputModifiers.None);

    void Wheel(PointD screen, double notches);

    bool NudgeSelection(PointD delta);

    void Cancel();
}
