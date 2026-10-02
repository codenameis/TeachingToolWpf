using System.Runtime.InteropServices;
using TeachingToolWpf.Controls;
using TeachingToolWpf.Core.Geometry;
using TeachingToolWpf.Presentation;

namespace TeachingToolWpf.Diagnostics;

internal static class EditingSmokeScenario
{
    [DllImport("user32.dll")] private static extern nint SendMessage(nint hwnd, int message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref NativeMethods.NativePoint point);
    private static void Require(bool value, string description) { if (!value) throw new InvalidOperationException("Smoke assertion: " + description); }
    internal static void Pointer(Dx12Viewport viewport, int message, PointD image)
    {
        var point = viewport.Input is not null ? image : throw new InvalidOperationException("Missing input controller");
        var packed = (uint)(ushort)Math.Round(point.X * viewport.Dpi) | ((uint)(ushort)Math.Round(point.Y * viewport.Dpi) << 16);
        SendMessage(viewport.Handle, message, message == 0x202 ? 0 : 1, (nint)packed);
    }
    public static async Task Run(EditorViewModel model, Dx12Viewport viewport)
    {
        var editor = model.Session;
        var hitPoint = new NativeMethods.NativePoint { X = 40, Y = 40 };
        Require(ClientToScreen(viewport.Handle, ref hitPoint), "native viewport screen coordinates");
        var screenPosition = (uint)(ushort)hitPoint.X | ((uint)(ushort)hitPoint.Y << 16);
        var hit = SendMessage(viewport.Handle, 0x84, 0, (nint)screenPosition);
        Require(hit == 1, $"viewport must receive physical pointer routing (WM_NCHITTEST = {hit}; expected HTCLIENT = 1)");
        model.AddRoi.Execute(null);
        Require(editor.State.Rois.Count == 1 && editor.State.Selection.Count == 1, "one-click ROI creation");
        await model.Undo.ExecuteAsync();
        Require(editor.State.Rois.Count == 0, "one-click creation uses edit history");
        void Draw(PointD a, PointD b)
        {
            model.DrawTool.Execute(null);
            var view = editor.State.Viewport;
            Pointer(viewport, 0x201, view.ImageToScreen(a)); Pointer(viewport, 0x200, view.ImageToScreen(b)); Pointer(viewport, 0x202, view.ImageToScreen(b));
            Require(model.IsSelect && !model.Input.IsDragging, "completed drawing returns to selection mode");
        }
        Draw(new(1000, 1000), new(1800, 1700)); Draw(new(2600, 1200), new(3500, 1800));
        Require(editor.State.Rois.Count == 2, "native HWND pointer input creates two ROIs");
        foreach (var roi in editor.State.Rois.Values.OrderBy(r => r.Order).ToArray())
        {
            var center = editor.State.Viewport.ImageToScreen(roi.Geometry.Center);
            Pointer(viewport, 0x201, center); Pointer(viewport, 0x202, center);
            Require(editor.State.Selection.SetEquals([roi.Id]), "click switches to another ROI immediately after drawing");
            Require(editor.State.Rois[roi.Id].Geometry == roi.Geometry, "selection click preserves ROI geometry");
        }
        await model.SelectAll.ExecuteAsync();
        Require(model.HandleKey(System.Windows.Input.Key.C, true, false), "Ctrl+C handles ROI copy");
        Require(model.Paste.CanExecute(null), "copied selection enables paste button");
        Require(model.HandleKey(System.Windows.Input.Key.V, true, false), "Ctrl+V handles ROI paste");
        Require(editor.State.Rois.Count == 4 && editor.State.Selection.Count == 2, "paste selects new ROI ids");
        SendMessage(viewport.Handle, 0x100, 0x2E, 0);
        await model.EditCompletion;
        Require(editor.State.Rois.Count == 2, "native Delete key removes pasted selection");
        await model.Undo.ExecuteAsync(); Require(editor.State.Rois.Count == 4, "Undo restores deleted copies");
        await model.Undo.ExecuteAsync(); Require(editor.State.Rois.Count == 2, "paste is one undoable edit");
        model.SelectTool.Execute(null); await model.SelectAll.ExecuteAsync(); model.Group.Execute(null);
        var parent = editor.State.Selection.Single();
        Require(editor.State.Rois.Count == 3 && editor.State.Rois.Values.Count(r => r.ParentId == parent) == 2, "group creates enclosing parent");
        model.Properties.Angle = "37"; model.ApplyProperties.Execute(null);
        Require(editor.State.Rois[parent].Geometry.Angle == 37, "numeric angle");
        model.ResetAngle.Execute(null); Require(editor.State.Rois[parent].Geometry.Angle == 0, "absolute zero");
        await model.Undo.ExecuteAsync(); Require(editor.State.Rois[parent].Geometry.Angle == 37, "button undo");
        await model.Redo.ExecuteAsync(); Require(editor.State.Rois[parent].Geometry.Angle == 0, "button redo");
        var beforeRotationButton = editor.State;
        model.RotateRight.Execute(null); await model.EditCompletion;
        Require(editor.State.Rois[parent].Geometry.Angle == 15, "rotation toolbar uses rigid preview and commit");
        await model.Undo.ExecuteAsync();
        Require(beforeRotationButton.Rois.All(pair => editor.State.Rois[pair.Key] == pair.Value), "one undo restores toolbar rotation and descendants");
        await model.Delete.ExecuteAsync(); Require(editor.State.Rois.Count == 0, "cascade delete");
        await model.Undo.ExecuteAsync(); Require(editor.State.Rois.Count == 3 && editor.State.Selection.Contains(parent), "cascade restore and selection");
        model.Ungroup.Execute(null); Require(editor.State.Rois.Count == 2, "ungroup keeps children");
        await model.Undo.ExecuteAsync(); Require(editor.State.Rois.Count == 3, "ungroup undo");
        await model.Redo.ExecuteAsync(); Require(editor.State.Rois.Count == 2, "ungroup redo");
        model.DrawTool.Execute(null);
        Pointer(viewport, 0x201, new(50, 50)); Pointer(viewport, 0x200, new(100, 100)); SendMessage(viewport.Handle, 0x100, 0x1B, 0);
        Require(editor.State.Rois.Count == 2 && !model.Input.IsDragging, "Escape cancels drawing");
        model.SelectTool.Execute(null); await model.SelectAll.ExecuteAsync(); await model.Delete.ExecuteAsync();
        await viewport.RestartAsync();
        Require(editor.State.Rois.Count == 0 && editor.CanUndo, "renderer restart preserves edit history");
    }
}
