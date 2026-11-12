using System.Diagnostics;
using System.IO;
using System.Text.Json;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionCanvas.Core.Interaction;
using VisionCanvas.Presentation;
using VisionCanvas.Vulkan.Controls;
using VisionCanvas.SampleShell.Services;
using VisionCanvas.VulkanSample.ViewModels;
using VisionGeometry;
using VisionImaging;
using VisionImaging.Abstractions;

namespace VisionCanvas.Vulkan.Tests;

public sealed class HundredMillionTests
{
    private const int BatchCellCount = 101 * 101;
    private const int CenterCellIndex = 50 * 101 + 50;

    [Fact]
    public Task SampleCommandAndPointerEditOneOfBatchOrdinaryCellsWithHistory() => Sta.RunAsync(async () =>
    {
        using var host = new ViewportHost(new VulkanViewport());
        await host.Ready;
        var editor = new EditorSession();
        using var model = CreateModel(host, editor);
        Assert.False(model.HundredMillionCommand.CanExecute(null));
        await model.SampleCommand.ExecuteAsync();
        var empty = await host.CaptureAsync(model.Scene);
        var started = Stopwatch.GetTimestamp();
        await model.HundredMillionCommand.ExecuteAsync();
        var addMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Assert.Equal(BatchCellCount, editor.State.RoiCount);
        Assert.Equal(BatchCellCount, editor.State.SelectionCount);
        Assert.Equal(BatchCellCount, editor.State.Rois.Count);
        var orderedRois = editor.State.Rois.Values.OrderBy(roi => roi.Order).ToArray();
        Assert.All(orderedRois, roi => Assert.False(roi.IsGroup));
        var full = await host.CaptureAsync(model.Scene);
        var addThroughCaptureMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Assert.False(empty.Pixels.SequenceEqual(full.Pixels));

        var cellRoi = orderedRois[CenterCellIndex];
        var neighborRoi = orderedRois[CenterCellIndex + 1];
        var original = cellRoi.Geometry;
        var neighbor = neighborRoi.Geometry;
        editor.SetViewport(new(4, new PointD(model.Layout.Width / 2, model.Layout.Height / 2) - (original.Center * 4)));
        var view = editor.State.Viewport;
        model.Input.DoubleClick(view.ImageToScreen(original.Center));
        model.Input.Up(view.ImageToScreen(original.Center));
        Assert.Equal(cellRoi.Id, editor.State.SelectionFacts.First?.Id);
        Assert.Equal(1, editor.State.SelectionCount);
        var before = await host.CaptureAsync(model.Scene);
        var center = view.ImageToScreen(original.Center);
        Drag(model.Input, center, center + new PointD(16, 8));
        var moved = editor.State.Rois[cellRoi.Id].Geometry;
        Assert.Equal(original.Center.X + 4.0, moved.Center.X, 6);
        Assert.Equal(original.Center.Y + 2.0, moved.Center.Y, 6);
        var corner = HandleGeometry.ResizeHandles(moved, view)[4];
        Drag(model.Input, corner, corner + new PointD(10, 8));
        var resized = editor.State.Rois[cellRoi.Id].Geometry;
        Assert.True(resized.Width > moved.Width && resized.Height > moved.Height);
        var rotation = HandleGeometry.RotationHandle(resized, view);
        center = view.ImageToScreen(resized.Center);
        Drag(model.Input, rotation, center + (rotation - center).Rotate(20));
        var rotated = editor.State.Rois[cellRoi.Id].Geometry;
        Assert.Equal(20, rotated.Angle, 6);
        Assert.Equal(neighbor, editor.State.Rois[neighborRoi.Id].Geometry);
        var after = await host.CaptureAsync(model.Scene);
        Assert.False(before.Pixels.SequenceEqual(after.Pixels));
        for (var index = 0; index < 3; index++)
        {
            await model.UndoCommand.ExecuteAsync();
        }
        Assert.Equal(original, editor.State.Rois[cellRoi.Id].Geometry);
        ViewportTests.AssertEquivalent(before, await host.CaptureAsync(model.Scene));
        for (var index = 0; index < 3; index++)
        {
            await model.RedoCommand.ExecuteAsync();
        }
        Assert.Equal(rotated, editor.State.Rois[cellRoi.Id].Geometry);
        ViewportTests.AssertEquivalent(after, await host.CaptureAsync(model.Scene));
        await model.DeleteCommand.ExecuteAsync();
        Assert.Equal(BatchCellCount - 1, editor.State.RoiCount);
        Assert.False(editor.State.Rois.ContainsKey(cellRoi.Id));
        await model.UndoCommand.ExecuteAsync();
        Assert.Equal(rotated, editor.State.Rois[cellRoi.Id].Geometry);
        Assert.Empty(host.Errors);
        SaveMeasurement("individual", new
        {
            Count = editor.State.RoiCount,
            editor.HistoryBytes,
            AddMilliseconds = addMilliseconds,
            AddThroughCaptureMilliseconds = addThroughCaptureMilliseconds,
            Note = "8K 소스와 GPU 준비 후 일반 ROI 배치 추가."
        });
    });

    [Fact]
    public Task HalfOfBatchOrdinaryCellsCanBeMovedGroupedCopiedAndRestoredInVulkan() => Sta.RunAsync(async () =>
    {
        using var host = new ViewportHost(new VulkanViewport());
        await host.Ready;
        var editor = new EditorSession();
        using var model = CreateModel(host, editor);
        await model.SampleCommand.ExecuteAsync();
        await model.HundredMillionCommand.ExecuteAsync();
        var initialState = editor.State;
        editor.Select([]);
        var started = Stopwatch.GetTimestamp();
        editor.CommitPrepared(await editor.PrepareRegionSelectionAsync(new(0, 0, 4080, 8192)));
        var selectionMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var selectedCount = editor.State.SelectionCount;
        Assert.Equal(5050, selectedCount);
        var before = await host.CaptureAsync(model.Scene);
        var selectedId = editor.State.Selection.First();
        var unselectedId = editor.State.Rois.Keys.First(id => !editor.State.Selection.Contains(id));
        var originalSelectedCenter = initialState.Rois[selectedId].Geometry.Center;
        var originalUnselectedGeometry = initialState.Rois[unselectedId].Geometry;
        editor.TranslateSelection(new(10, 20));
        var movedState = editor.State;
        Assert.Equal(originalSelectedCenter + new PointD(10, 20), movedState.Rois[selectedId].Geometry.Center);
        Assert.Equal(originalUnselectedGeometry, movedState.Rois[unselectedId].Geometry);

        // 일반 ROI이므로 그룹화 및 그룹 해제가 그대로 가능해야 한다.
        await model.GroupCommand.ExecuteAsync();
        Assert.Equal(BatchCellCount + 1, editor.State.RoiCount);
        await model.UngroupCommand.ExecuteAsync();
        Assert.Equal(BatchCellCount, editor.State.RoiCount);
        await model.UndoCommand.ExecuteAsync();
        await model.UndoCommand.ExecuteAsync();

        started = Stopwatch.GetTimestamp();
        await model.CopyCommand.ExecuteAsync();
        await model.PasteCommand.ExecuteAsync();
        var copyPasteMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Assert.Equal(BatchCellCount + selectedCount, editor.State.RoiCount);
        Assert.Equal(selectedCount, editor.State.SelectionCount);
        var pasted = await host.CaptureAsync(model.Scene);
        Assert.False(before.Pixels.SequenceEqual(pasted.Pixels));
        await model.UndoCommand.ExecuteAsync();
        Assert.Equal(BatchCellCount, editor.State.RoiCount);
        await model.UndoCommand.ExecuteAsync();
        Assert.Equal(originalSelectedCenter, editor.State.Rois[selectedId].Geometry.Center);
        ViewportTests.AssertEquivalent(before, await host.CaptureAsync(model.Scene));
        await model.RedoCommand.ExecuteAsync();
        await model.RedoCommand.ExecuteAsync();
        Assert.Equal(BatchCellCount + selectedCount, editor.State.RoiCount);
        ViewportTests.AssertEquivalent(pasted, await host.CaptureAsync(model.Scene));
        Assert.Empty(host.Errors);
        SaveMeasurement("partial", new
        {
            Count = BatchCellCount,
            editor.State.SelectionCount,
            PastedTotal = editor.State.RoiCount,
            editor.HistoryBytes,
            SelectionMilliseconds = selectionMilliseconds,
            CopyPasteMilliseconds = copyPasteMilliseconds,
            Note = "일반 ROI 배치 부분 선택/그룹화/복붙/Undo 검증."
        });
    });

    private static EditorViewModel CreateModel(ViewportHost host, EditorSession editor)
    {
        var layout = new ViewportLayout(host.Viewport.ActualWidth, host.Viewport.ActualHeight, host.Viewport.Dpi);
        var loader = new SourceImageLoader(new ImageLoader(SampleTests.ArtifactPath("hundred-million-cache")));
        return new(editor, new(editor), layout,
            new(editor, host.Viewport, layout, loader), new Picker());
    }

    private static void Drag(InteractionController input, PointD from, PointD to)
    {
        input.Down(from);
        input.Move(to);
        input.Up(to);
    }

    private static void SaveMeasurement(string name, object value)
        => File.WriteAllText(SampleTests.ArtifactPath($"hundred-million-{name}-{Guid.NewGuid():N}.json"),
            JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));

    private sealed class Picker : IImageFilePicker
    {
        public ImageReference? Pick() => null;
    }
}
