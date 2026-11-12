using System.Numerics;
using VisionGeometry;

namespace VisionCanvas.Layers;

/// <summary>불변 표시 데이터. ROI 문서·선택·이력에 참여하지 않는다.</summary>
public abstract record CanvasGraphic
{
    private protected CanvasGraphic()
    {
    }

    internal abstract void Validate(LayerSpace space);

    internal static void CheckPoint(PointD point)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
        {
            throw new ArgumentException("표시 좌표는 유한한 값이어야 합니다.");
        }
    }

    internal static void CheckStyle(Vector4 color, double thickness = 1)
    {
        if (!float.IsFinite(color.X) || !float.IsFinite(color.Y) || !float.IsFinite(color.Z) || !float.IsFinite(color.W) ||
            color.X < 0 || color.X > 1 || color.Y < 0 || color.Y > 1 || color.Z < 0 || color.Z > 1 || color.W < 0 || color.W > 1 ||
            !double.IsFinite(thickness) || thickness <= 0)
        {
            throw new ArgumentException("색은 0~1 RGBA, 선 두께는 양의 유한한 DIP 값이어야 합니다.");
        }
    }
}
