using System.Numerics;
using VisionGeometry;

namespace VisionCanvas.Layers;

/// <summary>위치는 레이어 좌표, 팔 길이와 중앙 간격은 DIP다. 화면 중앙 고정은 Screen 레이어에서만 지원한다.</summary>
public sealed record CrosshairGraphic(PointD Position, Vector4 Color, double ArmLength = 16, double Gap = 4,
    double Thickness = 1, bool AtScreenCenter = false) : CanvasGraphic
{
    public static CrosshairGraphic ScreenCenter(Vector4 color, double armLength = 16, double gap = 4, double thickness = 1)
        => new(default, color, armLength, gap, thickness, true);

    internal override void Validate(LayerSpace space)
    {
        CheckPoint(Position);
        CheckStyle(Color, Thickness);
        if (!double.IsFinite(ArmLength) || ArmLength <= 0 || !double.IsFinite(Gap) || Gap < 0 || Gap >= ArmLength ||
            (AtScreenCenter && space != LayerSpace.Screen))
        {
            throw new ArgumentException("십자선의 길이·간격 또는 화면 중앙 좌표계가 유효하지 않습니다.");
        }
    }
}
