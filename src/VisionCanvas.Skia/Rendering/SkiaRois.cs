using SkiaSharp;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionCanvas.Rendering;
using VisionGeometry;

namespace VisionCanvas.Skia.Rendering;

/// <summary>공통 BVH의 가시 블록과 요약 규칙을 사용하고 불변 상대 형상을 보관한다.</summary>
internal sealed class SkiaRois : IDisposable
{
    private readonly RoiBlockCache<Block> _cache = new();
    private readonly SKPath _path = new();
    private readonly SKPaint _paint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke };
    private readonly SKPathEffect _dash = SKPathEffect.CreateDash([6, 4], 0);

    private sealed class Block(PointD origin, RoiInstance[] instances, RoiRoleCache roles, double aggregateSize)
        : RoiCachedBlockBase(origin, instances.Length, roles, aggregateSize)
    {
        public RoiInstance[] Instances { get; } = instances;

        public override long OwnedBytes => (long)Instances.Length * (RoiInstance.SizeInBytes + 8);

        public override void Dispose()
        {
        }
    }

    public bool HasPendingDetails => _cache.HasPendingDetails;

    public void Draw(SKCanvas canvas, RenderScene scene)
    {
        _cache.BeginFrame();
        var view = scene.Document.Viewport;
        RoiSpatialTraversal.GetLodMetrics(scene, out var visible, out var level, out var aggregate);
        RoiSpatialTraversal.ForEachIndex(scene, (index, selected, transform, search) =>
        {
            foreach (var region in index.Blocks(transform.InverseTransformBounds(visible)))
            {
                var block = _cache.GetOrUpload(
                    region,
                    level,
                    aggregate,
                    scene.Document,
                    static (sourceRegion, blockAggregate, instances, roles) => new Block(sourceRegion.Bounds.Center, instances.ToArray(), roles, blockAggregate),
                    static _ => { },
                    allowDeferredFallback: false);
                if (block.Count == 0)
                {
                    continue;
                }

                for (var item = 0; item < block.Instances.Length; item++)
                {
                    var instance = block.Instances[item];
                    var style = scene.RoiStyles.For((RoiRole)block.Roles.Values[item]);
                    var center = view.ImageToScreen(transform.Apply(block.Origin + new PointD(instance.Center.X, instance.Center.Y)));
                    var axisX = new PointD(instance.AxisX.X, instance.AxisX.Y).Rotate(transform.AngleDegrees) * view.Scale;
                    var axisY = new PointD(instance.AxisY.X, instance.AxisY.Y).Rotate(transform.AngleDegrees) * view.Scale;
                    _path.Rewind();
                    if (instance.Kind == RoiInstanceKind.Edge)
                    {
                        _path.MoveTo(Point(center));
                        _path.LineTo(Point(center + axisX));
                    }
                    else if (instance.Kind == RoiInstanceKind.Circle)
                    {
                        _path.AddCircle((float)center.X, (float)center.Y, (float)axisX.Length);
                    }
                    else
                    {
                        _path.MoveTo(Point(center - axisX - axisY));
                        _path.LineTo(Point(center + axisX - axisY));
                        _path.LineTo(Point(center + axisX + axisY));
                        _path.LineTo(Point(center - axisX + axisY));
                        _path.Close();
                    }

                    _paint.PathEffect = search ? _dash : null;
                    _paint.StrokeWidth = search ? 1.5f : style.StrokeThickness(selected);
                    _paint.Color = search ? SKColors.Red : SkiaSprites.Color(style.StrokeColor(selected, scene.Document.SelectionCount > 1));
                    if (instance.Kind == RoiInstanceKind.Summary && !selected)
                    {
                        _paint.Color = _paint.Color.WithAlpha(128);
                    }

                    canvas.DrawPath(_path, _paint);
                }
            }
        });
        _cache.EvictInactive(static memoryBytes => memoryBytes, static block => block.Dispose());
    }

    internal static SKPoint Point(PointD point) => new((float)point.X, (float)point.Y);

    public void Dispose()
    {
        _cache.Dispose();
        _paint.Dispose();
        _path.Dispose();
        _dash.Dispose();
    }
}
