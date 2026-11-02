using SkiaSharp;
using VisionCanvas.Rendering;
using VisionGeometry;

namespace VisionCanvas.Skia.Rendering;

internal sealed class SkiaMasks : IDisposable
{
    private readonly SKPaint _paint = new() { IsAntialias = true };

    public void Draw(SKCanvas canvas, RenderScene scene)
    {
        if (!scene.ShowMasks || scene.Document.SelectionCount != 1 || scene.Document.Selection.Count != 1)
        {
            return;
        }
        var roi = scene.Document.Rois[scene.Document.Selection.Single()];
        if (scene.Document.IsGroupRoi(roi.Id) || (roi.Masks.IsEmpty && scene.Overlay.MaskDraft is null))
        {
            return;
        }
        var geometry = scene.Transform?.Geometry(roi) ?? roi.Geometry;
        var view = scene.Document.Viewport;
        using var owner = new SKPath();
        if (geometry.Shape.Kind == RoiShapeKind.Circle)
        {
            var center = view.ImageToScreen(geometry.Center);
            owner.AddCircle((float)center.X, (float)center.Y, (float)(geometry.Width * view.Scale / 2));
        }
        else
        {
            var vertexCount = geometry.VertexCount;
            Span<PointD> outlinePoints = vertexCount <= 128 ? stackalloc PointD[vertexCount] : new PointD[vertexCount];
            geometry.CopyOutlineTo(outlinePoints);
            if (outlinePoints.Length > 0)
            {
                owner.MoveTo(SkiaRois.Point(view.ImageToScreen(outlinePoints[0])));
                for (var i = 1; i < outlinePoints.Length; i++)
                {
                    owner.LineTo(SkiaRois.Point(view.ImageToScreen(outlinePoints[i])));
                }
                owner.Close();
            }
        }
        using var masks = new SKPath();
        using var selected = new SKPath();
        var draft = scene.Overlay.MaskDraft;
        var count = roi.Masks.Length + (draft is null ? 0 : 1);
        for (var index = 0; index < count; index++)
        {
            var mask = index < roi.Masks.Length ? roi.Masks[index] : draft!;
            using var path = new SKPath();
            var bounds = mask.Bounds;
            var rectangle = new SKRect((float)((bounds.Left - .5) * geometry.Width), (float)((bounds.Top - .5) * geometry.Height),
                (float)((bounds.Right - .5) * geometry.Width), (float)((bounds.Bottom - .5) * geometry.Height));
            if (mask.Shape == MaskShape.Ellipse)
            {
                path.AddOval(rectangle);
            }
            else
            {
                path.AddRect(rectangle);
            }
            var origin = view.ImageToScreen(geometry.Center);
            var (sin, cos) = Math.SinCos(geometry.Angle * Math.PI / 180);
            var scale = view.Scale;
            path.Transform(new SKMatrix((float)(cos * scale), (float)(-sin * scale), (float)origin.X,
                (float)(sin * scale), (float)(cos * scale), (float)origin.Y, 0, 0, 1));
            masks.AddPath(path);
            if (index == (draft is null ? scene.SelectedMaskIndex : roi.Masks.Length))
            {
                selected.AddPath(path);
            }
        }
        canvas.Save();
        canvas.ClipPath(owner, antialias: true);
        _paint.Style = SKPaintStyle.Fill;
        _paint.Color = new(224, 51, 94, 33);
        canvas.DrawPath(masks, _paint);
        _paint.Color = new(224, 51, 94, 35);
        canvas.DrawPath(selected, _paint);
        canvas.ClipPath(masks, antialias: true);
        _paint.Style = SKPaintStyle.Stroke;
        _paint.StrokeWidth = 1.8f;
        _paint.Color = new(224, 51, 94, 71);
        for (var offset = 0d; offset < scene.Width + scene.Height; offset += 9)
        {
            canvas.DrawLine((float)offset, 0, (float)(offset - scene.Height), (float)scene.Height, _paint);
        }
        canvas.Restore();
    }

    public void Dispose() => _paint.Dispose();
}
