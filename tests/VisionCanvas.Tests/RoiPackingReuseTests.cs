using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.CompilerServices;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionCanvas.Rendering;
using VisionGeometry;

namespace TeachingToolWpf.Imaging.Tests;

public class RoiPackingReuseTests
{
    [Fact]
    public void ReusingPackingBuffersKeepsPreviouslyOwnedRolesAndClearsOldEntries()
    {
        var parent = Roi(RoiGeometry.Create(RoiShapeKind.Triangle, new(100, 100), 80, 60));
        var duplicate = parent with
        {
            Id = Guid.NewGuid()
        };
        var child = Roi(RoiGeometry.Create(RoiShapeKind.Circle, new(100, 100), 10, 10), parent.Id);
        var before = State(parent, duplicate, child);
        var first = Block(before);
        var instances = new List<RoiInstance>();
        var roles = new List<uint>();
        var unique = new Dictionary<RoiGeometry, int>();
        var expected = RoiInstance.PackWithRoles(first, 0, before);

        RoiInstance.PackInto(first, 0, before, instances, roles, unique);
        Assert.Equal(expected.Instances, instances);
        Assert.Equal(expected.Roles, roles);
        Assert.Empty(unique);
        var retainedRoles = new RoiRoleCache(before, roles.ToArray());
        var geometryCapacity = instances.Capacity;
        var roleCapacity = roles.Capacity;
        RoiInstance.ReleasePackingBuffers(instances, roles, unique);
        Assert.Empty(instances);
        Assert.Empty(roles);
        Assert.Equal(geometryCapacity, instances.Capacity);
        Assert.Equal(roleCapacity, roles.Capacity);

        var next = State(Roi(new(new(400, 200), 40, 20, 15)));
        RoiInstance.PackInto(Block(next), 0, next, instances, roles, unique);
        Assert.Single(instances);
        Assert.Equal((uint)RoiRole.Child, Assert.Single(roles));
        Assert.Equal(expected.Roles, retainedRoles.Values);
        Assert.False(retainedRoles.Refresh(first, 0, before, expected.Instances.Length));
        Assert.Empty(unique);

        RoiInstance.ReleasePackingBuffers(instances, roles, unique);
        RoiInstance.PackInto(first, 0, before, instances, roles, unique);
        Assert.Equal(expected.Instances, instances);
        Assert.Equal(expected.Roles, roles);
    }

    [Fact]
    public void FailedEnumerationReleasesShapeReferencesAndBuffersCanBeReused()
    {
        var roi = Roi(RoiGeometry.Create(RoiShapeKind.Polygon, new(100, 100), 40, 30));
        var state = State(roi);
        var source = Block(state);
        var failing = new RoiSpatialBlock(source.Identity, source.Bounds, 1, _ => FailAfter(roi));
        var instances = new List<RoiInstance>();
        var roles = new List<uint>();
        var unique = new Dictionary<RoiGeometry, int>();

        Assert.Throws<InvalidOperationException>(() => RoiInstance.PackInto(failing, 0, state, instances, roles, unique));
        Assert.Empty(unique);
        RoiInstance.ReleasePackingBuffers(instances, roles, unique);
        RoiInstance.PackInto(source, 0, state, instances, roles, unique);
        Assert.Equal(RoiInstance.Pack(source, 0), instances);
        Assert.Equal(RoiInstance.ResolveRoles(source, 0, state), roles);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompletedOrFailedPackingDoesNotRetainPriorPolygon(bool fail)
    {
        var instances = new List<RoiInstance>();
        var roles = new List<uint>();
        var unique = new Dictionary<RoiGeometry, int>();
        var (geometry, shape) = PackDetachedPolygon(instances, roles, unique, fail);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.Empty(unique);
        Assert.False(geometry.TryGetTarget(out _));
        Assert.False(shape.TryGetTarget(out _));
        GC.KeepAlive(instances);
        GC.KeepAlive(roles);
        GC.KeepAlive(unique);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference<RoiGeometry> Geometry, WeakReference<RoiShape> Shape) PackDetachedPolygon(
        List<RoiInstance> instances, List<uint> roles, Dictionary<RoiGeometry, int> unique, bool fail)
    {
        var roi = Roi(RoiGeometry.Create(RoiShapeKind.Polygon, new(100, 100), 40, 30));
        var state = State(roi);
        var block = Block(state);
        if (fail)
        {
            var failing = new RoiSpatialBlock(block.Identity, block.Bounds, 1, _ => FailAfter(roi));
            Assert.Throws<InvalidOperationException>(() => RoiInstance.PackInto(failing, 0, state, instances, roles, unique));
        }
        else
        {
            RoiInstance.PackInto(block, 0, state, instances, roles, unique);
        }
        return (new(roi.Geometry), new(roi.Geometry.Shape));
    }

    [Fact]
    public void LargePolygonBlockKeepsEveryEdgeAndReleasesOversizedScratchAfterUpload()
    {
        const int RoiCount = 2048;
        const int VertexCount = 17;
        var shape = RoiGeometry.Polygon(Enumerable.Range(0, VertexCount)
            .Select(index => new PointD(Math.Cos(index * Math.PI * 2 / VertexCount) * 10, Math.Sin(index * Math.PI * 2 / VertexCount) * 10)));
        var state = State(Enumerable.Range(0, RoiCount).Select(index => Roi(shape.Translate(new(index * 30, 20)))).ToArray());
        var block = Block(state);
        var instances = new List<RoiInstance>();
        var roles = new List<uint>();
        var unique = new Dictionary<RoiGeometry, int>();

        RoiInstance.PackInto(block, 0, state, instances, roles, unique);
        Assert.Equal(RoiCount * VertexCount, instances.Count);
        Assert.Equal(instances.Count, roles.Count);
        Assert.All(instances, instance => Assert.Equal(2u, instance.Summary));
        Assert.True(instances.Capacity > RoiInstance.MaximumRetainedInstanceCount);
        var ownedRoles = roles.ToArray();
        RoiInstance.ReleasePackingBuffers(instances, roles, unique);

        Assert.Equal(0, instances.Capacity);
        Assert.Equal(0, roles.Capacity);
        Assert.Empty(unique);
        Assert.Equal(RoiCount * VertexCount, ownedRoles.Length);
    }

    [Theory]
    [InlineData(RoiShapeKind.Triangle, 0d)]
    [InlineData(RoiShapeKind.Triangle, 1e12)]
    [InlineData(RoiShapeKind.Polygon, 0d)]
    [InlineData(RoiShapeKind.Polygon, 1e12)]
    public void DirectVertexPackingExactlyMatchesDomainOutline(RoiShapeKind kind, double extent)
    {
        var center = new PointD(extent + 100, extent + 100);
        var shape = kind == RoiShapeKind.Triangle ? RoiGeometry.Create(kind, center, 40, 20)
            : RoiGeometry.Polygon([center + new PointD(-20, -10), center + new PointD(20, -10), center,
                center + new PointD(20, 10), center + new PointD(-20, 10)]);
        shape = shape.RotateAround(center, 37);
        var state = State(Roi(shape));
        var block = Block(state);
        var outline = shape.Outline;
        var expected = outline.Select((point, index) => new RoiInstance(Vector(point - block.Bounds.Center),
            Vector(outline[(index + 1) % outline.Length] - point), default, 2u)).ToArray();
        var instances = new List<RoiInstance>();
        var roles = new List<uint>();
        var unique = new Dictionary<RoiGeometry, int>();

        RoiInstance.PackInto(block, 0, state, instances, roles, unique);
        Assert.Equal(expected, instances);
        Assert.Equal(expected, RoiInstance.Pack(block, 0));
        Assert.Equal(expected, RoiInstance.PackWithRoles(block, 0, state).Instances);
        Assert.All(roles, role => Assert.Equal((uint)RoiRole.Child, role));
    }

    private static IEnumerable<(Roi? Roi, BoundsD Bounds, int Count)> FailAfter(Roi roi)
    {
        yield return (roi, roi.Geometry.Bounds, 1);
        throw new InvalidOperationException("진단용 공간 열거 실패.");
    }

    private static Vector2 Vector(PointD point) => new((float)point.X, (float)point.Y);

    private static Roi Roi(RoiGeometry shape, Guid? parent = null) => new(Guid.NewGuid(), "ROI", shape, parent, false, 0);

    private static DocumentState State(params Roi[] rois) => new(rois.ToImmutableDictionary(roi => roi.Id), [], null, ViewportTransform.Identity);

    private static RoiSpatialBlock Block(DocumentState state) => state.Snapshot.Spatial.Blocks(state.Snapshot.Spatial.Bounds).Single();
}
