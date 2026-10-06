using System.Numerics;
using System.Runtime.InteropServices;
using VisionGeometry;

namespace VisionCanvas.Rendering;

internal enum RoiDrawPass
{
    Shadow, Stroke
}

/// <summary>Roi.hlsl Options의 128바이트 배치. 필드 순서는 GPU 계약이다.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal readonly struct RoiDrawConstants
{
    public const int SizeInBytes = 128;
    public const int RootConstantCount = SizeInBytes / sizeof(float);

    public readonly Vector2 ClipScale;
    public readonly float ImageScale;
    public readonly float ReservedView;
    public readonly Vector2 ScreenOrigin;
    public readonly float ShadowPass;
    public readonly float SummaryOpacity;
    public readonly Vector4 ShadowColor;
    public readonly Vector2 Rotation;
    public readonly Vector2 ReservedRotation;
    public readonly Vector4 DefaultColor;
    public readonly Vector4 ChildColor;
    public readonly Vector4 ParentColor;
    public readonly Vector4 StrokeWidths;

    public RoiDrawConstants(RenderScene scene, PointD blockOrigin, RigidTransform2D transform, bool selected, RoiDrawPass pass)
    {
        // 큰 이미지 원점은 double에서 변환하고, GPU에는 화면 원점과 블록 상대 float만 전달한다.
        var screenOrigin = scene.Document.Viewport.ImageToScreen(transform.Apply(blockOrigin));
        var palette = scene.RoiStyles;
        var isShadow = pass == RoiDrawPass.Shadow;
        var shadowExpansion = 0f;
        if (isShadow)
        {
            shadowExpansion = selected ? 1.5f : 1f;
        }

        ClipScale = new((float)(2 / scene.Width), (float)(-2 / scene.Height));
        ImageScale = (float)scene.Document.Viewport.Scale;
        ReservedView = 0;
        ScreenOrigin = new((float)screenOrigin.X, (float)screenOrigin.Y);
        ShadowPass = isShadow ? 1 : 0;
        SummaryOpacity = selected ? 1 : .5f;
        ShadowColor = new(0, 0, 0, .22f);
        Rotation = new((float)transform.Cosine, (float)transform.Sine);
        ReservedRotation = default;
        var multiple = scene.Document.Selection.Count > 1;
        DefaultColor = palette.Default.StrokeColor(selected, multiple);
        ChildColor = palette.Child.StrokeColor(selected, multiple);
        ParentColor = palette.Parent.StrokeColor(selected, multiple);
        StrokeWidths = new(
            palette.Default.StrokeThickness(selected) + shadowExpansion,
            palette.Child.StrokeThickness(selected) + shadowExpansion,
            palette.Parent.StrokeThickness(selected) + shadowExpansion,
            0);
    }
}
