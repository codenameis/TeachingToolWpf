using System.Numerics;
using VisionGeometry;

namespace VisionCanvas.Layers;

/// <summary>Image는 원본 픽셀 좌표, Screen은 표시 영역의 DIP 좌표다.</summary>
public enum LayerSpace
{
    Image, Screen
}

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

public sealed record LineGraphic(PointD Start, PointD End, Vector4 Color, double Thickness = 1) : CanvasGraphic
{
    internal override void Validate(LayerSpace space)
    {
        CheckPoint(Start);
        CheckPoint(End);
        CheckStyle(Color, Thickness);
    }
}

/// <summary>사각형·원·삼각형·폴리곤의 실제 윤곽을 표시한다. 선 두께는 확대율과 무관한 DIP다.</summary>
public sealed record ShapeGraphic(RoiGeometry Geometry, Vector4 Color, double Thickness = 1) : CanvasGraphic
{
    internal override void Validate(LayerSpace space)
    {
        ArgumentNullException.ThrowIfNull(Geometry);
        CheckStyle(Color, Thickness);
    }
}

public sealed record TextGraphic(PointD Position, string Text, Vector4 Color) : CanvasGraphic
{
    internal override void Validate(LayerSpace space)
    {
        CheckPoint(Position);
        ArgumentNullException.ThrowIfNull(Text);
        CheckStyle(Color);
    }
}

/// <summary>원본 이미지 중심을 지나 Canvas 전체 너비/높이로 뻗는 십자선. 이미지가 없으면 표시하지 않는다.</summary>
public sealed record ImageCrosshairGraphic(Vector4 Color, double Thickness = 1) : CanvasGraphic
{
    internal override void Validate(LayerSpace space)
    {
        CheckStyle(Color, Thickness);
        if (space != LayerSpace.Image)
        {
            throw new ArgumentException("이미지 중심 십자선은 Image 레이어를 사용해야 합니다.");
        }
    }
}

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
