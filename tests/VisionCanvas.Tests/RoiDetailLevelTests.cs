using System.Collections.Immutable;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionCanvas.Rendering;
using VisionGeometry;

namespace TeachingToolWpf.Imaging.Tests;

public class RoiDetailLevelTests
{
    [Theory]
    [InlineData(RoiShapeKind.Rectangle, 1)]
    [InlineData(RoiShapeKind.Circle, 1)]
    [InlineData(RoiShapeKind.Triangle, 3)]
    [InlineData(RoiShapeKind.Polygon, 5)]
    public void FullDetailKeepsDuplicateGeometryAndRoleSlotsAtEveryFinerAggregate(RoiShapeKind kind, int shapeInstances)
    {
        var (state, _, _) = Fixture(kind);
        var block = Block(state);
        var instances = new List<RoiInstance>();
        var roles = new List<uint>();
        var unique = new Dictionary<RoiGeometry, int>();
        Assert.False(RoiInstance.PackInto(block, 32, state, instances, roles, unique));
        var expectedInstances = instances.ToArray();
        var expectedRoles = roles.ToArray();
        Assert.Equal(shapeInstances + 1, instances.Count);
        Assert.NotEqual(block.Count, instances.Count);
        Assert.Equal(shapeInstances, roles.Count(role => role == (uint)RoiRole.Parent));
        Assert.Single(roles, role => role == (uint)RoiRole.Child);

        foreach (var aggregate in new[] { 16d, 8, 1, 0 })
        {
            Assert.False(RoiInstance.PackInto(block, aggregate, state, instances, roles, unique));
            Assert.Equal(expectedInstances, instances);
            Assert.Equal(expectedRoles, roles);
        }
    }

    [Fact]
    public void SummaryFlagFollowsMixedBvhOutputAndResetsForFullDetail()
    {
        var values = Enumerable.Range(0, 8).Select(index => Roi(new(new(100 + (index * 2), 100), 1, 1, 0)))
            .Append(Roi(new(new(500, 100), 40, 40, 0))).ToArray();
        var state = State(values);
        var block = Block(state);
        var instances = new List<RoiInstance>();
        var roles = new List<uint>();
        var unique = new Dictionary<RoiGeometry, int>();
        Assert.True(RoiInstance.PackInto(block, 10, state, instances, roles, unique));
        Assert.Contains(instances, instance => instance.Summary == 1);
        Assert.Contains(instances, instance => instance.Summary != 1);
        Assert.Equal(RoiInstance.Pack(block, 10), instances);

        Assert.False(RoiInstance.PackInto(block, 0, state, instances, roles, unique));
        Assert.Equal(values.Length, instances.Count);
        Assert.DoesNotContain(instances, instance => instance.Summary == 1);
    }

    [Fact]
    public void LargerFullDetailProbeRefreshesDetachedRolesWithoutReenumeratingTheBlock()
    {
        var (before, _, child) = Fixture(RoiShapeKind.Polygon);
        var block = Block(before);
        var initial = RoiInstance.PackWithRoles(block, 8, before);
        var cache = new RoiRoleCache(before, initial.Roles);
        var after = before.WithRois(before.Rois.SetItem(child.Id, child with
        {
            ParentId = null
        }), [child.Id]);
        Assert.Same(block.Identity, Block(after).Identity);
        Assert.NotNull(block.Detail(0).Single(item => item.Roi?.Id == child.Id).Roi!.ParentId);

        var instances = new List<RoiInstance>();
        var roles = new List<uint>();
        Assert.False(RoiInstance.PackInto(block, 32, after, instances, roles, []));
        Assert.Equal(initial.Instances, instances);
        Assert.True(cache.RefreshPacked(after, roles.ToArray(), instances.Count));
        Assert.All(cache.Values, role => Assert.Equal((uint)RoiRole.Child, role));
        Assert.False(cache.Refresh(NoEnumeration(block), 32, after, instances.Count));
    }

    [Fact]
    public void UnchangedPackedRolesKeepTheirOwnedArrayAndAdvanceTheHierarchyVersion()
    {
        var first = Roi(new(new(100, 100), 40, 40, 0));
        var second = Roi(new(new(300, 100), 40, 40, 0));
        var moved = Roi(new(new(110, 100), 10, 10, 0), first.Id);
        var before = State(first, second, moved,
            Roi(new(new(120, 100), 10, 10, 0), first.Id), Roi(new(new(310, 100), 10, 10, 0), second.Id));
        var block = Block(before);
        var original = RoiInstance.ResolveRoles(block, 8, before);
        var cache = new RoiRoleCache(before, original);
        var after = before.WithRois(before.Rois.SetItem(moved.Id, moved with
        {
            ParentId = second.Id
        }), [moved.Id]);
        Assert.NotSame(before.Snapshot.Children, after.Snapshot.Children);
        var next = RoiInstance.ResolveRoles(block, 32, after);

        Assert.False(cache.RefreshPacked(after, next, next.Length));
        Assert.Same(original, cache.Values);
        Assert.False(cache.Refresh(NoEnumeration(block), 32, after, next.Length));
    }

    [Fact]
    public void MismatchedPackedRoleCountLeavesThePreviousVersionAndValuesIntact()
    {
        var (before, _, child) = Fixture(RoiShapeKind.Rectangle);
        var block = Block(before);
        var original = RoiInstance.ResolveRoles(block, 8, before);
        var cache = new RoiRoleCache(before, original);
        var after = before.WithRois(before.Rois.SetItem(child.Id, child with
        {
            ParentId = null
        }), [child.Id]);

        Assert.Throws<InvalidOperationException>(() => cache.RefreshPacked(after, [], original.Length));
        Assert.Same(original, cache.Values);
        Assert.True(cache.Refresh(block, 32, after, original.Length));
        Assert.All(cache.Values, role => Assert.Equal((uint)RoiRole.Child, role));
    }

    private static (DocumentState State, Roi Parent, Roi Child) Fixture(RoiShapeKind kind)
    {
        var shape = RoiGeometry.Create(kind, new(100, 100), 40, 40).RotateAround(new(100, 100), 23);
        var parent = Roi(shape);
        var duplicate = Roi(new(shape.Center, shape.Width, shape.Height, shape.Angle, shape.Shape));
        var child = Roi(new(new(250, 100), 10, 10, 0), parent.Id);
        return (State(parent, duplicate, child), parent, child);
    }

    private static RoiSpatialBlock NoEnumeration(RoiSpatialBlock block)
        => new(block.Identity, block.Bounds, block.Count, _ => throw new InvalidOperationException("같은 계층 버전을 다시 순회했습니다."));

    private static Roi Roi(RoiGeometry geometry, Guid? parent = null) => new(Guid.NewGuid(), "ROI", geometry, parent, false, 0);

    private static DocumentState State(params Roi[] rois) => new(rois.ToImmutableDictionary(roi => roi.Id), [], null, ViewportTransform.Identity);

    private static RoiSpatialBlock Block(DocumentState state) => state.Snapshot.Spatial.Blocks(state.Snapshot.Spatial.Bounds).Single();
}
