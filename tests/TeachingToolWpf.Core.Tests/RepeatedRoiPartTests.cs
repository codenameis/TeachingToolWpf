using VisionCanvas.Core.Editing;
using VisionGeometry;

namespace TeachingToolWpf.Core.Tests;

public class RepeatedRoiPartTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(90)]
    [InlineData(-133)]
    public void PartialRotatedQueriesAndBoundsMatchActualCells(double angle)
    {
        var grid = new RepeatedRoiGrid(31, 29, 5, new(500, 500)).Transform(new(new(500, 500), angle));
        grid = grid.WithGeometry(24, new(new(570, 590), 13, 7, 17));
        grid = grid.WithGeometry(180, new(new(430, 510), 3, 19, -45));
        var cells = RoiCellSet.All(grid.Count).Except(RoiCellSet.Create([new(32, 69), new(225, 420)]));
        var part = new RepeatedRoiPart(grid, cells);
        var actual = Enumerable.Range(0, grid.Count).Where(cells.Contains).Select(grid.Geometry).ToArray();
        var bounds = new BoundsD(actual.Min(geometry => geometry.Bounds.Left), actual.Min(geometry => geometry.Bounds.Top),
            actual.Max(geometry => geometry.Bounds.Right), actual.Max(geometry => geometry.Bounds.Bottom));
        Assert.Equal(bounds.Left, part.Bounds.Left, 8);
        Assert.Equal(bounds.Right, part.Bounds.Right, 8);
        Assert.Equal(bounds.Top, part.Bounds.Top, 8);
        Assert.Equal(bounds.Bottom, part.Bounds.Bottom, 8);
        var random = new Random(219);
        for (var sample = 0; sample < 50; sample++)
        {
            var x = 400 + random.Next(150);
            var y = 400 + random.Next(150);
            var query = new BoundsD(x, y, x + random.Next(50), y + random.Next(50));
            var result = part.Contained(query);
            var expected = Enumerable.Range(0, grid.Count).Where(cells.Contains).Where(index =>
            {
                var box = grid.Geometry(index).Bounds;
                return query.Contains(new(box.Left, box.Top)) && query.Contains(new(box.Right, box.Bottom));
            }).ToHashSet();
            Assert.Equal(expected.Count, result.Count);
            Assert.True(expected.SetEquals(Enumerable.Range(0, grid.Count).Where(result.Contains)));

            var point = new PointD(x, y);
            var hits = Enumerable.Range(0, grid.Count).Where(cells.Contains)
                .Where(index => grid.Geometry(index).Contains(point, 2))
                .OrderBy(index => (grid.Geometry(index).Center - point).Length).ThenByDescending(index => index)
                .Select(index => (int?)index).FirstOrDefault();
            Assert.Equal(hits, part.HitTest(point, 2));
        }
    }

    [Fact]
    public void HugeRectangleRetainsExactPartialCountWithoutIndividualValues()
    {
        var grid = new RepeatedRoiGrid(10001, 10001, .25, new(4096, 4096));
        var part = new RepeatedRoiPart(grid, RoiCellSet.All(grid.Count));
        var selection = part.Contained(new(0, 0, 4096, 8192));
        Assert.Equal(5000 * 10001, selection.Count);
        Assert.Equal(10001, selection.Ranges.Length);
        Assert.Single(new RepeatedRoiPart(grid, selection).ConstraintGeometries());
        Assert.Single(part.ConstraintGeometries());
        Assert.Null(part.HitTest(new(0, 0), .001));
    }
}
