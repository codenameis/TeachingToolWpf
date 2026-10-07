using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TeachingToolWpf.Presentation;
using VisionCanvas.Controls;
using VisionCanvas.Core.Interaction;
using VisionGeometry;

namespace TeachingToolWpf.Diagnostics;

internal static class MaskSmokeScenario
{
    public static async Task Run(Window window, EditorViewModel model, Dx12Viewport viewport)
    {
        var editor = model.Session;
        editor.SetViewport(ViewportTransform.Identity);
        var geometry = new RoiGeometry(new(250, 230), 240, 240, 28, RoiShape.Circle);
        var id = editor.Add(geometry);
        editor.Rename(id, "마스크 티칭 영역");
        window.UpdateLayout();
        var buttons = Descendants(window).OfType<Button>().ToArray();
        var rectangle = buttons.Single(button => AutomationProperties.GetName(button) == "사각 마스크 추가");
        rectangle.BringIntoView();
        rectangle.Command.Execute(null);
        Require(model.Input.Tool == EditorTool.Mask, "WPF rectangle button starts masking");
        Drag(geometry.ToWorld(new(-80, -40)), geometry.ToWorld(new(180, 110)));
        Require(editor.State.Rois[id].Masks.Length == 1, "native rectangle commits one mask");
        buttons.Single(button => AutomationProperties.GetName(button) == "타원 마스크 추가").Command.Execute(null);
        Drag(geometry.ToWorld(new(-70, -70)), geometry.ToWorld(new(-10, -15)));
        Require(editor.State.Rois[id].Masks.Length == 2, "native ellipse commits second mask");
        var masks = editor.State.Rois[id].Masks;
        EditingSmokeScenario.Pointer(viewport, 0x201, geometry.Center);
        EditingSmokeScenario.Pointer(viewport, 0x200, geometry.Center + new PointD(60, 60));
        model.HandleKey(System.Windows.Input.Key.Escape, false, false);
        EditingSmokeScenario.Pointer(viewport, 0x202, geometry.Center + new PointD(60, 60));
        Require(editor.State.Rois[id].Masks == masks && model.Input.Tool == EditorTool.Select, "Escape cancels draft and exits mode");
        var on = await Capture("mask-on");
        WindowCapture.Save(window, viewport, "artifacts/mask-on.png", "artifacts/mask-app.png");
        var display = Descendants(window).OfType<CheckBox>().Single(control => AutomationProperties.GetName(control) == "마스크 표시");
        display.IsChecked = false;
        var off = await Capture("mask-off");
        var changedInside = 0;
        var changedOutside = 0;
        for (var y = 0; y < on.Height; y++)
        {
            for (var x = 0; x < on.Width; x++)
            {
                var offset = ((y * on.Width) + x) * 4;
                var difference = Math.Abs(on.Pixels[offset] - off.Pixels[offset]) + Math.Abs(on.Pixels[offset + 1] - off.Pixels[offset + 1]) +
                    Math.Abs(on.Pixels[offset + 2] - off.Pixels[offset + 2]);
                if (difference < 15)
                {
                    continue;
                }
                var point = new PointD((x + .5) / viewport.Dpi, (y + .5) / viewport.Dpi);
                if (geometry.Contains(point, 1))
                {
                    changedInside++;
                }
                else
                {
                    changedOutside++;
                }
            }
        }
        Require(changedInside > 500 && changedOutside == 0, $"GPU mask pixels clipped to circle ({changedInside}/{changedOutside})");
        editor.Select([]);
        editor.Select([id]);
        Require(!model.Masks.ShowMasks && editor.State.Rois[id].Masks == masks, "hidden display survives reselection without removing masks");
        display.IsChecked = true;
        await model.Undo.ExecuteAsync();
        Require(editor.State.Rois[id].Masks.Length == 1, "single undo removes last ellipse");
        await model.Redo.ExecuteAsync();
        Require(editor.State.Rois[id].Masks == masks, "redo restores exact mask values");
        model.Copy.Execute(null);
        await model.Paste.ExecuteAsync();
        var copy = editor.State.Rois[editor.State.Selection.Single()];
        Require(copy.Id != id && copy.Masks.SequenceEqual(masks), "copy and paste preserve masks");
        editor.Select([id, copy.Id]);
        await model.Delete.ExecuteAsync();
        File.AppendAllText("artifacts/smoke.log", $"ROI mask UI/native rectangle+ellipse/Esc/history/copy/visibility/GPU clipping passed: {changedInside} changed pixels, {changedOutside} outside" + Environment.NewLine);

        void Drag(PointD first, PointD last)
        {
            EditingSmokeScenario.Pointer(viewport, 0x201, first);
            EditingSmokeScenario.Pointer(viewport, 0x200, last);
            EditingSmokeScenario.Pointer(viewport, 0x202, last);
        }

        async Task<(byte[] Pixels, int Width, int Height)> Capture(string name)
        {
            var path = Path.GetFullPath($"artifacts/{name}.png");
            viewport.Capture(path);
            var timer = Stopwatch.StartNew();
            while (true)
            {
                await Task.Delay(50);
                try
                {
                    using var stream = File.OpenRead(path);
                    var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    var bitmap = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
                    var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                    bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                    return (pixels, bitmap.PixelWidth, bitmap.PixelHeight);
                }
                catch (IOException) when (timer.Elapsed < TimeSpan.FromSeconds(30))
                {
                    // 렌더 스레드의 캡처 저장 완료만 기다린다.
                }
            }
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index)))
            {
                yield return child;
            }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
