using System.IO;
using TeachingToolWpf.Controls;
using TeachingToolWpf.Core.Geometry;
using TeachingToolWpf.Presentation;

namespace TeachingToolWpf.Diagnostics;

internal static class MinimapSmokeScenario
{
    public static async Task Run(EditorViewModel model, Dx12Viewport viewport, string name)
    {
        var before = model.Session.State; var historyBytes = model.Session.HistoryBytes;
        var center = new PointD(viewport.ViewWidth / 2, viewport.ViewHeight / 2);
        model.Session.SetViewport(before.Viewport.ZoomAt(center, 6));
        var map = model.Input.Minimap ?? throw new InvalidOperationException("Missing minimap.");
        var target = map.ImageToMap(new(map.ImageWidth * .62, map.ImageHeight * .45));
        void Pointer(int message, PointD p) => EditingSmokeScenario.Pointer(viewport, message, p);
        Pointer(0x201, target); Pointer(0x202, target);
        var mappedCenter = map.ImageToMap(model.Session.State.Viewport.ScreenToImage(center));
        Require((mappedCenter - target).Length < 1, "native click centers the mapped image location at actual DPI");
        var clicked = model.Session.State.Viewport;
        var grab = map.VisibleBounds(clicked).Center;
        Pointer(0x201, grab); Pointer(0x200, grab + new PointD(-10, 8)); Pointer(0x202, grab + new PointD(-10, 8));
        Require(clicked != model.Session.State.Viewport && clicked.Scale == model.Session.State.Viewport.Scale, "drag changes location while preserving zoom");
        var dragged = model.Session.State.Viewport;
        Pointer(0x201, target); Pointer(0x200, target + new PointD(15, 5)); NativeMethods.ReleaseCapture();
        Require(!model.Input.IsDragging && model.Session.State.Viewport == dragged, "capture loss restores the navigation baseline");
        Require(ReferenceEquals(before.Rois, model.Session.State.Rois) && ReferenceEquals(before.Selection, model.Session.State.Selection) && historyBytes == model.Session.HistoryBytes,
            "minimap preserves ROI values, selection and history");
        var path = $"artifacts/minimap-{name}.png"; var requested = DateTime.UtcNow;
        viewport.Capture(path);
        for (var i = 0; i < 100 && (!File.Exists(path) || File.GetLastWriteTimeUtc(path) < requested); i++) await Task.Delay(100);
        Require(File.Exists(path) && File.GetLastWriteTimeUtc(path) >= requested, "GPU minimap capture completes");
        model.Session.SetViewport(before.Viewport);
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Minimap smoke: " + message); }
}
