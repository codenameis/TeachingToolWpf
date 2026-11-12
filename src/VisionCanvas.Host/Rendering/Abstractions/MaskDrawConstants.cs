using System.Numerics;
using System.Runtime.InteropServices;
using VisionGeometry;

namespace VisionCanvas.Rendering.Abstractions;

/// <summary>Mask.hlsl / Mask.wgsl의 상수 버퍼 레이아웃 및 마스크 패킹 헬퍼.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct MaskDrawConstants
{
    public Vector2 Center;
    public Vector2 AxisX;
    public Vector2 AxisY;
    public uint MaskCount;
    public uint VertexCount;
    public uint IsCircle;
    public uint SelectedIndex;
    public float Dpi;
    public uint Padding;

    public static ActiveMaskTarget? TryResolveActiveTarget(RenderScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (!scene.ShowMasks || scene.Document.SelectionCount != 1 || scene.Document.Selection.Count != 1)
        {
            return null;
        }

        var roi = scene.Document.Rois[scene.Document.Selection.Single()];
        if (scene.Document.IsGroupRoi(roi.Id))
        {
            return null;
        }

        var draft = scene.Overlay.MaskDraft;
        var maskCount = roi.Masks.Length + (draft is null ? 0 : 1);
        if (maskCount == 0)
        {
            return null;
        }

        var geometry = scene.EffectiveGeometry(roi);
        return new ActiveMaskTarget(roi, geometry, draft, maskCount, geometry.Shape.Vertices.Length);
    }

    public static MaskDrawConstants Pack(
        RenderScene scene,
        in ActiveMaskTarget target,
        Span<Vector4> masks,
        Span<Vector2> points)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var roi = target.Roi;
        var geometry = target.Geometry;
        var draft = target.Draft;
        var maskCount = target.MaskCount;

        for (var index = 0; index < maskCount; index++)
        {
            var mask = index < roi.Masks.Length ? roi.Masks[index] : draft!;
            masks[index * 2] = new((float)mask.Bounds.Left, (float)mask.Bounds.Top, (float)mask.Bounds.Right, (float)mask.Bounds.Bottom);
            masks[(index * 2) + 1] = new(mask.Shape == MaskShape.Ellipse ? 1 : 0, 0, 0, 0);
        }

        var vertices = geometry.Shape.Vertices;
        for (var index = 0; index < vertices.Length; index++)
        {
            points[index] = new((float)vertices[index].X, (float)vertices[index].Y);
        }

        var center = scene.Document.Viewport.ImageToScreen(geometry.Center);
        var scale = scene.Document.Viewport.Scale;
        var (sin, cos) = Math.SinCos(geometry.Angle * GeometryConstants.DegreesToRadians);
        return new MaskDrawConstants
        {
            Center = new((float)((center.X * 2 / scene.Width) - 1), (float)(1 - (center.Y * 2 / scene.Height))),
            AxisX = new((float)(cos * geometry.Width * scale / scene.Width), (float)(-sin * geometry.Width * scale / scene.Height)),
            AxisY = new((float)(-sin * geometry.Height * scale / scene.Width), (float)(-cos * geometry.Height * scale / scene.Height)),
            MaskCount = (uint)maskCount,
            VertexCount = (uint)vertices.Length,
            IsCircle = geometry.Shape.Kind == RoiShapeKind.Circle ? 1u : 0u,
            SelectedIndex = draft is null ? unchecked((uint)scene.SelectedMaskIndex) : (uint)roi.Masks.Length,
            Dpi = (float)scene.Dpi
        };
    }
}
