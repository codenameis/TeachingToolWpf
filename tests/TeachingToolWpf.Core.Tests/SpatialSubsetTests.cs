using System.Collections.Immutable;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Core.Tests;

public class SpatialSubsetTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void SelectedAndTransformIndexesMatchRebuildForEmptyWholeAndPartialSelection(int selectionMode)
    {
        var originals = Grid();
        var selected = originals.Where(roi => selectionMode == 1 || (selectionMode == 2 && roi.Order % 3 != 1))
            .Select(roi => roi.Id).ToImmutableHashSet();
        var state = State(originals, selected);
        var spatial = state.Snapshot.Spatial;
        var included = originals.Where(roi => selected.Contains(roi.Id)).ToArray();
        var excluded = originals.Where(roi => !selected.Contains(roi.Id)).ToArray();

        AssertEquivalent(state.SelectedSpatial, included, originals);
        AssertEquivalent(state.TransformSelection.Moving, included, originals);
        AssertEquivalent(state.TransformSelection.Stationary, excluded, originals);
        AssertEquivalent(spatial, originals, originals);
        if (selectionMode == 1)
        {
            Assert.Same(spatial, state.SelectedSpatial);
            Assert.Same(spatial, state.TransformSelection.Moving);
        }
    }

    [Fact]
    public void MovingSubsetIncludesSelectedParentsOutsideDescendantsExactlyOnce()
    {
        var originals = Grid();
        var hierarchy = originals.Select((roi, index) => index % 3 == 0 ? roi : roi with
        {
            ParentId = originals[index - 1].Id
        }).ToArray();
        var selected = hierarchy.Where(roi => roi.Order % 3 == 0).Take(80).Select(roi => roi.Id)
            .Append(hierarchy[1].Id).ToImmutableHashSet();
        var state = State(hierarchy, selected);
        _ = state.Snapshot.Spatial;
        var moving = hierarchy.Take(240).ToArray();
        var stationary = hierarchy.Skip(240).ToArray();

        Assert.False(hierarchy[0].Geometry.Contains(hierarchy[1].Geometry));
        Assert.Equal(240, state.TransformSelection.Affected.Count);
        Assert.True(state.TransformSelection.Affected.SetEquals(moving.Select(roi => roi.Id)));
        AssertEquivalent(state.SelectedSpatial, hierarchy.Where(roi => selected.Contains(roi.Id)).ToArray(), hierarchy);
        AssertEquivalent(state.TransformSelection.Moving, moving, hierarchy);
        AssertEquivalent(state.TransformSelection.Stationary, stationary, hierarchy);

        var translated = state.TransformSelection.Apply(new(default, 0, new(3, 4)));
        foreach (var roi in moving)
        {
            Assert.Equal(roi.Geometry.Center + new PointD(3, 4), translated.Rois[roi.Id].Geometry.Center);
        }
        Assert.All(stationary, roi => Assert.Same(roi, translated.Rois[roi.Id]));
        AssertEquivalent(state.TransformSelection.Moving, moving, hierarchy);
    }

    [Fact]
    public void SubsetsReturnLatestMetadataAndPreserveOriginalSnapshotValues()
    {
        var originals = Grid();
        var selection = originals.Where(roi => roi.Order % 3 != 1).Select(roi => roi.Id).ToImmutableHashSet();
        var before = State(originals, selection);
        var originalIndex = before.Snapshot.Spatial;
        var renamed = originals.ToImmutableDictionary(roi => roi.Id, roi => roi with
        {
            Name = $"changed {roi.Order}",
            Filter = new(RoiFilterKind.Edge),
            IsGroup = true
        });
        var after = before.WithRois(renamed, renamed.Keys);
        _ = after.Snapshot.Spatial;

        AssertEquivalent(after.SelectedSpatial, renamed.Values.Where(roi => selection.Contains(roi.Id)).ToArray(), originals);
        AssertEquivalent(after.TransformSelection.Stationary, renamed.Values.Where(roi => !selection.Contains(roi.Id)).ToArray(), originals);
        foreach (var index in new[] { after.SelectedSpatial, after.TransformSelection.Moving, after.TransformSelection.Stationary })
        {
            Assert.All(index.Visible(index.Bounds, 0), item => Assert.Same(renamed[item.Roi!.Id], item.Roi));
            Assert.All(index.LabelCandidates(index.Bounds, 0, 1, 1), item => Assert.Same(renamed[item.Roi!.Id], item.Roi));
        }
        AssertEquivalent(originalIndex, originals, originals);
        Assert.All(originalIndex.Visible(originalIndex.Bounds, 0), item => Assert.Same(before.Rois[item.Roi!.Id], item.Roi));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SubsetPathsSupportReplaceRemoveAndInsertionWithoutChangingTheirSource(bool stationary)
    {
        var originals = Grid();
        var selected = originals.Where(roi => roi.Order % 3 != 1).Select(roi => roi.Id).ToImmutableHashSet();
        var before = State(originals, selected);
        var sourceIndex = before.Snapshot.Spatial;
        var subset = stationary ? before.TransformSelection.Stationary : before.SelectedSpatial;
        var members = originals.Where(roi => selected.Contains(roi.Id) != stationary).ToArray();
        var expected = members.ToImmutableDictionary(roi => roi.Id);
        var moved = members[0] with
        {
            Geometry = members[0].Geometry.Translate(new(12, 9))
        };
        var removed = members[^1];
        var restored = originals.First(roi => !expected.ContainsKey(roi.Id)) with
        {
            Name = "restored outside subset"
        };
        var added = new Roi(Guid.NewGuid(), "added", new(new(950, 630), 12, 8, 25), null, false, 1000);

        var edited = subset.WithChanges([(moved.Id, moved), (removed.Id, null), (restored.Id, restored), (added.Id, added)]);
        expected = expected.SetItem(moved.Id, moved).Remove(removed.Id).Add(restored.Id, restored).Add(added.Id, added);
        AssertEquivalent(edited, expected.Values.ToArray(), originals.Append(added).ToArray());
        AssertEquivalent(subset, members, originals);
        AssertEquivalent(sourceIndex, originals, originals);

        var empty = edited.WithChanges(expected.Keys.Select(id => (id, (Roi?)null)));
        AssertEquivalent(empty, [], originals);
        var reinserted = empty.WithChanges([(removed.Id, removed)]);
        AssertEquivalent(reinserted, [removed], originals);
        AssertEquivalent(edited, expected.Values.ToArray(), originals);
    }

    [Fact]
    public void LargeSubsetRetainsUnchangedSpatialBlockIdentity()
    {
        var originals = Grid();
        var before = State(originals, []);
        var spatial = before.Snapshot.Spatial;
        var originalBlocks = spatial.Blocks(spatial.Bounds, 8).ToArray();
        var kept = originalBlocks.Where((_, index) => index % 4 != 0).ToArray();
        var selected = kept.SelectMany(block => block.Detail(0)).Select(item => item.Roi!.Id).ToImmutableHashSet();
        var after = before with
        {
            Selection = selected
        };
        var subset = after.SelectedSpatial;
        var blocks = subset.Blocks(subset.Bounds, 8).ToArray();

        Assert.True(selected.Count > 64 && selected.Count > originals.Length / 8);
        Assert.Contains(blocks, block => kept.Any(original => ReferenceEquals(original.Identity, block.Identity)));
        Assert.Equal(selected.Count, blocks.Sum(block => block.Count));
        AssertEquivalent(subset, originals.Where(roi => selected.Contains(roi.Id)).ToArray(), originals);
        AssertEquivalent(spatial, originals, originals);
    }

    [Fact]
    public void OverlappingSubsetKeepsAreaThenOrderHitPriority()
    {
        var originals = Enumerable.Range(0, 384).Select(index => new Roi(Guid.NewGuid(), $"ROI {index}",
            new(new(400, 300), index % 3 == 0 ? 12 : 24, 12, index % 90), null, false, index)).ToArray();
        var selected = originals.Where(roi => roi.Order % 2 == 0).Select(roi => roi.Id).ToImmutableHashSet();
        var state = State(originals, selected);
        _ = state.Snapshot.Spatial;

        AssertEquivalent(state.SelectedSpatial, originals.Where(roi => selected.Contains(roi.Id)).ToArray(), originals);
        AssertEquivalent(state.TransformSelection.Stationary, originals.Where(roi => !selected.Contains(roi.Id)).ToArray(), originals);
    }

    private static void AssertEquivalent(RoiSpatialIndex actual, Roi[] expected, Roi[] probes)
    {
        var rebuilt = new RoiSpatialIndex(expected);
        Assert.Equal(rebuilt.Bounds, actual.Bounds);
        Assert.Equal(expected.OrderBy(roi => roi.Id), actual.Intersecting(new(0, 0, 1000, 700)).OrderBy(roi => roi.Id));
        Assert.Equal(expected.OrderBy(roi => roi.Id), actual.Visible(actual.Bounds, 0).Select(item => item.Roi!).OrderBy(roi => roi.Id));
        foreach (var roi in probes)
        {
            foreach (var tolerance in new[] { 0d, 2, 40 })
            {
                Assert.Equal(rebuilt.HitTest(roi.Geometry.Center, tolerance), actual.HitTest(roi.Geometry.Center, tolerance));
            }
            Assert.Equal(rebuilt.Intersecting(roi.Geometry.Bounds).OrderBy(value => value.Id),
                actual.Intersecting(roi.Geometry.Bounds).OrderBy(value => value.Id));
        }
        foreach (var pivot in new[] { new PointD(500, 350), new PointD(30, 30) })
        {
            foreach (var angle in new[] { -73d, 0, 37, 180 })
            {
                var expectedBounds = rebuilt.RotatedBounds(pivot, angle);
                var actualBounds = actual.RotatedBounds(pivot, angle);
                Assert.Equal(expectedBounds.Left, actualBounds.Left, 8);
                Assert.Equal(expectedBounds.Top, actualBounds.Top, 8);
                Assert.Equal(expectedBounds.Right, actualBounds.Right, 8);
                Assert.Equal(expectedBounds.Bottom, actualBounds.Bottom, 8);
            }
            var image = new ImageReference("fixture", "fixture", 1000, 700);
            var expectedRange = rebuilt.RotationLimits(pivot, image);
            var actualRange = actual.RotationLimits(pivot, image);
            Assert.Equal(expectedRange.MinimumDegrees, actualRange.MinimumDegrees, 8);
            Assert.Equal(expectedRange.MaximumDegrees, actualRange.MaximumDegrees, 8);
        }
    }

    private static DocumentState State(Roi[] rois, ImmutableHashSet<Guid> selection)
        => new(rois.ToImmutableDictionary(roi => roi.Id), selection, new("fixture", "fixture", 1000, 700), ViewportTransform.Identity);

    private static Roi[] Grid()
        => Enumerable.Range(0, 384).Select(index =>
        {
            var center = new PointD(35 + (index % 24 * 36), 35 + (index / 24 * 36));
            var shape = (index % 4) switch
            {
                0 => new RoiGeometry(center, 22, 12, 0),
                1 => RoiGeometry.Create(RoiShapeKind.Circle, center, 16, 16),
                2 => RoiGeometry.Create(RoiShapeKind.Triangle, center, 22, 14),
                _ => RoiGeometry.Polygon([center + new PointD(-12, -8), center + new PointD(12, -8),
                    center + new PointD(1, 0), center + new PointD(12, 8), center + new PointD(-12, 8)])
            };
            return new Roi(Guid.NewGuid(), $"ROI {index}", shape.RotateAround(center, index * 13 % 360), null, false, index);
        }).ToArray();
}
