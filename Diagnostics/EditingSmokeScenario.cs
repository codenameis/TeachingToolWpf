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
    private static void Pointer(Dx12Viewport viewport, int message, PointD image)
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
        model.Undo.Execute(null);
        Require(editor.State.Rois.Count == 0, "one-click creation uses edit history");
        model.DrawTool.Execute(null);
        void Draw(PointD a, PointD b)
        {
            var view = editor.State.Viewport;
            Pointer(viewport, 0x201, view.ImageToScreen(a)); Pointer(viewport, 0x200, view.ImageToScreen(b)); Pointer(viewport, 0x202, view.ImageToScreen(b));
        }
        Draw(new(1000, 1000), new(1800, 1700)); Draw(new(2600, 1200), new(3500, 1800));
        Require(editor.State.Rois.Count == 2, "native HWND pointer input creates two ROIs");
        model.SelectTool.Execute(null); model.SelectAll.Execute(null); model.Group.Execute(null);
        var parent = editor.State.Selection.Single();
        Require(editor.State.Rois.Count == 3 && editor.State.Rois.Values.Count(r => r.ParentId == parent) == 2, "group creates enclosing parent");
        model.Properties.Angle = "37"; model.ApplyProperties.Execute(null);
        Require(editor.State.Rois[parent].Geometry.Angle == 37, "numeric angle");
        model.ResetAngle.Execute(null); Require(editor.State.Rois[parent].Geometry.Angle == 0, "absolute zero");
        model.Undo.Execute(null); Require(editor.State.Rois[parent].Geometry.Angle == 37, "button undo");
        model.Redo.Execute(null); Require(editor.State.Rois[parent].Geometry.Angle == 0, "button redo");
        model.Delete.Execute(null); Require(editor.State.Rois.Count == 0, "cascade delete");
        model.Undo.Execute(null); Require(editor.State.Rois.Count == 3 && editor.State.Selection.Contains(parent), "cascade restore and selection");
        model.Ungroup.Execute(null); Require(editor.State.Rois.Count == 2, "ungroup keeps children");
        model.Undo.Execute(null); Require(editor.State.Rois.Count == 3, "ungroup undo");
        model.Redo.Execute(null); Require(editor.State.Rois.Count == 2, "ungroup redo");
        model.DrawTool.Execute(null);
        Pointer(viewport, 0x201, new(50, 50)); Pointer(viewport, 0x200, new(100, 100)); SendMessage(viewport.Handle, 0x100, 0x1B, 0);
        Require(editor.State.Rois.Count == 2 && !model.Input.IsDragging, "Escape cancels drawing");
        model.SelectTool.Execute(null); model.SelectAll.Execute(null); model.Delete.Execute(null);
        await viewport.RestartAsync();
        Require(editor.State.Rois.Count == 0 && editor.CanUndo, "renderer restart preserves edit history");
    }
}
