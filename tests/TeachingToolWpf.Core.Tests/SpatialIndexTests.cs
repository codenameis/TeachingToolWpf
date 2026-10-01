using System.Collections.Immutable;
using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;

namespace TeachingToolWpf.Core.Tests;

public class SpatialIndexTests
{
    [Fact] public void IndexedHierarchyMatchesExhaustiveRotatedContainment()
    {
        var random = new Random(123);
        var rois = Enumerable.Range(0, 300).Select(i => new Roi(Guid.NewGuid(), $"R{i}",
            new(new(random.Next(1000), random.Next(1000)), random.Next(10, 700), random.Next(10, 700), random.Next(360)), null, false, i)).ToImmutableDictionary(r => r.Id);
        var hierarchy = RoiHierarchy.Rebuild(rois);
        foreach (var child in rois.Values)
        {
            var expected = rois.Values.Where(candidate => candidate.Geometry.Contains(child.Geometry)).OrderBy(r => r.Geometry.Area).ThenBy(r => r.Id).FirstOrDefault()?.Id;
            Assert.Equal(expected, hierarchy[child.Id].ParentId);
        }
    }
    [Fact] public void BroadPhaseReturnsAllOverlappingBoundsIncludingEdges()
    {
        var roi = new Roi(Guid.NewGuid(), "edge", new(new(0, 0), 20, 20, 45), null, false, 1);
        var index = new RoiSpatialIndex([roi]);
        Assert.Single(index.At(new(roi.Geometry.Bounds.Right, 0)));
        Assert.Empty(index.At(new(100, 100)));
    }
}
