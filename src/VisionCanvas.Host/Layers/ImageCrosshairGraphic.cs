using System.Numerics;
using VisionGeometry;

namespace VisionCanvas.Layers;

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
