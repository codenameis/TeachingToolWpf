using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using VisionCanvas.Core.Editing;
using VisionCanvas.Presentation;
using VisionCanvas.Skia.Controls;
using VisionCanvas.SampleShell.Services;
using VisionCanvas.SkiaSample.ViewModels;
using VisionCanvas.SkiaSample.Views;
using VisionGeometry;
using VisionImaging;
using VisionImaging.Abstractions;

namespace VisionCanvas.Skia.Tests;

public sealed class RepeatedSampleTests
{
    private const int BatchCellCount = 101 * 101;
    private const int CenterCellIndex = 50 * 101 + 50;

    [Theory]
    [InlineData(555, false)]
    [InlineData(556, true)]
    public Task SmallImageBoundaryPreservesHistoryOnFailure(int side, bool accepted) => Sta.RunAsync(async () =>
    {
        using var viewport = new SkiaViewport();
        var editor = new EditorSession();
        var layout = new ViewportLayout(600, 400, 1);
        using var model = new EditorViewModel(editor, new(editor), layout,
            new(editor, viewport, layout, new ImageLoader(SampleTests.ArtifactPath("sample-cache"))), new Picker());
        editor.InitializeImage(new("small", "small", side, side), ViewportTransform.Identity);
        var before = editor.State;
        await model.AddRepeatedCommand.ExecuteAsync();
        if (accepted)
        {
            Assert.Equal(BatchCellCount, editor.State.RoiCount);
            Assert.True(editor.CanUndo);
        }
        else
        {
            Assert.Same(before, editor.State);
            Assert.False(editor.CanUndo);
            Assert.Contains("556px", model.Status);
        }
    });

    [Fact]
    public Task BatchRoiButtonPreservesIndividualEditingGroupingAndHistory() => Sta.RunAsync(async () =>
    {
        var window = new EditorWindow { ShowActivated = false, ShowInTaskbar = false };
        var editor = new EditorSession();
        var layout = new ViewportLayout();
        using var model = new EditorViewModel(editor, new(editor), layout,
            new(editor, window.Viewport, layout, new ImageLoader(SampleTests.ArtifactPath("sample-cache"))), new Picker());
        window.DataContext = model;
        try
        {
            Assert.False(model.AddRepeatedCommand.CanExecute(null));
            window.Show();
            await model.SampleCommand.ExecuteAsync();
            await model.AddCommand.ExecuteAsync();
            var ordinary = Assert.Single(editor.State.Rois).Value;
            var started = Stopwatch.GetTimestamp();
            await model.AddRepeatedCommand.ExecuteAsync();
            var addMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Assert.Equal(BatchCellCount + 1, editor.State.RoiCount);
            Assert.Equal(BatchCellCount, editor.State.SelectionCount);
            Assert.Same(ordinary, editor.State.Rois[ordinary.Id]);

            var addedRois = editor.State.Rois.Values
                .Where(roi => roi.Id != ordinary.Id)
                .OrderBy(roi => roi.Order)
                .ToArray();
            Assert.Equal(BatchCellCount, addedRois.Length);
            Assert.All(addedRois, roi => Assert.False(roi.IsGroup));

            await model.UndoCommand.ExecuteAsync();
            Assert.Single(editor.State.Rois);
            await model.RedoCommand.ExecuteAsync();
            Assert.Equal(BatchCellCount + 1, editor.State.Rois.Count);

            // 중앙 셀을 확대한 뒤 실제 입력 경로로 한 개만 이동한다.
            var centerRoi = addedRois[CenterCellIndex];
            var neighborRoi = addedRois[CenterCellIndex + 1];
            var original = centerRoi.Geometry;
            var neighborOriginal = neighborRoi.Geometry;
            editor.Select([]);
            editor.SetViewport(new(16, new((layout.Width / 2) - (original.Center.X * 16), (layout.Height / 2) - (original.Center.Y * 16))));
            var start = editor.State.Viewport.ImageToScreen(original.Center);
            model.Input.Down(start);
            model.Input.Move(start + new PointD(16, 8));
            model.Input.Up(start + new PointD(16, 8));
            Assert.Equal(1, editor.State.SelectionCount);
            Assert.Contains(centerRoi.Id, editor.State.Selection);
            var moved = editor.State.Rois[centerRoi.Id].Geometry;
            Assert.Equal(original.Center.X + 1.0, moved.Center.X, 8);
            Assert.Equal(original.Center.Y + 0.5, moved.Center.Y, 8);
            Assert.Equal(neighborOriginal, editor.State.Rois[neighborRoi.Id].Geometry);
            await model.UndoCommand.ExecuteAsync();
            Assert.Equal(original, editor.State.Rois[centerRoi.Id].Geometry);
            await model.RedoCommand.ExecuteAsync();
            Assert.Equal(moved, editor.State.Rois[centerRoi.Id].Geometry);

            editor.RotateSelection(17, moved.Center);
            Assert.Equal(17, editor.State.Rois[centerRoi.Id].Geometry.Angle, 8);
            await model.UndoCommand.ExecuteAsync();
            Assert.Equal(moved, editor.State.Rois[centerRoi.Id].Geometry);
            await model.DeleteCommand.ExecuteAsync();
            Assert.Equal(BatchCellCount, editor.State.RoiCount);
            await model.UndoCommand.ExecuteAsync();
            Assert.Equal(moved, editor.State.Rois[centerRoi.Id].Geometry);

            // 일반 ROI이므로 그룹화/해제가 가능해야 한다.
            await model.SelectAllCommand.ExecuteAsync();
            await model.GroupCommand.ExecuteAsync();
            Assert.Equal(BatchCellCount + 2, editor.State.RoiCount);
            await model.UngroupCommand.ExecuteAsync();
            Assert.Equal(BatchCellCount + 1, editor.State.RoiCount);

            await model.SelectAllCommand.ExecuteAsync();
            await model.CopyCommand.ExecuteAsync();
            await model.PasteCommand.ExecuteAsync();
            Assert.Equal((BatchCellCount + 1) * 2, editor.State.RoiCount);
            await model.UndoCommand.ExecuteAsync();
            Assert.Equal(BatchCellCount + 1, editor.State.RoiCount);
            await model.RedoCommand.ExecuteAsync();
            Assert.Equal((BatchCellCount + 1) * 2, editor.State.RoiCount);
            await model.UndoCommand.ExecuteAsync();
            await model.AddRepeatedCommand.ExecuteAsync();
            Assert.Equal((BatchCellCount * 2) + 1, editor.State.RoiCount);
            Assert.False(model.RedoCommand.CanExecute(null));
            await model.FitCommand.ExecuteAsync();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
            Assert.Same(model.Scene, window.Viewport.Scene);
            var capture = SampleTests.ArtifactPath($"repeated-batch-{Guid.NewGuid():N}.png");
            window.Viewport.Capture(capture);
            await SampleTests.WaitForCapture(capture);
            File.WriteAllText(SampleTests.ArtifactPath("repeated-command.json"), JsonSerializer.Serialize(new
            {
                AddMilliseconds = addMilliseconds,
                Count = editor.State.RoiCount
            }));
        }
        finally
        {
            model.Dispose();
            window.Close();
        }
    });

    private sealed class Picker : IImageFilePicker
    {
        public ImageReference? Pick() => null;
    }
}
