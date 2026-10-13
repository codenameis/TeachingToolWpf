using VisionGeometry;

namespace VisionCanvas.Core.Interaction;

/// <summary>HWND가 전달하는 포인터·탐색 입력. 문서 종류와 편집 이력은 구현이 소유한다.</summary>
public interface IViewportInput
{
    EditorTool Tool
    {
        get;
    }
    bool IsDragging
    {
        get;
    }
    void Down(PointD screen, InputModifiers modifiers = InputModifiers.None, bool middle = false);
    void DoubleClick(PointD screen, InputModifiers modifiers = InputModifiers.None);
    void Move(PointD screen, InputModifiers modifiers = InputModifiers.None);
    void Up(PointD screen, InputModifiers modifiers = InputModifiers.None);
    void Wheel(PointD screen, double notches);
    void Cancel();
}
