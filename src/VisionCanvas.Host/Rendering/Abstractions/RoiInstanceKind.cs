namespace VisionCanvas.Rendering.Abstractions;

/// <summary>GPU/CPU ROI 인스턴스 셰이더 형상 구분.</summary>
public enum RoiInstanceKind : uint
{
    Rectangle = 0,
    Summary = 1,
    Edge = 2,
    Circle = 3
}
