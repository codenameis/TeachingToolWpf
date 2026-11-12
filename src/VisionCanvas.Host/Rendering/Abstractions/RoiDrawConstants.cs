using System.Numerics;
using System.Runtime.InteropServices;
using VisionGeometry;

namespace VisionCanvas.Rendering.Abstractions;

/// <summary>Roi.hlsl / Roi.wgsl Options의 176바이트 배치. 필드 순서는 GPU 계약이다.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct RoiDrawConstants
{
    public const int SizeInBytes = 176;
    public const int RootConstantCount = SizeInBytes / sizeof(float);
    // [Domain Why]: 사각형은 네 변(4 × 6정점), 선분/원은 첫 여섯 정점만 사용하며 단일 인스턴스 배치로 제출한다.
    public const uint VerticesPerInstance = 4 * 6;
    private const float SearchRangeStrokeWidth = 1.5f;

    public readonly Vector2 ClipScale;
    public readonly float ImageScale;
    public readonly float SearchRangePass;
    public readonly Vector2 ScreenOrigin;
    public readonly float ShadowPass;
    public readonly float SummaryOpacity;
    public readonly Vector4 ShadowColor;
    public readonly Vector2 Rotation;
    public readonly Vector2 ReservedRotation;
    public readonly Vector4 DefaultColor;
    public readonly Vector4 ChildColor;
    public readonly Vector4 ParentColor;
    public readonly Vector4 ModuleColor;
    public readonly Vector4 RootColor;
    public readonly Vector4 StrokeWidths;
    public readonly Vector4 ExtraStrokeWidths;

    public RoiDrawConstants(RenderScene scene, PointD blockOrigin, RigidTransform2D transform, bool selected, RoiDrawPass pass)
    {
        ArgumentNullException.ThrowIfNull(scene);
        // 큰 이미지 원점은 double에서 변환하고, GPU에는 화면 원점과 블록 상대 float만 전달한다.
        var screenOrigin = scene.Document.Viewport.ImageToScreen(transform.Apply(blockOrigin));
        var palette = scene.RoiStyles;
        // [Domain Why]: 그림자 패스 시 강조 외곽선 확장량 (선택 시 1.5px, 비선택 시 1.0px).
        const float selectedShadowExpansion = 1.5f;
        const float defaultShadowExpansion = 1.0f;
        var isShadow = pass == RoiDrawPass.Shadow;
        var shadowExpansion = 0f;
        if (isShadow)
        {
            shadowExpansion = selected ? selectedShadowExpansion : defaultShadowExpansion;
        }

        ClipScale = new((float)(2 / scene.Width), (float)(-2 / scene.Height));
        ImageScale = (float)scene.Document.Viewport.Scale;
        SearchRangePass = pass == RoiDrawPass.SearchRange ? 1 : 0;
        ScreenOrigin = new((float)screenOrigin.X, (float)screenOrigin.Y);
        ShadowPass = isShadow ? 1 : 0;
        SummaryOpacity = selected ? 1 : RenderingConstants.UnselectedSummaryOpacity;
        ShadowColor = new(0, 0, 0, RenderingConstants.ShadowAlpha);
        Rotation = new((float)transform.Cosine, (float)transform.Sine);
        ReservedRotation = default;
        var multiple = scene.Document.SelectionCount > 1;
        DefaultColor = palette.Default.StrokeColor(selected, multiple);
        ChildColor = palette.Child.StrokeColor(selected, multiple);
        ParentColor = palette.Parent.StrokeColor(selected, multiple);
        ModuleColor = palette.Module.StrokeColor(selected, multiple);
        RootColor = palette.Root.StrokeColor(selected, multiple);
        StrokeWidths = new(
            palette.Default.StrokeThickness(selected) + shadowExpansion,
            palette.Child.StrokeThickness(selected) + shadowExpansion,
            palette.Parent.StrokeThickness(selected) + shadowExpansion,
            palette.Module.StrokeThickness(selected) + shadowExpansion);
        ExtraStrokeWidths = new(palette.Root.StrokeThickness(selected) + shadowExpansion, 0, 0, 0);
        if (pass == RoiDrawPass.SearchRange)
        {
            DefaultColor = ChildColor = ParentColor = ModuleColor = RootColor = new(1, 0, 0, 1);
            StrokeWidths = new(SearchRangeStrokeWidth, SearchRangeStrokeWidth, SearchRangeStrokeWidth, SearchRangeStrokeWidth);
            ExtraStrokeWidths = new(SearchRangeStrokeWidth, 0, 0, 0);
        }
    }
}
