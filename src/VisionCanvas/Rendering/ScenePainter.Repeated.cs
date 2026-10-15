using System.Numerics;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionGeometry;

namespace VisionCanvas.Rendering;

internal sealed partial class ScenePainter
{
    private void DrawRepeatedDocument(RenderScene scene, GpuTexture white, Func<string, GpuTexture> label)
    {
        if (scene.Document.RepeatedRois.IsEmpty)
        {
            return;
        }

        var selection = scene.Transform?.Selection ?? scene.Document.TransformSelection;
        var view = scene.Document.Viewport;
        var summary = false;
        foreach (var batch in selection.StationaryRepeated)
        {
            foreach (var part in batch.Parts)
            {
                DrawOverrides(part, default, false);
            }
        }

        foreach (var batch in selection.Repeated)
        {
            foreach (var part in batch.Parts)
            {
                DrawOverrides(part, scene.Transform?.Transform ?? default, true);
            }
        }

        if (scene.Document.SingleRepeatedCell is { } cell)
        {
            var original = scene.Document.RepeatedRois[cell.BatchId].Geometry(cell.Index);
            var transform = scene.Transform?.Transform ?? default;
            var geometry = transform.IsIdentity ? original : original.WithFrame(transform.Apply(original.Center),
                original.Width, original.Height, original.Angle + transform.AngleDegrees);
            var color = scene.RoiStyles.Default.SelectedColor;
            var corners = geometry.FrameCorners.Select(view.ImageToScreen).ToArray();
            _mesh.Outline(white, geometry.Outline.Select(view.ImageToScreen).ToArray(), scene.RoiStyles.Default.StrokeThickness(true), color);
            foreach (var point in HandleGeometry.ResizeHandles(geometry, view))
            {
                Handle(white, point, scene.Overlay.HandleScale, color);
            }

            var rotation = HandleGeometry.RotationHandle(geometry, view, scene.Overlay.HandleScale);
            _mesh.Line(white, (corners[0] + corners[1]) / 2, rotation, 1, color);
            RotationHandle(white, rotation, color, scene.Overlay.HandleScale);
        }
        if (summary)
        {
            var text = label("반복 격자 요약 · 확대하면 개별 ROI 윤곽 표시");
            _mesh.Rectangle(white, new(8, 8, 20 + (text.Width / scene.Dpi), 16 + (text.Height / scene.Dpi)), new(.13f, .16f, .22f, .94f));
            _mesh.Rectangle(text, new(14, 12, 14 + (text.Width / scene.Dpi), 12 + (text.Height / scene.Dpi)), Vector4.One);
        }

        void DrawOverrides(RepeatedRoiPart part, RigidTransform2D transform, bool selected)
        {
            summary |= RepeatedRoiPipeline.SummaryStride(part.Grid, view.Scale) > 1;
            var grid = part.Grid.Transform(transform);
            foreach (var index in grid.Overrides.Keys)
            {
                if (!part.Cells.Contains(index))
                {
                    continue;
                }

                _mesh.Outline(white, grid.Geometry(index).Outline.Select(view.ImageToScreen).ToArray(), scene.RoiStyles.Default.StrokeThickness(selected),
                    scene.RoiStyles.Default.StrokeColor(selected, selected && scene.Document.SelectionCount > 1));
            }
        }
    }
}
