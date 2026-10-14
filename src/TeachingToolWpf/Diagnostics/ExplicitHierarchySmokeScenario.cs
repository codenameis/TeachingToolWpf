using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TeachingToolWpf.Commands;
using TeachingToolWpf.Controls;
using TeachingToolWpf.ViewModels;
using VisionCanvas.Controls;
using VisionCanvas.Rendering;
using VisionGeometry;

namespace TeachingToolWpf.Diagnostics;

internal static class ExplicitHierarchySmokeScenario
{
    public static async Task Run(Window window, MainWindowViewModel model, Dx12Viewport viewport)
    {
        var editor = model.Session;
        Require(editor.State.Rois.Count == 0, "empty fixture");
        Require(!model.AddChildRoi.CanExecute(null), "child requires one selected parent");
        ExecuteButton(model.AddRoi);
        var parent = editor.State.Selection.Single();
        Require(editor.State.Rois[parent].ParentId is null && !editor.State.Rois[parent].IsGroup,
            "default add button creates one independent leaf");
        await CheckLeafColor(parent, "added-selected");
        editor.Select([]);
        await CheckLeafColor(parent, "added-unselected");
        editor.Select([parent]);
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
        Pointer(0x201, parentGeometry.Center - new PointD(100, 100));
        Pointer(0x200, parentGeometry.Center + new PointD(100, 100));
        Pointer(0x202, parentGeometry.Center + new PointD(100, 100));
        var drawn = editor.State.Selection.Single();
        Require(editor.State.Rois[drawn].ParentId == parent && model.IsSelect, "native child drawing");
        ExecuteButton(model.DrawTool);
        Pointer(0x201, parentGeometry.Center - new PointD(50, 50));
        Pointer(0x200, parentGeometry.Center + new PointD(50, 50));
        await CheckStrokeColor(model.Scene!.Overlay.Draft!, model.StyleSettings.Palette.Child.SelectedColor, "drawing-preview");
        Pointer(0x202, parentGeometry.Center + new PointD(50, 50));
        var independent = editor.State.Selection.Single();
        Require(editor.State.Rois[independent].ParentId is null && !editor.State.Rois[independent].IsGroup,
            "default draw button creates an independent leaf after child drawing");
        await CheckLeafColor(independent, "drawn-selected");
        model.StyleSettings.Child.SelectedColor = "#12D47A";
        model.StyleSettings.Apply.Execute(null);
        await CheckLeafColor(independent, "drawn-custom");
        model.StyleSettings.Reset.Execute(null);
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
        ExecuteButton(model.AddParentRoi);
        var emptyParent = editor.State.Rois[editor.State.Selection.Single()];
        Require(emptyParent.IsGroup && emptyParent.ParentId is null && editor.State.Rois.Count == 1,
            "parent add button creates one empty group");
        Require(RoiStyleRoles.Resolve(editor.State, emptyParent.Id) == RoiRole.Parent, "empty parent has parent role");
        await CheckStrokeColor(emptyParent.Geometry, model.StyleSettings.Palette.Parent.SelectedColor, "parent-added");
        await model.Undo.ExecuteAsync();
        Require(editor.State.Rois.Count == 0, "empty parent creation is one undo step");
        await model.Redo.ExecuteAsync();
        Require(editor.State.Rois[emptyParent.Id] == emptyParent, "empty parent redo retains metadata");
        await model.Delete.ExecuteAsync();
        ExecuteButton(model.DrawParentRoi);
        Pointer(0x201, parentGeometry.Center - new PointD(500, 500));
        Pointer(0x200, parentGeometry.Center + new PointD(500, 500));
        Require(model.Scene!.Overlay.DraftIsGroup, "parent drawing preview has parent role");
        await CheckStrokeColor(model.Scene.Overlay.Draft!, model.StyleSettings.Palette.Parent.SelectedColor, "parent-preview");
        Pointer(0x202, parentGeometry.Center + new PointD(500, 500));
        var drawnParent = editor.State.Rois[editor.State.Selection.Single()];
        Require(drawnParent.IsGroup && editor.State.Rois.Count == 1, "parent drawing commits one group");
        await CheckStrokeColor(drawnParent.Geometry, model.StyleSettings.Palette.Parent.SelectedColor, "parent-drawn");
        ExecuteButton(model.AddChildRoi);
        Require(editor.State.Rois[editor.State.Selection.Single()].ParentId == drawnParent.Id,
            "restored parent accepts child without an extra group");
        await model.SelectAll.ExecuteAsync();
        await model.Delete.ExecuteAsync();
        File.AppendAllText("artifacts/smoke.log", "Restored parent add/draw buttons: empty group, parent preview/stroke pixels, child creation, Undo/Redo passed" + Environment.NewLine);
        File.AppendAllText("artifacts/smoke.log", "Default add/draw buttons: independent leaf, green/custom stroke pixels; explicit child, native input, cascade and Undo/Redo passed" + Environment.NewLine);

        Task CheckLeafColor(Guid id, string name)
        {
            Require(RoiStyleRoles.Resolve(editor.State, id) == RoiRole.Child, "leaf uses child style before attachment");
            var color = model.StyleSettings.Palette.Child.StrokeColor(editor.State.Selection.Contains(id));
            return CheckStrokeColor(editor.State.Rois[id].Geometry, color, name);
        }

        async Task CheckStrokeColor(RoiGeometry geometry, System.Numerics.Vector4 color, string name)
        {
            var path = Path.GetFullPath($"artifacts/leaf-{name}.png");
            viewport.Capture(path);
            for (var attempt = 0; attempt < 100; attempt++)
            {
                await Task.Delay(100);
                if (!File.Exists(path))
                {
                    continue;
                }
                using var stream = File.OpenRead(path);
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                var bitmap = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
                var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                // 선택 핸들을 피한 윗변 위치에서 실제 선 색상을 확인한다.
                var point = geometry.Center + new PointD(geometry.Width / 4, -geometry.Height / 2);
                var screen = editor.State.Viewport.ImageToScreen(point) * viewport.Dpi;
                Require(RoiHierarchyRulesSmoke.HasColor(pixels, bitmap.PixelWidth, bitmap.PixelHeight, screen, color), name + " leaf stroke pixels");
                return;
            }
            throw new TimeoutException("Leaf stroke capture timed out.");
        }

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
