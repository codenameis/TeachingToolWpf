using System.Collections.Immutable;
using VisionCanvas.Core.Alignment;
using VisionCanvas.Core.Editing;
using VisionGeometry;

namespace TeachingToolWpf.Core.Tests;

public class RoiAlignmentMapTests
{
    [Fact]
    public void HierarchicalAlignmentComposesGlobalModuleAndGroupInOrderAndPreservesRoiMetadata()
    {
        var (state, moduleA, _, group, leaf, sibling) = CreateDocument();
        var map = new RoiAlignmentMap(state);
        Assert.True(map.TryAlign(null,
            [new(0, 0), new(1000, 0)],
            [new(10, 20), new(1010, 20)],
            out map, out var error), error);
        Assert.True(map.TryAlign(moduleA.Id,
            [new(10, 20), new(110, 20)],
            [new(15, 22), new(15, 122)],
            out map, out error), error);
        Assert.True(map.TryAlign(group.Id,
            [new(30, 30)],
            [new(33, 26)],
            out map, out error), error);

        var expected = new RigidTransform2D(new(30, 30), 90, new(3, -4))
            .Apply(leaf.Geometry.Center);
        var actual = map.GetAlignment(leaf.Id)!;
        AssertClose(expected, actual.Transform.Apply(leaf.Geometry.Center));
        var aligned = map.Map(leaf.Id);
        Assert.Equal(leaf.Id, aligned.Id);
        Assert.Equal(leaf.ParentId, aligned.ParentId);
        Assert.Equal(leaf.Name, aligned.Name);
        Assert.Equal(leaf.SearchRange, aligned.SearchRange);
        Assert.Equal(leaf.Masks, aligned.Masks);
        AssertClose(expected, aligned.Geometry.Center);
        Assert.Equal(90, aligned.Geometry.Angle, 6);
        Assert.Equal(leaf.Geometry.Width, aligned.Geometry.Width, 8);
        Assert.Equal(leaf.Geometry.Height, aligned.Geometry.Height, 8);

        var alignedSibling = map.Map(sibling.Id);
        AssertClose(sibling.Geometry.Center + new PointD(10, 20), alignedSibling.Geometry.Center);
        Assert.Equal(0, alignedSibling.Geometry.Angle, 8);
        Assert.Equal(leaf, state.Rois[leaf.Id]);
    }

    [Fact]
    public void ChangingAnAncestorAfterDescendantAlignmentIsRejectedWithoutMutatingPreviousMap()
    {
        var (state, moduleA, _, group, leaf, _) = CreateDocument();
        var map = new RoiAlignmentMap(state);
        Assert.True(map.TryAlign(group.Id, [new(0, 0)], [new(3, 4)], out map, out _));
        var before = map.Map(leaf.Id);
        Assert.False(map.TryAlign(moduleA.Id, [new(0, 0)], [new(10, 0)], out var rejected, out var error));
        Assert.Same(map, rejected);
        Assert.Contains("하위", error);
        Assert.Equal(before, map.Map(leaf.Id));
    }

    [Fact]
    public void LeafAndUnknownScopesAndFailedFitsDoNotChangeTheMap()
    {
        var (state, module, _, _, leaf, _) = CreateDocument();
        var map = new RoiAlignmentMap(state);
        foreach (var id in new[] { leaf.Id, Guid.NewGuid() })
        {
            Assert.False(map.TryAlign(id, [new(0, 0)], [new(1, 1)], out var failed, out var error));
            Assert.Same(map, failed);
            Assert.NotEmpty(error);
        }
        Assert.False(map.TryAlign(module.Id, [], [], out var rejected, out _));
        Assert.Same(map, rejected);
    }

    [Fact]
    public void BrokenOrCyclicParentsAreReportedInsteadOfGuessingByPosition()
    {
        var (state, module, _, group, leaf, _) = CreateDocument();
        var orphaned = state.WithRois(state.Rois.SetItem(group.Id, group with
        {
            ParentId = Guid.NewGuid()
        }), [group.Id]);
        Assert.Throws<InvalidOperationException>(() => new RoiAlignmentMap(orphaned).Map(leaf.Id));
        var cyclic = state.WithRois(state.Rois.SetItem(module.Id, module with
        {
            ParentId = group.Id
        }), [module.Id]);
        Assert.Throws<InvalidOperationException>(() => new RoiAlignmentMap(cyclic).Map(leaf.Id));
    }

    private static (DocumentState State, Roi ModuleA, Roi ModuleB, Roi Group, Roi Leaf, Roi Sibling) CreateDocument()
    {
        var root = new Roi(Guid.NewGuid(), "전체", new(new(500, 500), 1000, 1000, 0), null, true, 1);
        var moduleA = new Roi(Guid.NewGuid(), "모듈 A", new(new(50, 50), 100, 100, 0), root.Id, true, 2);
        var moduleB = new Roi(Guid.NewGuid(), "모듈 B", new(new(250, 50), 100, 100, 0), root.Id, true, 3);
        var group = new Roi(Guid.NewGuid(), "그룹 A1", new(new(25, 35), 20, 20, 0), moduleA.Id, true, 4);
        var leaf = new Roi(Guid.NewGuid(), "검사 ROI", new(new(25, 35), 4, 4, 0), group.Id, false, 5)
        {
            SearchRange = new(20, 20),
            Masks = [new(MaskShape.Rectangle, new(0, 0, 0.2, 0.2))]
        };
        var sibling = new Roi(Guid.NewGuid(), "다른 모듈 ROI", new(new(205, 35), 4, 4, 0), moduleB.Id, false, 6);
        var rois = new[] { root, moduleA, moduleB, group, leaf, sibling }.ToImmutableDictionary(r => r.Id);
        return (new(rois, RoiIdSet.Empty, null, ViewportTransform.Identity), moduleA, moduleB, group, leaf, sibling);
    }

    private static void AssertClose(PointD expected, PointD actual)
    {
        Assert.InRange((expected - actual).Length, 0, 1e-8);
    }
}
