using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TeachingToolWpf.ViewModels;
using VisionCanvas.Controls;
using VisionCanvas.Rendering;
using VisionGeometry;

namespace TeachingToolWpf.Diagnostics;

internal static class RoiHierarchyRulesSmoke
{
    public static async Task Run(MainWindowViewModel model, Dx12Viewport viewport)
    {
        var editor = model.Session;
        model.Fit.Execute(null);
        var root = editor.Add(new(new(4096, 4096), 6500, 6500, 0));
        var module = editor.AddChild(root, new(new(4096, 4096), 5000, 5000, 0));
        var group = editor.AddChild(module, new(new(4096, 4096), 3500, 3500, 0));
        var leaf = editor.AddChild(group, new(new(4096, 4096), 1500, 1500, 0));
        Require(!model.AddChildRoi.CanExecute(null), "fifth-level child button disabled");
        editor.Select([]);
        await CaptureAndCheck("default");

        var before = editor.State;
        model.StyleSettings.Module.Color = "#12A4F0";
        model.StyleSettings.Root.Color = "#E89012";
        model.StyleSettings.Apply.Execute(null);
        Require(string.IsNullOrEmpty(model.StyleSettings.Error), "settings valid");
        Require(ReferenceEquals(before, editor.State), "color changes preserve document and history");
        await CaptureAndCheck("custom");

        editor.Select([leaf]);
        model.Properties.SelectedType = 1;
        Require(editor.State.Rois[leaf].SearchRange is not null, "search type applies immediately");
        await model.Undo.ExecuteAsync();
        Require(editor.State.Rois[leaf].SearchRange is null, "type Undo");
        await model.Redo.ExecuteAsync();
        Require(editor.State.Rois[leaf].SearchRange is not null, "type Redo");

        var standalone = editor.Add(new(new(500, 500), 100, 100, 0));
        model.ChooseChildRois.Execute(null);
        editor.Select([group]);
        await model.AttachToParent.ExecuteAsync();
        Require(editor.State.Rois[standalone].ParentId == group, "existing standalone attaches without containment restriction");
        editor.Select([standalone, leaf]);
        Require(!model.Group.CanExecute(null), "parented children cannot regroup");
        await model.Undo.ExecuteAsync();
        Require(editor.State.Rois[standalone].ParentId is null, "attach Undo");
        editor.Select([root, standalone]);
        var count = editor.State.Rois.Count;
        var originalRoot = editor.State.Rois[root];
        Require(model.Group.CanExecute(null), "existing parent accepts standalone");
        await model.Group.ExecuteAsync();
        Require(editor.State.Rois.Count == count && editor.State.Rois[root] == originalRoot &&
            editor.State.Rois[standalone].ParentId == root, "group reuses parent without creating an ROI");
        await model.Undo.ExecuteAsync();
        Require(editor.State.Rois[standalone].ParentId is null, "group attachment Undo");
        editor.AddChild(standalone, new(new(500, 500), 20, 20, 0));
        editor.Select([root, standalone]);
        Require(!model.Group.CanExecute(null), "fifth-level group button disabled");
        File.AppendAllText("artifacts/smoke.log", "PASS: four hierarchy colors/default/custom GPU pixels, settings, immediate search type, attach, depth cap and history" + Environment.NewLine);

        async Task CaptureAndCheck(string name)
        {
            var path = Path.GetFullPath($"artifacts/hierarchy-{name}.png");
            var timer = Stopwatch.StartNew();
            viewport.Capture(path);
            while (true)
            {
                await Task.Delay(100);
                if (File.Exists(path))
                {
                    using var stream = File.OpenRead(path);
                    var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    var bitmap = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
                    var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                    bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                    foreach (var (id, role) in new[] { (root, RoiRole.Root), (module, RoiRole.Module), (group, RoiRole.Parent), (leaf, RoiRole.Child) })
                    {
                        var geometry = editor.State.Rois[id].Geometry;
                        var top = editor.State.Viewport.ImageToScreen(geometry.Center - new PointD(0, geometry.Height / 2));
                        Require(HasColor(pixels, bitmap.PixelWidth, bitmap.PixelHeight, top * viewport.Dpi, model.StyleSettings.Palette.For(role).Color), $"{name} {role} stroke pixels");
                    }
                    return;
                }
                if (timer.Elapsed > TimeSpan.FromSeconds(10))
                {
                    throw new TimeoutException("Hierarchy capture timed out.");
                }
            }
        }
    }

    internal static bool HasColor(byte[] pixels, int width, int height, PointD point, Vector4 expected)
    {
        for (var y = Math.Max(0, (int)point.Y - 5); y <= Math.Min(height - 1, (int)point.Y + 5); y++)
        {
            for (var x = Math.Max(0, (int)point.X - 5); x <= Math.Min(width - 1, (int)point.X + 5); x++)
            {
                var offset = ((y * width) + x) * 4;
                if (Math.Abs(pixels[offset] - (255 * expected.Z)) < 12 &&
                    Math.Abs(pixels[offset + 1] - (255 * expected.Y)) < 12 &&
                    Math.Abs(pixels[offset + 2] - (255 * expected.X)) < 12)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("ROI hierarchy smoke: " + message);
        }
    }
}
