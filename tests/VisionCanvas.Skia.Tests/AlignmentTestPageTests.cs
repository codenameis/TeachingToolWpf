using System.IO;
using VisionCanvas.Core.Editing;
using VisionCanvas.Presentation;
using VisionCanvas.SampleShell.Services;
using VisionCanvas.SkiaSample.ViewModels;
using VisionCanvas.SkiaSample.Views;
using VisionImaging;

namespace VisionCanvas.Skia.Tests;

public sealed class AlignmentTestPageTests
{
    [Fact]
    public Task AlignmentTestPageAppliesHierarchicalOffsetsAndRendersWithoutMutatingDocumentOrUndo() => Sta.RunAsync(async () =>
    {
        var window = new EditorWindow { ShowActivated = false, ShowInTaskbar = false };
        var editor = new EditorSession();
        var layout = new ViewportLayout();
        using var model = new EditorViewModel(editor, new(editor), layout,
            new(editor, window.Viewport, layout, new SourceImageLoader(new ImageLoader(SampleTests.ArtifactPath("sample-cache")))), new NullPicker());
        window.DataContext = model;
        try
        {
            window.Show();
            await model.AlignmentTest.LoadPresetAsync();
            Assert.NotNull(model.AlignmentTest.CurrentAlignment);
            Assert.Equal(7, model.AlignmentTest.Scopes.Count);
            Assert.False(model.UndoCommand.CanExecute(null));

            var roisBefore = editor.State.Rois;
            var baselineCapture = SampleTests.ArtifactPath($"alignment-baseline-{Guid.NewGuid():N}.png");
            window.Viewport.Capture(baselineCapture);
            await SampleTests.WaitForCapture(baselineCapture);
            var baselineBytes = await File.ReadAllBytesAsync(baselineCapture);

            model.AlignmentTest.SelectedScope = model.AlignmentTest.Scopes[0];
            model.AlignmentTest.DeltaXText = "32";
            model.AlignmentTest.DeltaYText = "-18";
            model.AlignmentTest.DeltaThetaText = "2.0";
            model.AlignmentTest.ApplyInput();

            model.AlignmentTest.SelectedScope = model.AlignmentTest.Scopes[1];
            model.AlignmentTest.DeltaXText = "-10";
            model.AlignmentTest.DeltaYText = "14";
            model.AlignmentTest.DeltaThetaText = "-1.25";
            model.AlignmentTest.ApplyInput();

            model.AlignmentTest.SelectedScope = model.AlignmentTest.Scopes[3];
            model.AlignmentTest.DeltaXText = "6";
            model.AlignmentTest.DeltaYText = "-5";
            model.AlignmentTest.DeltaThetaText = "0.75";
            model.AlignmentTest.ApplyInput();

            Assert.Same(roisBefore, editor.State.Rois);
            Assert.False(model.UndoCommand.CanExecute(null));
            Assert.Contains("첫 셀[0,0]", model.AlignmentTest.SampleCellsSummary);

            var alignedCapture = SampleTests.ArtifactPath($"alignment-shifted-{Guid.NewGuid():N}.png");
            window.Viewport.Capture(alignedCapture);
            await SampleTests.WaitForCapture(alignedCapture);
            var alignedBytes = await File.ReadAllBytesAsync(alignedCapture);
            Assert.False(baselineBytes.AsSpan().SequenceEqual(alignedBytes));

            model.AlignmentTest.ResetAllScopes();
            Assert.False(model.AlignmentTest.CurrentAlignment!.HasAnyAlignment);
        }
        finally
        {
            model.Dispose();
            window.Close();
        }
    });

    private sealed class NullPicker : IImageFilePicker
    {
        public VisionImaging.Abstractions.ImageReference? Pick() => null;
    }
}
