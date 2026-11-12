using System.Numerics;
using VisionGeometry;

namespace VisionCanvas.Layers;

public sealed record TextGraphic(PointD Position, string Text, Vector4 Color) : CanvasGraphic
{
    internal override void Validate(LayerSpace space)
    {
        CheckPoint(Position);
        ArgumentNullException.ThrowIfNull(Text);
        CheckStyle(Color);
    }
}
