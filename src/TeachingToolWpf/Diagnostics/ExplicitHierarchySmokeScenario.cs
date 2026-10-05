using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TeachingToolWpf.Controls;
using TeachingToolWpf.Presentation;
using VisionCanvas.Controls;
using VisionGeometry;

namespace TeachingToolWpf.Diagnostics;

internal static class ExplicitHierarchySmokeScenario
{
    public static async Task Run(Window window, EditorViewModel model, Dx12Viewport viewport)
    {
        var editor = model.Session;
        Require(editor.State.Rois.Count == 0, "empty fixture");
        Require(!model.AddChildRoi.CanExecute(null), "child requires one selected parent");
        ExecuteButton(model.AddRoi);
        var parent = editor.State.Selection.Single();
        var parentGeometry = editor.State.Rois[parent].Geometry;
        ExecuteButton(model.AddChildRoi);
        var child = editor.State.Selection.Single();
        Require(editor.State.Rois[child].ParentId == parent, "child button connects selected parent");
        await model.Undo.ExecuteAsync();
        Require(!editor.State.Rois.ContainsKey(child), "child creation is one history entry");
        await model.Redo.ExecuteAsync();
        Require(editor.State.Rois[child].ParentId == parent, "redo restores child id and relation");
        editor.SetGeometry(child, new(new(1000, 1000), 200, 200, 0));
        Require(editor.State.Rois[child].ParentId == parent, "outside child retains its parent");
        editor.Select([parent]);
        ExecuteButton(model.DrawChildRoi);
        Pointer(0x201, new(1500, 1000));
        Pointer(0x200, new(1900, 1400));
        Pointer(0x202, new(1900, 1400));
        var drawn = editor.State.Selection.Single();
        Require(editor.State.Rois[drawn].ParentId == parent && model.IsSelect, "native child drawing");
        ExecuteButton(model.DrawTool);
        Pointer(0x201, parentGeometry.Center - new PointD(50, 50));
        Pointer(0x200, parentGeometry.Center + new PointD(50, 50));
        Pointer(0x202, parentGeometry.Center + new PointD(50, 50));
        var independent = editor.State.Selection.Single();
        Require(editor.State.Rois[independent].ParentId is null, "overlapping parent drawing stays independent");
        editor.Select([parent, independent]);
        Require(!model.AddChildRoi.CanExecute(null), "multiple selection disables ambiguous parent");
        editor.Select([parent]);
        await model.Delete.ExecuteAsync();
        Require(editor.State.Rois.Count == 1 && editor.State.Rois.ContainsKey(independent), "cascade preserves unrelated overlap");
        await model.Undo.ExecuteAsync();
        Require(editor.State.Rois.Count == 4, "cascade undo restores full subtree");
        await Task.Delay(400);
        viewport.Capture("artifacts/explicit-hierarchy.png");
        await Task.Delay(500);
        WindowCapture.Save(window, viewport, "artifacts/explicit-hierarchy.png", "artifacts/explicit-hierarchy-app.png");
        await model.SelectAll.ExecuteAsync();
        await model.Delete.ExecuteAsync();
        File.AppendAllText("artifacts/smoke.log", "Explicit parent/child buttons, native drawing, overlap independence, cascade, Undo/Redo passed" + Environment.NewLine);

        void ExecuteButton(RelayCommand command)
        {
            var button = Descendants(window).OfType<Button>().First(item => ReferenceEquals(item.Command, command));
            Require(button.IsEnabled, "visible button enabled");
            button.Command.Execute(null);
        }

        void Pointer(int message, PointD point)
            => EditingSmokeScenario.Pointer(viewport, message, editor.State.Viewport.ImageToScreen(point));
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Explicit hierarchy smoke: " + message);
        }
    }
}
