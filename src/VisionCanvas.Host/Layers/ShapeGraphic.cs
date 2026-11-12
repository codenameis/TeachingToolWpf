using System.Numerics;
using VisionGeometry;

namespace VisionCanvas.Layers;

/// <summary>사각형·원·삼각형·폴리곤의 실제 윤곽을 표시한다. 선 두께는 확대율과 무관한 DIP다.</summary>
public sealed record ShapeGraphic(RoiGeometry Geometry, Vector4 Color, double Thickness = 1) : CanvasGraphic
{
    internal override void Validate(LayerSpace space)
    {
        ArgumentNullException.ThrowIfNull(Geometry);
        CheckStyle(Color, Thickness);
    }
}
