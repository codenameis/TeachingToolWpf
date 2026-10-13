using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionCanvas.Core.Interaction;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Core.Tests;

public class RepeatedRoiTests
{
    [Fact]
    public void HitSearchPreservesRectangularToleranceOutsideAnEditedCorner()
    {
        var grid = new RepeatedRoiGrid(7, 7, 20, new(4096, 4096));
        grid = grid.WithGeometry(0, new(new(7000, 7000), grid.CellWidth, grid.CellHeight, 0));

        Assert.Equal(1, grid.HitTest(new(4001, 4001), 50));
    }

    private static readonly ImageReference s_image = new("sample", "sample", 8192, 8192);

    private static RepeatedRoiGrid CreateGrid() => new(10001, 10001, .3, new(4096, 4096));

    [Fact]
    public void HundredMillionCellsHaveExactCenteredGeometryWithoutPerCellAllocation()
    {
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var grid = CreateGrid();
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 100_000);
        Assert.Equal(100_020_001, grid.Count);
        Assert.Empty(grid.Overrides);
        Assert.Equal(new PointD(4096, 4096), grid.Geometry(grid.Count / 2).Center);
        var first = grid.Geometry(0);
        var last = grid.Geometry(grid.Count - 1);
        Assert.Equal(grid.Center, (first.Center + last.Center) / 2);
        Assert.Equal(2596, first.Center.X, 8);
        Assert.Equal(5596, last.Center.X, 8);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9999)]
    [InlineData(50010000)]
    [InlineData(100020000)]
    public void CellsKeepIdentityAfterTranslationAndRotation(int index)
    {
        var grid = CreateGrid();
        var old = grid.Geometry(index);
        var transform = new RigidTransform2D(grid.Center, 137, new(123, -45));
        var moved = grid.Transform(transform);
        var geometry = moved.Geometry(index);
        Assert.True((transform.Apply(old.Center) - geometry.Center).Length < 1e-9);
        Assert.Equal(index, moved.HitTest(geometry.Center, 0));
        Assert.Equal(137, geometry.Angle, 8);
        Assert.Same(grid.Overrides, moved.Overrides);
    }

    [Fact]
    public void ModifiedCellIsRemovedFromOldPositionAndRetainsSizeAndAngleThroughGroupEdit()
    {
        var grid = CreateGrid();
        var index = grid.Count / 2;
        var old = grid.Geometry(index);
        var changed = new RoiGeometry(new(7000, 4096), 4, 3, 27);
        var edited = grid.WithGeometry(index, changed);
        Assert.Null(edited.HitTest(old.Center, 0));
        Assert.Equal(index, edited.HitTest(changed.Center, 0));
        var rotated = edited.Transform(new(grid.Center, 90));
        Assert.Equal(117, rotated.Geometry(index).Angle, 8);
        Assert.Equal(4, rotated.Geometry(index).Width);
        Assert.Equal(3, rotated.Geometry(index).Height);
        Assert.Equal(old, grid.Geometry(index));
        Assert.Single(edited.Overrides);
    }

    [Fact]
    public void WholeAndIndividualEditsUseOneHistoryWithExactCancelAndRedo()
    {
        var session = new RepeatedRoiSession(CreateGrid(), s_image);
        var initial = session.Grid;
        session.Begin();
        session.PreviewTranslation(new(100, 200));
        session.Complete();
        var translated = session.Grid;
        session.Select(session.Grid.Count / 2);
        session.Begin();
        session.PreviewRotation(53);
        session.Complete();
        var individual = session.Grid;
        session.Select(null);
        session.Begin();
        session.PreviewRotation(-110);
        session.Cancel();
        Assert.Same(individual, session.Grid);
        session.Undo();
        Assert.Same(translated, session.Grid);
        session.Undo();
        Assert.Same(initial, session.Grid);
        session.Redo();
        session.Redo();
        Assert.Same(individual, session.Grid);
    }

    [Fact]
    public void NoOpAndSelectionPreserveRedoAndNewEditRemovesRedo()
    {
        var session = new RepeatedRoiSession(CreateGrid(), s_image);
        session.Begin();
        session.PreviewTranslation(new(20, 20));
        session.Complete();
        session.Undo();
        session.Select(0);
        session.Begin();
        session.PreviewTranslation(default);
        session.Complete();
        Assert.True(session.CanRedo);
        session.Begin();
        session.PreviewRotation(10);
        session.Complete();
        Assert.False(session.CanRedo);
    }

    [Fact]
    public void WholeGridStaysInsideImageAndCannotRotateThroughBoundary()
    {
        var session = new RepeatedRoiSession(CreateGrid(), s_image);
        session.Begin();
        session.PreviewTranslation(new(1e9, 0));
        session.Complete();
        Assert.True(RoiImageBounds.Fits(session.Grid.Frame.Bounds, s_image));
        var before = session.Grid;
        session.Begin();
        session.PreviewRotation(360);
        Assert.True(RoiImageBounds.Fits(session.Grid.Frame.Bounds, s_image));
        Assert.Equal(before.Angle, session.Grid.Angle, 6);
        session.Cancel();
    }

    [Fact]
    public void InvalidCellAndOutsideGeometryDoNotChangeStateOrHistory()
    {
        var session = new RepeatedRoiSession(CreateGrid(), s_image);
        var before = session.Grid;
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Select(before.Count));
        session.Select(10);
        Assert.Throws<ArgumentException>(() => session.SetGeometry(new(new(-10, -10), 1, 1, 0)));
        Assert.Same(before, session.Grid);
        Assert.False(session.CanUndo);
        Assert.False(session.IsPreviewing);
    }

    [Fact]
    public void SparseGridRotationConstraintsMatchEveryActualCell()
    {
        var grid = new RepeatedRoiGrid(8, 6, 20, new(4096, 4096));
        grid = grid.WithGeometry(0, new(new(8000, 7800), 20, 30, 47));
        grid = grid.WithGeometry(7, new(new(4200, 8000), 40, 20, 20));
        grid = grid.WithGeometry(15, new(new(4100, 4100), 4, 9, 190));
        var all = new RoiSpatialIndex(Enumerable.Range(0, grid.Count)
            .Select(index => new Roi(Guid.NewGuid(), "", grid.Geometry(index), null, false, index)));
        var expected = all.RotationLimits(grid.Frame.Center, s_image);
        var session = new RepeatedRoiSession(grid, s_image);
        session.Begin();
        session.PreviewRotation(360);
        Assert.Equal(RoiGeometry.NormalizeAngle(expected.MaximumDegrees), session.Grid.Angle, 6);
        session.PreviewRotation(-360);
        Assert.Equal(RoiGeometry.NormalizeAngle(expected.MinimumDegrees), session.Grid.Angle, 6);
        session.Cancel();
        Assert.Same(grid, session.Grid);
    }

    [Fact]
    public void DoubleClickReplacesIndividualSelectionEvenWhenWholeGridWasSelected()
    {
        var session = new RepeatedRoiSession(CreateGrid(), s_image);
        session.SetViewport(new(20, new(-80000, -80000)));
        var input = new RepeatedRoiInput(session);
        var first = session.Grid.Count / 2;
        var second = first + 7;
        input.DoubleClick(session.Viewport.ImageToScreen(session.Grid.Geometry(first).Center));
        Assert.Equal(first, session.SelectedIndex);
        input.DoubleClick(session.Viewport.ImageToScreen(session.Grid.Geometry(second).Center));
        Assert.Equal(second, session.SelectedIndex);
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void InvalidCountOverflowIsRejectedBeforeAllocation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RepeatedRoiGrid(100000, 100000, 1, default));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RepeatedRoiGrid(1, 1, double.NaN, default));
    }

    [Fact]
    public void HitStillFindsNearestCellWhenAllNineAdjacentCellsWereMovedAway()
    {
        var grid = new RepeatedRoiGrid(7, 7, 20, new(4096, 4096));
        for (var row = 2; row <= 4; row++)
        {
            for (var column = 2; column <= 4; column++)
            {
                grid = grid.WithGeometry((row * 7) + column, new(new(7000 + (column * 20), 7000 + (row * 20)), 12, 8, 0));
            }
        }
        Assert.Equal(38, grid.HitTest(grid.Center, 50));
        Assert.Null(grid.HitTest(grid.Center, 0));
    }
}
