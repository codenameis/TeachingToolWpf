using System.Numerics;
using System.Runtime.InteropServices;

namespace VisionCanvas.Rendering.Abstractions;

/// <summary>스프라이트 정점 레이아웃 (32바이트: NDC 위치, UV, 색상).</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct SpriteVertex(Vector2 Position, Vector2 Uv, Vector4 Color);
