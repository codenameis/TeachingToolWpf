using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TeachingToolWpf.Controls;
using TeachingToolWpf.ViewModels;
using VisionCanvas.Controls;
using VisionCanvas.Core.Editing;
using VisionGeometry;

namespace TeachingToolWpf.Diagnostics;

internal static class RoiSearchSmokeScenario
{
    public static async Task Run(Window window, MainWindowViewModel model, Dx12Viewport viewport)
    {
        var editor = model.Session;
        var originalView = editor.State.Viewport;
        editor.SetViewport(ViewportTransform.Identity);
        var id = editor.Add(RoiGeometry.Create(RoiShapeKind.Triangle, new(220, 220), 100, 80));
        window.UpdateLayout();
        var controls = Descendants(window).ToArray();
        var type = controls.OfType<ComboBox>().Single(control => AutomationProperties.GetName(control) == "ROI 타입");
        type.SelectedIndex = 1;
        await Dispatcher.Yield(DispatcherPriority.Background);
        Edit("검색 가로 여유", "60");
        Edit("검색 세로 여유", "40");
        model.ApplyProperties.Execute(null);
        Require(editor.State.Rois[id].SearchRange == new RoiSearchRange(60, 40), "numeric inspector range commits");
        await Capture("dashed", true);
        type.SelectedIndex = 0;
        model.ApplyProperties.Execute(null);
        await Capture("standard", false);
        await model.Undo.ExecuteAsync();
        await Capture("undo", true);
        await model.Redo.ExecuteAsync();
        Require(editor.State.Rois[id].SearchRange is null, "Redo restores standard type");
        editor.AddChild(id, new(new(220, 220), 20, 20, 0));
        editor.Select([id]);
        window.UpdateLayout();
        Edit("ROI 이름", "한글 그룹 이름");
        var visible = controls.OfType<CheckBox>().Single(control => AutomationProperties.GetName(control) == "캔버스 이름 표시");
        visible.IsChecked = false;
        model.ApplyProperties.Execute(null);
        Require(editor.State.Rois[id].Name == "한글 그룹 이름" && !editor.State.Rois[id].IsNameVisible,
            "parent name and Visible binding commit together");
        Require(model.Properties.IsGroup && !model.Properties.CanChangeType && !model.Matching.Train.CanExecute(null),
            "group parent cannot become inspection input");
        await model.Undo.ExecuteAsync();
        Require(editor.State.Rois[id].IsNameVisible, "Undo restores parent label visibility");
        await model.Redo.ExecuteAsync();
        editor.DeleteSelection();
        editor.SetViewport(originalView);

        void Edit(string name, string text)
        {
            var field = controls.OfType<ValueTextBox>().Single(control => AutomationProperties.GetName(control) == name);
            field.BringIntoView();
            Keyboard.Focus(field);
            field.Text = text;
            Keyboard.ClearFocus();
        }

        async Task Capture(string name, bool dashed)
        {
            var path = Path.GetFullPath($"artifacts/roi-search-{name}.png");
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
                    var red = 0;
                    var gaps = 0;
                    for (var x = 115; x < 325; x++)
                    {
                        var found = false;
                        for (var dy = -1; dy <= 1; dy++)
                        {
                            var offset = ((((int)Math.Round(140 * viewport.Dpi) + dy) * bitmap.PixelWidth) + (int)Math.Round(x * viewport.Dpi)) * 4;
                            found |= pixels[offset] < 20 && pixels[offset + 1] < 20 && pixels[offset + 2] > 235;
                        }
                        if (found)
                        {
                            red++;
                        }
                        else
                        {
                            gaps++;
                        }
                    }
                    Require(dashed ? red > 70 && gaps > 40 : red == 0, $"{name}: red dash/gap pixels ({red}/{gaps})");
                    return;
                }
                catch (IOException) when (timer.Elapsed < TimeSpan.FromSeconds(30))
                {
                    // 파일 저장만 기다린다. 픽셀 판정 실패는 재시도하지 않는다.
                }
            }
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
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
            throw new InvalidOperationException("ROI search smoke: " + message);
        }
    }
}
