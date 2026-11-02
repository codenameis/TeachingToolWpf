using System.Collections.Immutable;
using VisionGeometry;

namespace VisionCanvas.Layers;

internal sealed record LayerItem(Guid Id, CanvasGraphic Graphic)
{
    // 불변 형상의 윤곽은 추가/교체할 때만 만든다. pan/zoom 프레임마다 배열을 만들지 않는다.
    public ImmutableArray<PointD> Outline { get; } = CreateOutline(Graphic);

    private static ImmutableArray<PointD> CreateOutline(CanvasGraphic graphic)
    {
        if (graphic is not ShapeGraphic shape)
        {
            return [];
        }

        var geometry = shape.Geometry;
        var count = geometry.VertexCount;
        var builder = ImmutableArray.CreateBuilder<PointD>(count);
        for (var i = 0; i < count; i++)
        {
            builder.Add(geometry.GetVertex(i));
        }

        return builder.MoveToImmutable();
    }
}

internal sealed record LayerSnapshot(LayerSpace Space, bool IsVisible, ImmutableSortedDictionary<long, LayerItem> Items);
