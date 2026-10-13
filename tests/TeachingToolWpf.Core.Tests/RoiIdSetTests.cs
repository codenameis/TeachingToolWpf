using VisionCanvas.Core.Editing;

namespace TeachingToolWpf.Core.Tests;

public class RoiIdSetTests
{
    [Fact]
    public void BulkAndIncrementalChangesMatchSetSemanticsWithoutChangingSnapshots()
    {
        var ids = Enumerable.Range(0, 1000).Select(_ => Guid.NewGuid()).ToArray();
        var original = RoiIdSet.CreateRange(ids.Take(700).Concat(ids.Take(10)));
        var expected = ids.Take(700).ToHashSet();
        var current = original;
        for (var i = 0; i < 1000; i += 3)
        {
            current = current.Remove(ids[i]);
            expected.Remove(ids[i]);
        }
        for (var i = 0; i < 1000; i += 5)
        {
            current = current.Add(ids[i]);
            expected.Add(ids[i]);
        }
        Assert.Equal(expected.Count, current.Count);
        Assert.True(expected.SetEquals(current));
        Assert.True(current.SetEquals(expected));
        Assert.True(original.SetEquals(ids.Take(700)));
        Assert.Same(current, RoiIdSet.CreateRange(current));
        Assert.True(current.Union(ids).SetEquals(ids));
        Assert.True(current.Except(ids).IsEmpty);
        Assert.True(current.Except(current).IsEmpty);
        Assert.Same(current, current.Union(current));
        Assert.True(current.Union(ids.Take(10)).Except(ids.Take(10)).SetEquals(expected.Except(ids.Take(10))));
    }

    [Fact]
    public void RemovingThenAddingBaseAndDeltaIdsRestoresMembership()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        RoiIdSet initial = [first, first];
        var changed = initial.Add(second).Remove(first).Add(first).Remove(second);
        Assert.True(changed.SetEquals(initial));
        Assert.Single(changed);
        Assert.True(changed.IsSubsetOf([first, second]));
        Assert.True(changed.IsProperSubsetOf([first, second]));
        Assert.True(changed.IsSupersetOf([first, first]));
        Assert.False(changed.IsProperSupersetOf([first, first]));
        Assert.True(changed.IsProperSupersetOf([]));
        Assert.True(changed.Overlaps([first, second]));
        Assert.False(changed.Overlaps([second]));
    }
}
