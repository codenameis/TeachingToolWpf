using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionCanvas.Rendering;
using VisionGeometry;

namespace TeachingToolWpf.Imaging.Tests;

public class RoiStyleRenderingTests
{
    [Fact]
    public void RetainedRoleCacheDoesNotKeepOldDocumentHierarchyAlive()
    {
        var (cache, hierarchy) = CreateDetachedCache();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(hierarchy.TryGetTarget(out _));
        Assert.Contains((uint)RoiRole.Parent, cache.Values);
        GC.KeepAlive(cache);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (RoiRoleCache Cache, WeakReference<object> Hierarchy) CreateDetachedCache()
    {
        var parent = Roi("parent", new(100, 100), 100);
        var state = State(parent, Roi("child", new(100, 100), 10, parent.Id));
        var block = Block(state);
        var cache = new RoiRoleCache();
        cache.Refresh(block, 0, state, RoiInstance.Pack(block, 0).Length);
        return (cache, new(state.Snapshot.Children));
    }

    [Fact]
    public void MetadataOnlyDetachmentRefreshesRolesWithSameGeometryBlock()
    {
        var parent = Roi("parent", new(100, 100), 100);
        var child = Roi("child", new(110, 100), 10, parent.Id);
        var before = State(parent, child);
        var block = Block(before);
        var geometry = RoiInstance.Pack(block, 0);
        var cache = new RoiRoleCache();
        Assert.True(cache.Refresh(block, 0, before, geometry.Length));
        AssertRoles(block, before, cache.Values);
        Assert.Contains((uint)RoiRole.Parent, cache.Values);
        Assert.Contains((uint)RoiRole.Child, cache.Values);

        // A hierarchy-only snapshot shares BVH nodes; the old block deliberately still exposes old ROI metadata.
        var detached = before.WithRois(before.Rois.SetItem(child.Id, child with
        {
            ParentId = null
        }), [child.Id]);
        var sameBlock = Block(detached);
        Assert.Same(block.Identity, sameBlock.Identity);
        Assert.NotNull(block.Detail(0).Single(item => item.Roi?.Id == child.Id).Roi!.ParentId);
        Assert.True(cache.Refresh(block, 0, detached, geometry.Length));
        Assert.All(cache.Values, role => Assert.Equal((uint)RoiRole.Default, role));
        Assert.Equal(geometry, RoiInstance.Pack(sameBlock, 0));
        Assert.True(cache.Refresh(block, 0, before, geometry.Length));
        AssertRoles(block, before, cache.Values);
    }

    [Fact]
    public void ParentRoleWinsForNestedParentAndFallsBackAfterItsLastChildDetaches()
    {
        var root = Roi("root", new(100, 100), 200);
        var parent = Roi("nested parent", new(100, 100), 100, root.Id);
        var child = Roi("child", new(100, 100), 10, parent.Id);
        var before = State(root, parent, child);
        Assert.Equal(RoiRole.Parent, RoiStyleRoles.Resolve(before, parent.Id));
        var detached = before.WithRois(before.Rois.SetItem(child.Id, child with
        {
            ParentId = null
        }), [child.Id]);
        Assert.Equal(RoiRole.Child, RoiStyleRoles.Resolve(detached, parent.Id));
        Assert.Equal(RoiRole.Default, RoiStyleRoles.Resolve(detached, child.Id));
        Assert.Equal(RoiRole.Parent, RoiStyleRoles.Resolve(detached, root.Id));
    }

    [Fact]
    public void DuplicateGeometryKeepsOneInstanceAndUsesHighestCurrentRole()
    {
        var parent = Roi("parent", new(100, 100), 100);
        var duplicate = parent with
        {
            Id = Guid.NewGuid(),
            Name = "independent duplicate"
        };
        var child = Roi("child", new(100, 100), 10, parent.Id);
        var state = State(parent, duplicate, child);
        var block = Block(state);
        var geometry = RoiInstance.Pack(block, 0);
        var roles = RoiInstance.ResolveRoles(block, 0, state);
        Assert.Equal(2, geometry.Length);
        Assert.Equal(geometry.Length, roles.Length);
        var outer = Array.FindIndex(geometry, value => value.AxisX.Length() == 50);
        Assert.Equal((uint)RoiRole.Parent, roles[outer]);
        Assert.Equal(1u, Assert.Single(RoiInstance.Pack(block, 1000)).Summary);
        Assert.Equal((uint)RoiRole.Default, Assert.Single(RoiInstance.ResolveRoles(block, 1000, state)));
        var detached = state.WithRois(state.Rois.SetItem(child.Id, child with
        {
            ParentId = null
        }), [child.Id]);
        Assert.Equal(geometry, RoiInstance.Pack(Block(detached), 0));
        Assert.All(RoiInstance.ResolveRoles(block, 0, detached), role => Assert.Equal((uint)RoiRole.Default, role));
    }

    [Fact]
    public void RoleSlotsMatchMixedSummaryAndDeduplicatedGeometryOrder()
    {
        var parent = Roi("parent", new(100, 100), 100);
        var duplicate = parent with
        {
            Id = Guid.NewGuid(),
            Name = "duplicate"
        };
        var child = Roi("child", new(100, 100), 10, parent.Id);
        var state = State(parent, duplicate, child);
        var summary = new BoundsD(200, 200, 220, 220);
        var block = new RoiSpatialBlock(new(), new(0, 0, 300, 300), 8, _ =>
            new (Roi? Roi, BoundsD Bounds, int Count)[]
            { (null, summary, 5), (duplicate, duplicate.Geometry.Bounds, 1), (parent, parent.Geometry.Bounds, 1), (child, child.Geometry.Bounds, 1) });
        var geometry = RoiInstance.Pack(block, 50);
        var roles = RoiInstance.ResolveRoles(block, 50, state);
        Assert.Equal(3, geometry.Length);
        Assert.Equal(1u, geometry[0].Summary);
        Assert.Equal(new uint[] { (uint)RoiRole.Default, (uint)RoiRole.Parent, (uint)RoiRole.Child }, roles);
        Assert.Equal(32, Marshal.SizeOf<RoiInstance>());
    }

    [Fact]
    public void HierarchyChangeWithUnchangedRolesKeepsSidecarAndSkipsRepeatedEnumeration()
    {
        var first = Roi("first parent", new(100, 100), 100);
        var second = Roi("second parent", new(400, 100), 100);
        var firstChild = Roi("first child", new(100, 100), 10, first.Id);
        var secondChild = Roi("second child", new(400, 100), 10, second.Id);
        var additional = Roi("other child", new(105, 100), 10, first.Id);
        var state = State(first, second, firstChild, secondChild, additional);
        var enumerations = 0;
        var sourceBlock = Block(state);
        var tracked = new RoiSpatialBlock(sourceBlock.Identity, sourceBlock.Bounds, sourceBlock.Count, size =>
        {
            enumerations++;
            return sourceBlock.Detail(size);
        });
        var count = RoiInstance.Pack(tracked, 0).Length;
        var cache = new RoiRoleCache();
        Assert.True(cache.Refresh(tracked, 0, state, count));
        var roles = cache.Values;
        var changed = state.WithRois(state.Rois.SetItem(additional.Id, additional with
        {
            ParentId = second.Id
        }), [additional.Id]);
        Assert.False(cache.Refresh(tracked, 0, changed, count));
        Assert.Same(roles, cache.Values);
        var checkedCount = enumerations;
        Assert.False(cache.Refresh(tracked, 0, changed, count));
        Assert.Equal(checkedCount, enumerations);
        var renamed = changed.WithRois(changed.Rois.SetItem(first.Id, first with
        {
            Name = "renamed"
        }), [first.Id]);
        Assert.False(cache.Refresh(tracked, 0, renamed, count));
        Assert.Equal(checkedCount, enumerations);
    }

    [Fact]
    public void StyleAndNavigationChangesOnlyAlterConstantsAndKeepRoleSlots()
    {
        var parent = Roi("parent", new(100, 100), 100);
        var child = Roi("child", new(100, 100), 10, parent.Id);
        var state = State(parent, child);
        var block = Block(state);
        var geometry = RoiInstance.Pack(block, 0);
        var cache = new RoiRoleCache();
        cache.Refresh(block, 0, state, geometry.Length);
        var roles = cache.Values;
        var standard = RoiStylePalette.Standard;
        var palette = new RoiStylePalette(standard.Default.WithThickness(2), standard.Child.WithThickness(3), standard.Parent.WithThickness(4));
        RenderScene Scene(RoiStylePalette styles) => new(state, null, 0, 800, 600, 1, new(), Styles: styles);
        var original = new RoiDrawConstants(Scene(standard), block.Bounds.Center, default, false, RoiDrawPass.Stroke);
        var customized = new RoiDrawConstants(Scene(palette), block.Bounds.Center, default, false, RoiDrawPass.Stroke);
        Assert.Equal(new Vector4(1, 1, 1, 0), original.StrokeWidths);
        Assert.Equal(new Vector4(2, 3, 4, 0), customized.StrokeWidths);
        Assert.Equal(original.DefaultColor, customized.DefaultColor);
        Assert.Equal(original.ChildColor, customized.ChildColor);
        Assert.Equal(original.ParentColor, customized.ParentColor);
        var selected = new RoiDrawConstants(Scene(palette), block.Bounds.Center, default, true, RoiDrawPass.Stroke);
        Assert.Equal(new Vector4(2.5f, 3.5f, 4.5f, 0), selected.StrokeWidths);
        Assert.Equal(palette.Parent.SelectedColor, selected.ParentColor);
        var shadow = new RoiDrawConstants(Scene(standard), block.Bounds.Center, default, true, RoiDrawPass.Shadow);
        Assert.Equal(new Vector4(3, 3, 3, 0), shadow.StrokeWidths);
        var navigated = state with
        {
            Viewport = state.Viewport.Pan(new(100, 50))
        };
        Assert.False(cache.Refresh(block, 0, navigated, geometry.Length));
        Assert.Same(roles, cache.Values);
        Assert.Equal(geometry, RoiInstance.Pack(block, 0));
    }

    private static Roi Roi(string name, PointD center, double width, Guid? parent = null)
        => new(Guid.NewGuid(), name, new(center, width, width, 0), parent, false, 0);

    private static DocumentState State(params Roi[] rois) => new(rois.ToImmutableDictionary(roi => roi.Id), [], null, ViewportTransform.Identity);

    private static RoiSpatialBlock Block(DocumentState state) => state.Snapshot.Spatial.Blocks(state.Snapshot.Spatial.Bounds).Single();

    private static void AssertRoles(RoiSpatialBlock block, DocumentState state, uint[] roles)
    {
        var ordered = block.Detail(0).Select(item => item.Roi!).ToArray();
        Assert.Equal(ordered.Select(roi => (uint)RoiStyleRoles.Resolve(state, roi.Id)), roles);
    }
}
