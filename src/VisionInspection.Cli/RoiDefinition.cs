using System.Text.Json.Serialization;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionAlgorithm.OpenSrc.ShapeDetection;
using VisionGeometry;

namespace VisionInspection.Cli;

/// <summary>폴리곤 꼭짓점은 원본 좌표다. 나머지 도형의 프레임 각도는 시계 방향 degree다.</summary>
public sealed record RoiDefinition
{
    public string Shape { get; init; } = "rectangle";
    public double? CenterX
    {
        get; init;
    }
    public double? CenterY
    {
        get; init;
    }
    public double? Width
    {
        get; init;
    }
    public double? Height
    {
        get; init;
    }
    public double AngleDegrees
    {
        get; init;
    }
    public ImagePoint[]? Vertices
    {
        get; init;
    }

    public RoiGeometry ToGeometry()
    {
        if (Shape == "polygon")
        {
            if (Vertices is null || CenterX is not null || CenterY is not null || Width is not null || Height is not null || AngleDegrees != 0)
            {
                throw new ArgumentException("폴리곤은 원본 좌표 vertices만 지정하세요. 별도 프레임·각도는 적용하지 않습니다.");
            }
            return RoiGeometry.Polygon(Vertices.Select(point => new PointD(point.X, point.Y)));
        }
        if (Vertices is not null || CenterX is null || CenterY is null || Width is null || Height is null)
        {
            throw new ArgumentException("사각형·원·삼각형에는 centerX/centerY/width/height가 필요합니다.");
        }
        var shape = Shape switch
        {
            "rectangle" => RoiShape.Rectangle,
            "circle" => RoiShape.Circle,
            "triangle" => RoiShape.Triangle,
            _ => throw new ArgumentException("ROI shape은 rectangle, circle, triangle, polygon 중 하나여야 합니다.")
        };
        return new(new(CenterX.Value, CenterY.Value), Width.Value, Height.Value, AngleDegrees, shape);
    }
}
