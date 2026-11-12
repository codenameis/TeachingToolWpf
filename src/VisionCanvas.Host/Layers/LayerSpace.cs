using System.Numerics;
using VisionGeometry;

namespace VisionCanvas.Layers;

/// <summary>Image는 원본 픽셀 좌표, Screen은 표시 영역의 DIP 좌표다.</summary>
public enum LayerSpace
{
    Image, Screen
}
