using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using TeachingToolWpf.Controls;
using TeachingToolWpf.Presentation;
using VisionCanvas.Controls;
using VisionCanvas.Core.Editing;
using VisionGeometry;

namespace TeachingToolWpf.Diagnostics;

internal static class ShapeSmokeScenario
{
    public static async Task Run(Window window, EditorViewModel model, Dx12Viewport viewport)
    {
        var editor = model.Session;
        Require(editor.State.Rois.Count == 0, "empty shape fixture");
        var combo = Descendants(window).OfType<ComboBox>().Single(c => AutomationProperties.GetName(c) == "ROI 도형 선택");
        void Choose(RoiShapeKind kind)
        {
            combo.SetCurrentValue(ComboBox.SelectedValueProperty, kind);
            Require(model.SelectedShape == kind, "visible shape selector binding");
        }
        void Pointer(int message, PointD point) => EditingSmokeScenario.Pointer(viewport, message, editor.State.Viewport.ImageToScreen(point));
        void Click(PointD point)
        {
            Pointer(0x201, point);
            Pointer(0x202, point);
        }
        model.Fit.Execute(null);
        Choose(RoiShapeKind.Circle);
        model.AddRoi.Execute(null);
        Require(editor.State.Rois.Count == 1 && editor.State.Rois.Values.Single().Geometry.Shape.Kind == RoiShapeKind.Circle, "circle add button");
        await model.Undo.ExecuteAsync();
        model.DrawTool.Execute(null);
        Pointer(0x201, new(1000, 1500));
        Pointer(0x200, new(3000, 3500));
        Pointer(0x202, new(3000, 3500));
        var circle = editor.State.Selection.Single();
        Require(editor.State.Rois[circle].Geometry.Shape.Kind == RoiShapeKind.Circle && model.IsSelect, "native circle drawing");
        editor.Rename(circle, "원형 검사");
        model.Properties.Width = "1800";
        model.ApplyProperties.Execute(null);
        Require(editor.State.Rois[circle].Geometry.Width == 1800 && editor.State.Rois[circle].Geometry.Height == 1800, "circle numeric diameter");
        Choose(RoiShapeKind.Triangle);
        model.DrawTool.Execute(null);
        Pointer(0x201, new(4500, 1500));
        Pointer(0x200, new(6800, 3500));
        Pointer(0x202, new(6800, 3500));
        var triangle = editor.State.Selection.Single();
        editor.Rename(triangle, "삼각형 검사");
        Require(editor.State.Rois[triangle].Geometry.Shape.Kind == RoiShapeKind.Triangle, "native triangle drawing");
        Choose(RoiShapeKind.Polygon);
        var add = Descendants(window).OfType<Button>().First(b => ReferenceEquals(b.Command, model.AddRoi) && b.IsEnabled);
        add.Command.Execute(null);
        Require(model.IsDraw && editor.State.Rois.Count == 2 && model.Input.PolygonPointCount == 0, "polygon add starts empty point drawing instead of pentagon");
        foreach (var p in new PointD[] { new(1600, 4500), new(5600, 4500), new(5600, 6500), new(3600, 5500), new(1600, 6500) })
        {
            Click(p);
        }

        Require(model.Input.PolygonPointCount == 5 && editor.State.Rois.Count == 2, "native polygon clicks retain draft");
        var complete = Descendants(window).OfType<Button>().Single(b => ReferenceEquals(b.Command, model.CompletePolygon));
        Require(complete.IsEnabled, "visible polygon completion button enabled");
        complete.Command.Execute(null);
        var polygon = editor.State.Selection.Single();
        editor.Rename(polygon, "오목 폴리곤 검사");
        Require(editor.State.Rois[polygon].Geometry.Shape.Vertices.Length == 5 && model.IsSelect, "polygon completed");
        var before = editor.State;
        var addChild = Descendants(window).OfType<Button>().Single(b => ReferenceEquals(b.Command, model.AddChildRoi));
        Require(addChild.IsEnabled, "visible polygon child add enabled");
        addChild.Command.Execute(null);
        foreach (var p in new PointD[] { new(1800, 4700), new(3000, 5900), new(1800, 5900) })
        {
            Click(p);
        }
        Pointer(0x200, new(3000, 4700));
        Require(editor.State.Rois.Count == 3, "crossing preview does not commit");
        Click(new(3000, 4700));
        var crossed = editor.State.Rois[editor.State.Selection.Single()];
        Require(model.IsSelect && crossed.ParentId == polygon && crossed.Geometry.Shape.Vertices.Length == 3,
            "native crossing creates triangular child at intersection");
        Require(crossed.Geometry.Outline.Any(p => (p - new PointD(2400, 5300)).Length < 30), "new intersection vertex matches native input rounding");
        await Task.Delay(300);
        viewport.Capture("artifacts/polygon-crossing.png");
        await Task.Delay(500);
        await model.Undo.ExecuteAsync();
        Require(before.Rois.All(pair => editor.State.Rois[pair.Key] == pair.Value) && editor.State.Rois.Count == 3, "polygon closure is one undo");
        await model.Redo.ExecuteAsync();
        Require(editor.State.Rois[crossed.Id] == crossed, "polygon closure exact redo");
        await model.Undo.ExecuteAsync();
        editor.Select([circle, triangle, polygon]);
        model.RotateRight.Execute(null);
        await model.EditCompletion;
        await model.Undo.ExecuteAsync();
        Require(before.Rois.All(pair => editor.State.Rois[pair.Key] == pair.Value), "mixed shape rotation undo");
        model.Filters.SelectedFilter = RoiFilterKind.Binarize;
        model.Filters.Preview.Execute(null);
        await model.Filters.WhenIdle;
        Require(model.Filters.Result?.Regions.Count == 3 && model.Filters.Result.Regions.All(r => r.Mask is not null), "OpenCV shape masks");
        await Task.Delay(700);
        viewport.Capture("artifacts/shapes-filter.png");
        await Task.Delay(700);
        WindowCapture.Save(window, viewport, "artifacts/shapes-filter.png", "artifacts/shapes-app.png");
        model.Filters.Stop.Execute(null);
        editor.Select([]);
        await Task.Delay(300);
        viewport.Capture("artifacts/shapes-outline.png");
        await Task.Delay(500);
        editor.Select([circle, triangle, polygon]);
        model.Group.Execute(null);
        await model.EditCompletion;
        Require(editor.State.Rois.Count == 4, "mixed shape grouping");
        await model.Delete.ExecuteAsync();
        Require(editor.State.Rois.Count == 0, "mixed subtree cascade");
        await model.Undo.ExecuteAsync();
        Require(editor.State.Rois.Count == 4, "mixed subtree restore");
        await model.Delete.ExecuteAsync();
        Choose(RoiShapeKind.Rectangle);
        model.Filters.SelectedFilter = RoiFilterKind.None;
        File.AppendAllText("artifacts/smoke.log", "Shape selector, polygon add point drawing, native crossing closure/child/undo/redo, circle/triangle input, mixed grouping and masked OpenCV passed" + Environment.NewLine);
    }

    private static void Require(bool value, string description)
    {
        if (!value)
        {
            throw new InvalidOperationException("Shape smoke: " + description);
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }
}
