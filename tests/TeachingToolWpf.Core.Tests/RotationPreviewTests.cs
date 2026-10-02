using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;
using TeachingToolWpf.Core.Interaction;

namespace TeachingToolWpf.Core.Tests;

public class RotationPreviewTests
{
    [Fact] public void FullTurnNoOpPreservesNavigationAndRedo()
    {
        var editor = Create(); var id = editor.Add(new(new(400, 400), 80, 40, 0));
        editor.Rename(id, "changed"); editor.Undo(); var before = editor.State;
        editor.BeginPreview(); editor.PreviewRotation(new(400, 400), 360);
        var view = before.Viewport.Pan(new(20, -10)); editor.SetViewport(view);
        editor.CommitPreview("회전");
        Assert.Equal(view, editor.State.Viewport); Assert.Same(before.Rois, editor.State.Rois); Assert.True(editor.CanRedo);
    }
    [Fact] public void RigidTransformDefaultIsIdentityAndInverseUsesTheSamePivotAndTranslation()
    {
        var point = new PointD(175, -36); var vector = new PointD(30, 11);
        var bounds = new BoundsD(-20, 15, 65, 90);
        var identity = default(RigidTransform2D);
        Assert.Equal(point, identity.Apply(point)); Assert.Equal(point, identity.InverseApply(point));
        Assert.Equal(vector, identity.ApplyVector(vector));
        Assert.Equal(bounds, identity.TransformBounds(bounds)); Assert.Equal(bounds, identity.InverseTransformBounds(bounds));
        var transform = new RigidTransform2D(new(75, 20), 37, new(-12, 9));
        Near(new PointD(75, 20) + (point - new PointD(75, 20)).Rotate(37) + new PointD(-12, 9), transform.Apply(point));
        Near(point, transform.InverseApply(transform.Apply(point)));
        Near(vector.Rotate(37), transform.ApplyVector(vector));
        BoundsNear(BoundsD.FromPoints(Corners(bounds).Select(transform.Apply)), transform.TransformBounds(bounds));
        BoundsNear(BoundsD.FromPoints(Corners(bounds).Select(transform.InverseApply)), transform.InverseTransformBounds(bounds));
    }

    [Fact] public void PreviewKeepsCommittedSnapshotAndRotatesSelectedParentAndChildExactlyOnce()
    {
        var editor = Create();
        var parent = editor.Add(new(new(300, 300), 200, 160, 15));
        var child = editor.Add(new(new(320, 300), 20, 15, 25));
        var other = editor.Add(new(new(800, 800), 25, 20, 0));
        Assert.Equal(parent, editor.State.Rois[child].ParentId);
        editor.Select([parent, child]); var before = editor.State; var bytes = editor.HistoryBytes;
        editor.BeginPreview(); editor.PreviewRotation(new(300, 300), 10);
        var selection = editor.TransformPreview!.Selection;
        editor.PreviewRotation(new(300, 300), 50);
        var preview = editor.TransformPreview!;
        Assert.Same(before, editor.State); Assert.Same(before.Rois, editor.State.Rois);
        Assert.Same(before.Snapshot, editor.State.Snapshot); Assert.Same(selection, preview.Selection);
        Assert.Equal(bytes, editor.HistoryBytes);
        GeometryNear(before.Rois[parent].Geometry.RotateAround(new(300, 300), 50), preview.Geometry(before.Rois[parent]));
        GeometryNear(before.Rois[child].Geometry.RotateAround(new(300, 300), 50), preview.Geometry(before.Rois[child]));
        Assert.Equal(before.Rois[other].Geometry, preview.Geometry(before.Rois[other]));
        editor.CommitPreview("회전");
        Assert.Null(editor.TransformPreview); Assert.False(editor.IsPreviewing);
        Assert.Equal(parent, editor.State.Rois[child].ParentId);
        GeometryNear(preview.Geometry(before.Rois[child]), editor.State.Rois[child].Geometry);
        Assert.Equal(before.Rois[other], editor.State.Rois[other]);
        var after = editor.State;
        editor.Undo(); Exact(before, editor.State);
        editor.Redo(); Exact(after, editor.State);
    }

    [Fact] public void RotatingOnlyChildReparentsAtCommitAndHistoryRestoresExactValues()
    {
        var editor = Create();
        var first = editor.Add(new(new(400, 500), 80, 80, 0));
        var second = editor.Add(new(new(600, 500), 80, 80, 0));
        var child = editor.Add(new(new(400, 500), 20, 12, 0));
        editor.SetFilters(new(RoiFilterKind.Edge)); editor.Rename(child, "child");
        Assert.Equal(first, editor.State.Rois[child].ParentId);
        var before = editor.State;
        editor.BeginPreview(); editor.PreviewRotation(new(500, 500), 180);
        Assert.Equal(first, editor.State.Rois[child].ParentId);
        Near(new(600, 500), editor.TransformPreview!.Geometry(before.Rois[child]).Center);
        editor.CommitPreview("회전");
        Assert.Equal(second, editor.State.Rois[child].ParentId);
        Assert.Equal(before.Rois[first], editor.State.Rois[first]); Assert.Equal(before.Rois[second], editor.State.Rois[second]);
        var after = editor.State; editor.Undo(); Exact(before, editor.State); editor.Redo(); Exact(after, editor.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RotationStopsAtFirstImageExitEvenWhenRequestedEndpointFits(bool preview)
    {
        var editor = Create(100, 60); var pivot = new PointD(50, 30);
        var id = editor.Add(new(pivot, 80, 10, 0)); var original = editor.State.Rois[id].Geometry;
        Assert.True(FitsImage(original.RotateAround(pivot, 180), editor.State.Image!));
        Assert.False(FitsImage(original.RotateAround(pivot, 90), editor.State.Image!));
        if (preview) { editor.BeginPreview(); editor.PreviewRotation(pivot, 180); }
        else editor.RotateSelection(180, pivot);
        var actual = preview ? editor.TransformPreview!.Geometry(editor.State.Rois[id]) : editor.State.Rois[id].Geometry;
        Assert.InRange(actual.Angle, 35, 55);
        Assert.True(FitsImage(actual, editor.State.Image!));
        Assert.False(FitsImage(original.RotateAround(pivot, actual.Angle + .01), editor.State.Image!));
        if (preview) { editor.CommitPreview("회전"); GeometryNear(actual, editor.State.Rois[id].Geometry); }
    }

    [Fact] public void CancelAndReturnToZeroPreserveRedoAndDoNotChangeCommittedState()
    {
        var editor = Create(); var id = editor.Add(new(new(400, 400), 80, 40, 0));
        editor.Rename(id, "renamed"); editor.Undo(); var before = editor.State; var bytes = editor.HistoryBytes;
        editor.BeginPreview(); editor.PreviewRotation(new(400, 400), 25); editor.CancelPreview();
        Assert.Same(before, editor.State); Assert.Null(editor.TransformPreview); Assert.True(editor.CanRedo);
        editor.BeginPreview(); editor.PreviewRotation(new(400, 400), 25); editor.PreviewRotation(new(400, 400), 0);
        editor.CommitPreview("회전");
        Assert.Same(before.Rois, editor.State.Rois); Assert.Equal(bytes, editor.HistoryBytes); Assert.True(editor.CanRedo);
        editor.Redo(); Assert.Equal("renamed", editor.State.Rois[id].Name);
    }

    [Fact] public void HundredThousandRoiPreviewHasBoundedAllocationAcrossOneHundredWarmSamples()
    {
        var editor = Create(20000, 20000);
        editor.AddRange(Enumerable.Range(0, 100000).Select(i => new RotatedRect(new(1000 + i % 400 * 20, 1000 + i / 400 * 20), 10, 8, 0)));
        editor.Select(editor.State.Rois.Keys); var before = editor.State;
        editor.BeginPreview();
        for (var i = 0; i < 10; i++) editor.PreviewRotation(new(5000, 4000), i * .1);
        var selection = editor.TransformPreview!.Selection;
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++) editor.PreviewRotation(new(5000, 4000), 1 + i * .01);
        var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.InRange(bytes, 0, 200000);
        Assert.Same(before, editor.State); Assert.Same(before.Snapshot, editor.State.Snapshot);
        Assert.Same(selection, editor.TransformPreview!.Selection);
        editor.CancelPreview();
    }

    [Fact] public void TransformSelectionCacheSurvivesNavigationButChangesWithSelectionImageOrGeometry()
    {
        var editor = Create(); var first = editor.Add(new(new(250, 250), 40, 20, 0));
        var second = editor.Add(new(new(650, 650), 50, 30, 0));
        editor.Select([first]); var before = editor.State; var cache = before.TransformSelection;
        editor.SetViewport(before.Viewport.Pan(new(50, -20)).ZoomAt(new(250, 200), 2));
        Assert.Same(cache, editor.State.TransformSelection);
        editor.Select([second]); Assert.NotSame(cache, editor.State.TransformSelection);
        cache = editor.State.TransformSelection;
        editor.SetGeometry(second, editor.State.Rois[second].Geometry.RotateAround(new(650, 650), 30));
        Assert.NotSame(cache, editor.State.TransformSelection);
        var state = editor.State; cache = state.TransformSelection;
        // A record copy must invalidate image-dependent constraints even when ROI/selection references are shared.
        var changedImage = state with { Image = new("larger", "larger", 2000, 2000) };
        Assert.NotSame(cache, changedImage.TransformSelection);
        editor.SetImage(new("new", "new", 1000, 1000), editor.State.Viewport);
        Assert.NotSame(cache, editor.State.TransformSelection);
    }

    [Fact] public async Task PreparedRotationPreservesNewViewportAndCanOnlyBeCommittedOnce()
    {
        var editor = Create(); var id = editor.Add(new(new(400, 400), 80, 40, 10));
        var before = editor.State;
        var pending = editor.PrepareRotationAsync(30, new(400, 400));
        var view = before.Viewport.Pan(new(45, -10)); editor.SetViewport(view);
        var prepared = await pending;
        Assert.Same(before.Rois, editor.State.Rois);
        editor.CommitPrepared(prepared);
        Assert.Equal(view, editor.State.Viewport);
        GeometryNear(before.Rois[id].Geometry.RotateAround(new(400, 400), 30), editor.State.Rois[id].Geometry);
        var after = editor.State;
        Assert.Throws<InvalidOperationException>(() => editor.CommitPrepared(prepared)); Assert.Same(after, editor.State);
        editor.Undo(); Exact(before, editor.State); Assert.Equal(view, editor.State.Viewport);
        editor.Redo(); Exact(after, editor.State);
    }

    [Theory]
    [InlineData("selection")]
    [InlineData("geometry")]
    [InlineData("image")]
    [InlineData("undo-redo")]
    public async Task PreparedRotationRejectsDocumentMutationIncludingHistoryAba(string change)
    {
        var editor = Create(); var id = editor.Add(new(new(400, 400), 80, 40, 0));
        editor.Rename(id, "before"); var pending = editor.PrepareRotationAsync(30, new(400, 400));
        switch (change)
        {
            case "selection": editor.Select([]); break;
            case "geometry": editor.SetGeometry(id, new(new(410, 410), 80, 40, 0)); break;
            case "image": editor.SetImage(new("new", "new", 1000, 1000), editor.State.Viewport); break;
            case "undo-redo": editor.Undo(); editor.Redo(); break;
        }
        var prepared = await pending; var current = editor.State; var bytes = editor.HistoryBytes;
        Assert.Throws<InvalidOperationException>(() => editor.CommitPrepared(prepared));
        Assert.Same(current, editor.State); Assert.Equal(bytes, editor.HistoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangingOrCancellingPreviewRejectsPreviouslyPreparedRotation(bool cancel)
    {
        var editor = Create(); editor.Add(new(new(400, 400), 80, 40, 0));
        var before = editor.State; var bytes = editor.HistoryBytes;
        editor.BeginPreview(); editor.PreviewRotation(new(400, 400), 20);
        var pending = editor.PreparePreviewAsync("회전");
        if (cancel) { editor.CancelPreview(); editor.BeginPreview(); }
        else editor.PreviewRotation(new(400, 400), 30);
        editor.PreviewRotation(new(400, 400), 20);
        var latest = editor.TransformPreview;
        var prepared = await pending;
        Assert.Throws<InvalidOperationException>(() => editor.CommitPrepared(prepared));
        Assert.Same(before, editor.State); Assert.Same(latest, editor.TransformPreview); Assert.Equal(bytes, editor.HistoryBytes);
        editor.CancelPreview();
    }

    [Fact] public async Task CancelledRotationPreparationDoesNotConsumeRedo()
    {
        var editor = Create(); var id = editor.Add(new(new(400, 400), 80, 40, 0));
        editor.Rename(id, "changed"); editor.Undo(); var before = editor.State; var bytes = editor.HistoryBytes;
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => editor.PrepareRotationAsync(30, new(400, 400), cancellation.Token));
        Assert.Same(before, editor.State); Assert.Equal(bytes, editor.HistoryBytes); Assert.True(editor.CanRedo);
    }

    [Fact] public void InputRotationUnwrapsAcrossAtanBranchWithoutJumpingBackOneTurn()
    {
        var editor = new EditorSession(); editor.Add(new(new(400, 400), 80, 40, 0));
        var before = editor.State; var roi = Assert.Single(before.Rois.Values);
        var pivot = before.Viewport.ImageToScreen(roi.Geometry.Center);
        var handle = HandleGeometry.RotationHandle(roi.Geometry, before.Viewport);
        var input = new InteractionController(editor); input.Down(handle);
        foreach (var degrees in new[] { 80d, 160, 240, 269, 271 })
            input.Move(pivot + (handle - pivot).Rotate(degrees));
        Assert.InRange(editor.TransformPreview!.Transform.AngleDegrees, 271 - 1e-8, 271 + 1e-8);
        GeometryNear(roi.Geometry.RotateAround(roi.Geometry.Center, 271), editor.TransformPreview.Geometry(roi));
        Assert.Same(before, editor.State);
        input.Up(pivot + (handle - pivot).Rotate(271));
        GeometryNear(roi.Geometry.RotateAround(roi.Geometry.Center, 271), editor.State.Rois[roi.Id].Geometry);
        editor.Undo(); Exact(before, editor.State);
    }

    private static EditorSession Create(int width = 1000, int height = 1000)
    {
        var editor = new EditorSession(); editor.InitializeImage(new("image", "image", width, height), ViewportTransform.Identity); return editor;
    }
    private static void Near(PointD expected, PointD actual) => Assert.InRange((expected - actual).Length, 0, 1e-7);
    private static bool FitsImage(RotatedRect geometry, ImageReference image) => geometry.Corners.All(point =>
        point.X >= -1e-7 && point.Y >= -1e-7 && point.X <= image.Width + 1e-7 && point.Y <= image.Height + 1e-7);
    private static void GeometryNear(RotatedRect expected, RotatedRect actual)
    {
        Near(expected.Center, actual.Center); Assert.Equal(expected.Width, actual.Width, 8); Assert.Equal(expected.Height, actual.Height, 8);
        var difference = Math.Abs(expected.Angle - actual.Angle) % 360;
        Assert.InRange(Math.Min(difference, 360 - difference), 0, 1e-7);
    }
    private static IEnumerable<PointD> Corners(BoundsD bounds)
        => [new(bounds.Left, bounds.Top), new(bounds.Right, bounds.Top), new(bounds.Right, bounds.Bottom), new(bounds.Left, bounds.Bottom)];
    private static void BoundsNear(BoundsD expected, BoundsD actual)
    {
        Near(new(expected.Left, expected.Top), new(actual.Left, actual.Top));
        Near(new(expected.Right, expected.Bottom), new(actual.Right, actual.Bottom));
    }
    private static void Exact(DocumentState expected, DocumentState actual)
    {
        Assert.Equal(expected.Rois.Count, actual.Rois.Count);
        foreach (var pair in expected.Rois) Assert.Equal(pair.Value, actual.Rois[pair.Key]);
        Assert.True(expected.Selection.SetEquals(actual.Selection)); Assert.Equal(expected.Image, actual.Image);
    }
}
