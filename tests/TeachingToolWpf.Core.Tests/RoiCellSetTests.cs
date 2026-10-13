using VisionCanvas.Core.Editing;

namespace TeachingToolWpf.Core.Tests;

public class RoiCellSetTests
{
    [Fact]
    public void RangeAlgebraMatchesIndividualMembership()
    {
        var random = new Random(173);
        for (var trial = 0; trial < 100; trial++)
        {
            var left = Create();
            var right = Create();
            var a = Enumerable.Range(0, 200).Where(left.Contains).ToHashSet();
            var b = Enumerable.Range(0, 200).Where(right.Contains).ToHashSet();
            Check(left.Union(right), a.Union(b));
            Check(left.Intersect(right), a.Intersect(b));
            Check(left.Except(right), a.Except(b));
            Assert.True(left.SetEquals(left.Union(RoiCellSet.Empty)));
            Assert.False(left.Contains(-1));
            Assert.False(left.Contains(200));
        }

        RoiCellSet Create() => RoiCellSet.Create(Enumerable.Range(0, 20).Select(_ =>
        {
            var start = random.Next(200);
            return new RoiCellSet.Range(start, random.Next(start, 201));
        }));

        static void Check(RoiCellSet actual, IEnumerable<int> expected)
        {
            var values = expected.ToHashSet();
            Assert.Equal(values.Count, actual.Count);
            Assert.True(values.SetEquals(Enumerable.Range(0, 200).Where(actual.Contains)));
            for (var i = 1; i < actual.Ranges.Length; i++)
            {
                Assert.True(actual.Ranges[i - 1].End < actual.Ranges[i].Start);
            }
        }
    }

    [Fact]
    public void HundredMillionSelectionStaysCompressedAndPreservesHoles()
    {
        var all = RoiCellSet.All(100020001);
        var removed = RoiCellSet.Create([new(500, 700), new(10000, 90000000)]);
        var remaining = all.Except(removed);
        Assert.Equal(100020001 - removed.Count, remaining.Count);
        Assert.Equal(3, remaining.Ranges.Length);
        Assert.True(remaining.Union(removed).SetEquals(all));
        Assert.True(all.Except(all).IsEmpty);
        Assert.Throws<ArgumentOutOfRangeException>(() => RoiCellSet.Create([new(-1, 5)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoiCellSet.Create([new(5, 4)]));
    }
}
