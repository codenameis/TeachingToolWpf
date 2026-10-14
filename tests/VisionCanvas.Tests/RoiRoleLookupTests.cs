using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionCanvas.Rendering;
using VisionGeometry;

namespace TeachingToolWpf.Imaging.Tests;

public class RoiRoleLookupTests
{
    [Fact]
    public async Task IndependentRoiUsesChildStyleBeforeAndAfterParentAttachment()
    {
        var editor = new EditorSession();
        var leaf = editor.Add(new(new(100, 100), 20, 20, 0));
        var lookup = new RoiRoleLookup();
        AssertLeaf();
        var parent = editor.Add(new(new(100, 100), 200, 200, 0));
        editor.CommitPrepared(await editor.PrepareAttachAsync([leaf], parent));
        AssertLeaf();
        Assert.Equal(RoiRole.Parent, RoiStyleRoles.Resolve(editor.State, parent));
        editor.Undo();
        Assert.Null(editor.State.Rois[leaf].ParentId);
        AssertLeaf();

        void AssertLeaf()
        {
            lookup.Update(editor.State);
            Assert.Equal(RoiRole.Child, RoiStyleRoles.Resolve(editor.State, leaf));
            Assert.Equal((uint)RoiRole.Child, lookup.Resolve(leaf));
        }
    }

    [Fact]
    public void FourHierarchyLevelsUseDistinctCachedRolesAndRestoreAfterUndo()
    {
        var editor = new EditorSession();
        var root = editor.Add(new(new(100, 100), 400, 400, 0));
        var module = editor.AddChild(root, new(new(100, 100), 300, 300, 0));
        var group = editor.AddChild(module, new(new(100, 100), 200, 200, 0));
        var leaf = editor.AddChild(group, new(new(100, 100), 20, 20, 0));
        var lookup = new RoiRoleLookup();
        lookup.Update(editor.State);
        Assert.Equal((uint)RoiRole.Root, lookup.Resolve(root));
        Assert.Equal((uint)RoiRole.Module, lookup.Resolve(module));
        Assert.Equal((uint)RoiRole.Parent, lookup.Resolve(group));
        Assert.Equal((uint)RoiRole.Child, lookup.Resolve(leaf));
        editor.Undo();
        lookup.Update(editor.State);
        Assert.Equal((uint)RoiRole.Module, lookup.Resolve(root));
        editor.Redo();
        lookup.Update(editor.State);
        Assert.Equal((uint)RoiRole.Root, lookup.Resolve(root));
        var scene = new RenderScene(editor.State, null, 0, 800, 600, 1, new());
        var constants = new RoiDrawConstants(scene, default, default, false, RoiDrawPass.Stroke);
        Assert.Equal(scene.RoiStyles.Module.Color, constants.ModuleColor);
        Assert.Equal(scene.RoiStyles.Root.Color, constants.RootColor);
    }

    [Theory]
    [InlineData(RoiShapeKind.Rectangle)]
    [InlineData(RoiShapeKind.Circle)]
    [InlineData(RoiShapeKind.Triangle)]
    [InlineData(RoiShapeKind.Polygon)]
    public void LookupPackingMatchesDocumentRolesAcrossHierarchyChangesAndUndo(RoiShapeKind kind)
    {
        var parent = Roi(RoiGeometry.Create(kind, new(100, 100), 80, 80));
        var duplicate = parent with
        {
            Id = Guid.NewGuid(),
            ParentId = parent.Id
        };
        var child = Roi(new(new(400, 100), 40, 40, 0)) with
        {
            ParentId = duplicate.Id
        };
        var before = State(parent, duplicate, child);
        var after = before.WithRois(before.Rois.SetItem(duplicate.Id, duplicate with
        {
            ParentId = null
        })
            .SetItem(child.Id, child with
            {
                ParentId = null
            }), [duplicate.Id, child.Id]);
        var block = before.Snapshot.Spatial.Blocks(before.Snapshot.Spatial.Bounds).Single();
        var lookup = new RoiRoleLookup();
        var instances = new List<RoiInstance>();
        var roles = new List<uint>();
        var unique = new Dictionary<RoiGeometry, int>();
        var cache = new RoiRoleCache();
        foreach (var state in new[] { before, after, before, after, before })
        {
            lookup.Update(state);
            foreach (var roi in state.Rois.Values)
            {
                Assert.Equal((uint)RoiStyleRoles.Resolve(state, roi.Id), lookup.Resolve(roi.Id));
            }
            var expected = RoiInstance.PackWithRoles(block, 0, state);
            RoiInstance.PackInto(block, 0, state, instances, roles, unique, lookup);
            Assert.Equal(expected.Instances, instances);
            Assert.Equal(expected.Roles, roles);
            cache.Refresh(block, 0, state, instances.Count, lookup);
            Assert.Equal(expected.Roles, cache.Values);
        }
        Assert.True(lookup.EstimatedBytes > 0);
        lookup.Update(DocumentState.Empty);
        // Dictionary.TrimExcess는 빈 사전에도 최소 bucket을 남길 수 있다.
        Assert.InRange(lookup.EstimatedBytes, 0, 256);
        Assert.All(before.Rois.Keys, id => Assert.Equal((uint)RoiRole.Child, lookup.Resolve(id)));
    }

    [Fact]
    public void UndoRedoUsesPreviousRoleArrayWithoutEnumeratingGeometryAgain()
    {
        var parent = Roi(new(new(100, 100), 40, 40, 0));
        var child = Roi(new(new(200, 100), 10, 10, 0)) with
        {
            ParentId = parent.Id
        };
        var before = State(parent, child);
        var after = before.WithRois(before.Rois.SetItem(child.Id, child with
        {
            ParentId = null
        }), [child.Id]);
        var block = before.Snapshot.Spatial.Blocks(before.Snapshot.Spatial.Bounds).Single();
        var original = RoiInstance.ResolveRoles(block, 0, before);
        var cache = new RoiRoleCache(before, original);
        var noEnumeration = new RoiSpatialBlock(block.Identity, block.Bounds, block.Count,
            _ => throw new InvalidOperationException("Cached roles must not enumerate geometry."));
        Assert.True(cache.Refresh(noEnumeration, 0, after, original.Length));
        var detached = cache.Values;
        Assert.All(detached, role => Assert.Equal((uint)RoiRole.Child, role));
        Assert.True(cache.Refresh(noEnumeration, 0, before, original.Length));
        Assert.Same(original, cache.Values);
        Assert.True(cache.Refresh(noEnumeration, 0, after, original.Length));
        Assert.Same(detached, cache.Values);
    }

    [Fact]
    public void LookupAndBothCachedRoleVersionsDoNotRetainDocuments()
    {
        var (lookup, cache, versions) = DetachedCaches();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.All(versions, version => Assert.False(version.TryGetTarget(out _)));
        GC.KeepAlive(lookup);
        GC.KeepAlive(cache);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (RoiRoleLookup, RoiRoleCache, WeakReference<object>[]) DetachedCaches()
    {
        var root = Roi(new(new(100, 100), 40, 40, 0));
        var child = Roi(new(new(200, 100), 10, 10, 0)) with
        {
            ParentId = root.Id
        };
        var grandchild = Roi(new(new(300, 100), 5, 5, 0)) with
        {
            ParentId = child.Id
        };
        var before = State(root, child, grandchild);
        var after = before.WithRois(before.Rois.SetItem(child.Id, child with
        {
            ParentId = null
        }), [child.Id]);
        var lookup = new RoiRoleLookup();
        var block = before.Snapshot.Spatial.Blocks(before.Snapshot.Spatial.Bounds).Single();
        var cache = new RoiRoleCache(before, RoiInstance.ResolveRoles(block, 0, before));
        lookup.Update(after);
        cache.Refresh(block, 0, after, cache.Values.Length, lookup);
        return (lookup, cache, [new(before.Snapshot.Children), new(after.Snapshot.Children)]);
    }

    private static Roi Roi(RoiGeometry geometry) => new(Guid.NewGuid(), "ROI", geometry, null, false, 0);

    private static DocumentState State(params Roi[] rois) => new(rois.ToImmutableDictionary(roi => roi.Id), [], null, ViewportTransform.Identity);
}
