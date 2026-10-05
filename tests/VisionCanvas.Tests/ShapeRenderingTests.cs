using VisionCanvas.Core.Editing;
using VisionCanvas.Imaging.Filters;
using VisionCanvas.Rendering;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Tests;

public class ShapeRenderingTests
{
    [Theory]
    [InlineData(RoiShapeKind.Circle, 1, 3u)]
    [InlineData(RoiShapeKind.Triangle, 3, 2u)]
    [InlineData(RoiShapeKind.Polygon, 5, 2u)]
    public void GpuInstancesMatchRealContourAndRoleCount(RoiShapeKind kind, int count, uint instanceKind)
    {
        var editor = new EditorSession();
        var shape = RoiGeometry.Create(kind, new(100, 100), 90, 90).RotateAround(new(100, 100), 23);
        editor.Add(shape);
        var block = editor.State.Snapshot.Spatial.Blocks(shape.Bounds).Single();
        var instances = RoiInstance.Pack(block, 0);
        var combined = RoiInstance.PackWithRoles(block, 0, editor.State);
        Assert.Equal(instances, combined.Instances);
        Assert.Equal(RoiInstance.ResolveRoles(block, 0, editor.State), combined.Roles);
        Assert.Equal(count, instances.Length);
        Assert.All(instances, i => Assert.Equal(instanceKind, i.Summary));
        Assert.Equal(count, RoiInstance.ResolveRoles(block, 0, editor.State).Length);
        if (kind == RoiShapeKind.Circle)
        {
            return;
        }

        var outline = shape.Outline;
        for (var i = 0;
i < count;
i++)
        {
            var start = block.Bounds.Center + new PointD(instances[i].Center.X, instances[i].Center.Y);
            var end = start + new PointD(instances[i].AxisX.X, instances[i].AxisX.Y);
            Assert.InRange((start - outline[i]).Length, 0, .0001);
            Assert.InRange((end - outline[(i + 1) % count]).Length, 0, .0001);
        }
    }

    [Theory]
    [InlineData(RoiShapeKind.Circle)]
    [InlineData(RoiShapeKind.Triangle)]
    [InlineData(RoiShapeKind.Polygon)]
    public void FilterAtlasKeepsPixelsInsideAndTransparencyOutsideShape(RoiShapeKind kind)
    {
        var shape = RoiGeometry.Create(kind, new(50, 50), 100, 100);
        var mask = RoiFilterMask.Create(shape, 100, 100, default)!;
        var region = new FilteredRoi(Guid.NewGuid(), shape, 100, 100, Enumerable.Repeat((byte)200, 10000).ToArray(), mask);
        var page = Assert.Single(FilterAtlas.Pack([region]));
        var placement = Assert.Single(page.Regions);
        using var pixels = page.Pixels();
        Assert.Equal(PixelKind.Rgba32, pixels.Kind);
        Assert.Equal(0, mask[0]);
        Assert.Equal(255, mask[5050]);
        for (var y = 0;
y < 100;
y++)
        {
            for (var x = 0;
x < 100;
x++)
            {
                var offset = ((placement.Y + y) * pixels.Stride) + ((placement.X + x) * 4);
                Assert.Equal(200, pixels.Span[offset]);
                Assert.Equal(mask[(y * 100) + x], pixels.Span[offset + 3]);
            }
        }
    }
}
