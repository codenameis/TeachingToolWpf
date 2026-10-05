using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionCanvas.Core.Interaction;
using VisionGeometry;

namespace TeachingToolWpf.Core.Tests;

public class TransformPreparationTests
{
    [Fact]
    public void WholeGroupRefitDoesNotRetainCanceledOperationInSharedSpatialIndex()
    {
        var editor = new EditorSession();
        editor.InitializeImage(new("test", "test", 10000, 10000), ViewportTransform.Identity);
        editor.AddRange(Enumerable.Range(0, 5000).Select(i => new RoiGeometry(
            new(2000 + (i % 100 * 20), 2000 + (i / 100 * 20)), 10, 10, i % 45)));
        editor.Select(editor.State.Rois.Keys);
        editor.GroupSelection();
        var before = editor.State;
        var selection = before.TransformSelection;
        var transform = new RigidTransform2D(selection.Bounds.Center, 15);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => selection.Apply(transform, cancellation.Token));
        Assert.Same(before, editor.State);
        Assert.Equal(before.Rois.Count, before.Snapshot.Spatial.Visible(before.Snapshot.Spatial.Bounds, 0).Count());

        using var completed = new CancellationTokenSource();
        var next = selection.Apply(transform, completed.Token);
        completed.Cancel();
        // 완료된 작업의 token을 취소해도 공유 snapshot의 지연 읽기는 성공해야 한다.
        var actual = next.Snapshot.Spatial.Visible(next.Snapshot.Spatial.Bounds, 0).Select(item => item.Roi!).OrderBy(roi => roi.Id);
        Assert.Equal(next.Rois.Values.OrderBy(roi => roi.Id), actual);
        Assert.Same(before, editor.State);
    }

    [Fact]
    public void ReadyRequiresMovingStationaryAndTheDefaultPivotLimits()
    {
        var editor = Create();
        var state = editor.State;
        Assert.False(state.IsTransformSelectionPrepared);
        var selection = state.TransformSelection;
        Assert.False(selection.IsPrepared);
        _ = selection.Moving;
        Assert.False(state.IsTransformSelectionPrepared);
        _ = selection.Stationary;
        Assert.False(state.IsTransformSelectionPrepared);
        _ = selection.RotationLimits(selection.Bounds.Center + new PointD(10, 0));
        Assert.False(state.IsTransformSelectionPrepared);
        _ = selection.RotationLimits(selection.Bounds.Center);
        Assert.True(selection.IsPrepared);
        Assert.True(state.IsTransformSelectionPrepared);
        _ = selection.RotationLimits(selection.Bounds.Center + new PointD(10, 0));
        Assert.False(state.IsTransformSelectionPrepared);
    }

    [Fact]
    public void NavigationSharesReadinessButGeometrySelectionAndImageInvalidateIt()
    {
        var editor = Create();
        var state = editor.State;
        Warm(state);
        Assert.True((state with
        {
            Viewport = state.Viewport.Pan(new(10, 20))
        }).IsTransformSelectionPrepared);
        var roi = state.Rois.Values.First();
        var geometryChanged = state.WithRois(state.Rois.SetItem(roi.Id, roi with
        {
            Geometry = roi.Geometry.RotateAround(roi.Geometry.Center, 10)
        }), [roi.Id]);
        Assert.False(geometryChanged.IsTransformSelectionPrepared);
        Assert.False((state with
        {
            Selection = []
        }).IsTransformSelectionPrepared);
        Assert.False((state with
        {
            Image = state.Image! with
            {
                Version = "new"
            }
        }).IsTransformSelectionPrepared);
        Assert.False((state with
        {
            Rois = state.Rois
        }).IsTransformSelectionPrepared);
        Assert.True(state.IsTransformSelectionPrepared);
    }

    [Fact]
    public void PreviewEpochDistinguishesCanceledAndRestartedGestureOnSameDocument()
    {
        var editor = Create();
        var source = editor.State;
        editor.BeginPreview();
        var firstEpoch = editor.PreviewEpoch;
        editor.PreviewTranslation(new(10, 20));
        Assert.Equal(firstEpoch, editor.PreviewEpoch);
        editor.CancelPreview();
        var canceledEpoch = editor.PreviewEpoch;
        Assert.True(canceledEpoch > firstEpoch);
        Assert.Same(source.Rois, editor.State.Rois);
        editor.BeginPreview();
        Assert.True(editor.PreviewEpoch > canceledEpoch);
        Assert.Same(source.Rois, editor.State.Rois);
        Assert.True(editor.IsPreviewing);
        Assert.Null(editor.TransformPreview);
        editor.CancelPreview();
    }

    [Fact]
    public void MoveAndRotateCallbacksReceiveGestureValuesWithoutSynchronousPreview()
    {
        var editor = Create();
        var roi = editor.State.Rois.Values.Single();
        PointD? translation = null;
        PointD? rotationPivot = null;
        double? rotationDegrees = null;
        var input = new InteractionController(editor,
            previewTranslation: delta => translation = delta,
            previewRotation: (pivot, degrees) =>
        {
            rotationPivot = pivot;
            rotationDegrees = degrees;
        });
        var center = editor.State.Viewport.ImageToScreen(roi.Geometry.Center);
        input.Down(center);
        input.Move(center + new PointD(17, 9));
        Assert.Equal(new PointD(17, 9), translation);
        Assert.Null(editor.TransformPreview);
        input.Cancel();
        var handle = HandleGeometry.RotationHandle(roi.Geometry, editor.State.Viewport);
        input.Down(handle);
        input.Move(center + (handle - center).Rotate(15));
        Assert.Equal(roi.Geometry.Center, rotationPivot);
        Assert.Equal(15, rotationDegrees!.Value, 8);
        Assert.Null(editor.TransformPreview);
        input.Cancel();
    }

    [Fact]
    public void OmittingCallbacksKeepsSynchronousInputPreview()
    {
        var editor = Create();
        var center = editor.State.Rois.Values.Single().Geometry.Center;
        var input = new InteractionController(editor);
        input.Down(center);
        input.Move(center + new PointD(20, 5));
        Assert.Equal(new PointD(20, 5), editor.TransformPreview!.Transform.Translation);
        input.Up(center + new PointD(20, 5));
        Assert.False(editor.IsPreviewing);
        Assert.Equal(center + new PointD(20, 5), editor.State.Rois.Values.Single().Geometry.Center);
    }

    private static EditorSession Create()
    {
        var editor = new EditorSession();
        editor.InitializeImage(new("test", "test", 2000, 2000), ViewportTransform.Identity);
        editor.Add(new(new(400, 400), 80, 50, 0));
        return editor;
    }

    private static void Warm(DocumentState state)
    {
        var selection = state.TransformSelection;
        _ = selection.Moving;
        _ = selection.Stationary;
        _ = selection.RotationLimits(selection.Bounds.Center);
    }
}
