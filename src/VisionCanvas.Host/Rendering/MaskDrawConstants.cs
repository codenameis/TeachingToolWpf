using System.Numerics;
using System.Runtime.InteropServices;
using VisionCanvas.Core.Editing;
using VisionGeometry;

namespace VisionCanvas.Rendering;

/// <summary>Mask.hlsl / Mask.wgsl의 상수 버퍼 레이아웃 및 마스크 패킹 헬퍼.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MaskDrawConstants
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

    public static bool TryResolveActiveMaskTarget(
        RenderScene scene,
        out Roi roi,
        out RoiGeometry geometry,
        out MaskRegion? draft,
        out int maskCount,
        out int vertexCount)
    {
        roi = default!;
        geometry = default!;
        draft = null;
        maskCount = 0;
        vertexCount = 0;
        if (!scene.ShowMasks || scene.Document.SelectionCount != 1 || scene.Document.Selection.Count != 1)
        {
            return false;
        }

        roi = scene.Document.Rois[scene.Document.Selection.Single()];
        if (scene.Document.IsGroupRoi(roi.Id))
        {
            return false;
        }

        draft = scene.Overlay.MaskDraft;
        maskCount = roi.Masks.Length + (draft is null ? 0 : 1);
        if (maskCount == 0)
        {
            return false;
        }

        geometry = scene.Transform?.Geometry(roi) ?? roi.Geometry;
        vertexCount = geometry.Shape.Vertices.Length;
        return true;
    }

    public static void Pack(
        RenderScene scene,
        Roi roi,
        RoiGeometry geometry,
        MaskRegion? draft,
        int maskCount,
        Span<Vector4> masks,
        Span<Vector2> points,
        out MaskDrawConstants constants)
    {
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
        var (sin, cos) = Math.SinCos(geometry.Angle * Math.PI / 180);
        constants = new MaskDrawConstants
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
