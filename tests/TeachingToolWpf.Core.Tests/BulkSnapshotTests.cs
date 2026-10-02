using System.Collections.Immutable;
using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;

namespace TeachingToolWpf.Core.Tests;

public class BulkSnapshotTests
{
    [Fact] public void BulkGeometryRebuildPreservesRowsTopologyAndOldSnapshot()
    {
        var values = Enumerable.Range(0, 5000).Select(i => new Roi(Guid.NewGuid(), $"R{i}",
            new(new(i * 20, 50), 10, 10, 0), null, false, i)).ToImmutableDictionary(r => r.Id);
        var before = DocumentState.Empty with { Rois = values };
        var nextValues = values.ToImmutableDictionary(p => p.Key, p => p.Value with { Geometry = p.Value.Geometry.Translate(new(0, 100)) });
        var after = before.WithRois(nextValues, values.Keys);

        Assert.Same(before.Snapshot.RowVersion, after.Snapshot.RowVersion);
        Assert.Same(before.Snapshot.Children, after.Snapshot.Children);
        foreach (var roi in values.Values.Take(5))
        {
            Assert.Equal(roi, Assert.Single(before.Snapshot.Spatial.At(roi.Geometry.Center)));
            Assert.Empty(after.Snapshot.Spatial.At(roi.Geometry.Center));
            Assert.Equal(nextValues[roi.Id], Assert.Single(after.Snapshot.Spatial.At(roi.Geometry.Center + new PointD(0, 100))));
        }
    }

    [Fact] public void MixedMetadataGeometryAndParentChangesAllReachTheirIndexes()
    {
        var parent = new Roi(Guid.NewGuid(), "parent", new(new(50, 50), 100, 100, 0), null, true, 1);
        var child = new Roi(Guid.NewGuid(), "child", new(new(50, 50), 10, 10, 0), parent.Id, false, 2);
        var before = DocumentState.Empty with { Rois = new[] { parent, child }.ToImmutableDictionary(r => r.Id) };
        var changedParent = parent with { Name = "renamed", Order = 5 };
        var changedChild = child with { Geometry = child.Geometry.Translate(new(200, 0)), ParentId = null };
        var after = before.WithRois(before.Rois.SetItem(parent.Id, changedParent).SetItem(child.Id, changedChild), [parent.Id, child.Id]);

        Assert.NotSame(before.Snapshot.RowVersion, after.Snapshot.RowVersion);
        Assert.Empty(after.Snapshot.Children);
        Assert.Equal(child.Id, Assert.Single(before.Snapshot.Children[parent.Id]));
        Assert.Equal(changedParent, Assert.Single(after.Snapshot.Spatial.At(parent.Geometry.Center)));
        Assert.Equal(changedChild, Assert.Single(after.Snapshot.Spatial.At(changedChild.Geometry.Center)));
    }
}
