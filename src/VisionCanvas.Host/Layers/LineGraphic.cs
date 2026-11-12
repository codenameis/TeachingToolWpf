using System.Numerics;
using VisionGeometry;

namespace VisionCanvas.Layers;

public sealed record LineGraphic(PointD Start, PointD End, Vector4 Color, double Thickness = 1) : CanvasGraphic
{
    internal override void Validate(LayerSpace space)
    {
        CheckPoint(Start);
        CheckPoint(End);
        CheckStyle(Color, Thickness);
    }
}
