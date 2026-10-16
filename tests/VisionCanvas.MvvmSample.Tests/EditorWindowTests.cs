using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VisionCanvas.Core.Editing;
using VisionCanvas.Controls;
using VisionCanvas.MvvmSample.Models;
using VisionCanvas.MvvmSample.Services;
using VisionCanvas.MvvmSample.ViewModels;
using VisionCanvas.MvvmSample.Views;
using VisionCanvas.Presentation;
using VisionCanvas.Rendering;
using VisionImaging;
using VisionImaging.Abstractions;

namespace VisionCanvas.MvvmSample.Tests;

public sealed partial class EditorWindowTests
{
    [Theory]
    [InlineData(RenderDevicePreference.Automatic)]
    [InlineData(RenderDevicePreference.Software)]
    public Task TwoCameraWindowRendersBothModelsAndKeepsSelectionAndHistoryIndependent(RenderDevicePreference device) => OnStaAsync(async () =>
    {
        var output = Path.Combine(RepositoryRoot(), "artifacts", "mvvm-sample", "two-cameras", device.ToString());
        Directory.CreateDirectory(output);
        using var bindingErrors = new StringWriter();
        using var listener = new TextWriterTraceListener(bindingErrors);
        var trace = PresentationTraceSources.DataBindingSource;
        var previousLevel = trace.Switch.Level;
        trace.Switch.Level = SourceLevels.Warning;
        trace.Listeners.Add(listener);
        var window = new InspectionModelWindow { ShowActivated = false, ShowInTaskbar = false, Left = -32000, Top = -32000 };
        var definition = SampleInspectionModel.Create();
        var errors = new List<string>();
        var adapters = new List<string>();
        var loader = new DemoImageLoader(new ImageLoader(Path.Combine(output, "cache")));
        using var model = new InspectionModelViewModel(definition.Name,
            CreateCamera(definition.FirstCamera, window.FirstViewport), CreateCamera(definition.SecondCamera, window.SecondViewport));
        window.DataContext = model;
        window.Closing += (_, _) => model.Dispose();
        try
        {
            window.Show();
            Invoke(window, "2카메라 모델 불러오기");
            await UntilAsync(() => model.FirstCamera.Scene.Document.Rois.Count == 7 && model.SecondCamera.Scene.Document.Rois.Count == 7 &&
                model.LoadCommand.CanExecute(null) && adapters.Count == 2);
            Assert.Same(model.FirstCamera.Scene, window.FirstViewport.Scene);
            Assert.Same(model.SecondCamera.Scene, window.SecondViewport.Scene);
            await CaptureViewportAsync(window.FirstViewport, Path.Combine(output, "camera-1.png"), HasSamplePixels);
            await CaptureViewportAsync(window.SecondViewport, Path.Combine(output, "camera-2.png"), pixels =>
            {
                var blue = 0;
                for (var index = 0; index < pixels.Length; index += 4)
                {
                    if (pixels[index] is > 60 and < 130 && pixels[index + 1] < 70)
                    {
                        blue++;
                    }
                }
                return blue > 1000;
            });
            var firstBefore = model.FirstCamera.Scene.Document;
            var list = Descendants(window).OfType<ListBox>().Single(control => ReferenceEquals(control.DataContext, model.SecondCamera));
            var selected = model.SecondCamera.RoiItems.First(item => item.Algorithm is not null);
            list.SelectedItem = selected;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.Equal(selected.Id, Assert.Single(model.SecondCamera.Scene.Document.Selection));
            Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text == selected.Parameters);
            SendMessage(window.SecondViewport.Handle, 0x0100, 0x2E, 0);
            Assert.Equal(6, model.SecondCamera.Scene.Document.Rois.Count);
            await model.SecondCamera.UndoCommand.ExecuteAsync();
            Assert.Equal(7, model.SecondCamera.Scene.Document.Rois.Count);
            Assert.Equal(selected.Id, model.SecondCamera.SelectedRoi?.Id);
            Assert.Equal(firstBefore, model.FirstCamera.Scene.Document);
            Assert.Empty(errors);
            Assert.Equal(string.Empty, bindingErrors.ToString());
            File.WriteAllText(Path.Combine(output, "result.txt"), $"PASS\n{string.Join('\n', adapters)}\nTwo 8192 × 8192 images / 14 ROIs including 4 groups / 10 algorithm assignments / independent native Delete and Undo / bindings\n");
        }
        finally
        {
            model.Dispose();
            window.Close();
            trace.Listeners.Remove(listener);
            trace.Switch.Level = previousLevel;
        }
        Assert.Equal(nint.Zero, window.FirstViewport.Handle);
        Assert.Equal(nint.Zero, window.SecondViewport.Handle);

        EditorViewModel CreateCamera(CameraModel camera, Dx12Viewport viewport)
        {
            viewport.DevicePreference = device;
            viewport.RendererStatusChanged += status =>
            {
                if (status.StartsWith("DX12 ·", StringComparison.Ordinal))
                {
                    adapters.Add(status);
                }
                else if (status.Contains("오류", StringComparison.Ordinal))
                {
                    errors.Add(status);
                }
            };
            viewport.InputFailed += error => errors.Add(error.Message);
            viewport.Tiles.Failed += errors.Add;
            var editor = new EditorSession();
            var layout = new ViewportLayout();
            return new(editor, new(editor), layout, new(editor, viewport, layout, loader), new Picker(), camera);
        }
    });

    [Theory]
    [InlineData(RenderDevicePreference.Automatic)]
    [InlineData(RenderDevicePreference.Software)]
    public Task RealWindowBindsCommandsRenders8KAnd24KAndHandlesNativeInput(RenderDevicePreference device) => OnStaAsync(async () =>
    {
        var output = Path.Combine(RepositoryRoot(), "artifacts", "mvvm-sample", device.ToString());
        Directory.CreateDirectory(output);
        using var bindingErrors = new StringWriter();
        using var listener = new TextWriterTraceListener(bindingErrors);
        var trace = PresentationTraceSources.DataBindingSource;
        var previousLevel = trace.Switch.Level;
        trace.Switch.Level = SourceLevels.Warning;
        trace.Listeners.Add(listener);
        var errors = new List<string>();
        var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new EditorWindow { ShowActivated = false, ShowInTaskbar = false, Left = -32000, Top = -32000 };
        window.Viewport.DevicePreference = device;
        var editor = new EditorSession();
        var layout = new ViewportLayout();
        var picker = new Picker();
        using var model = new EditorViewModel(editor, new(editor), layout,
            new(editor, window.Viewport, layout, new ImageLoader(Path.Combine(output, "cache"))), picker);
        window.DataContext = model;
        window.Closing += (_, _) => model.Dispose();
        window.Viewport.RendererStatusChanged += status =>
        {
            if (status.StartsWith("DX12 ·", StringComparison.Ordinal))
            {
                ready.TrySetResult(status);
            }
            else if (status.Contains("오류", StringComparison.Ordinal))
            {
                errors.Add(status);
                ready.TrySetException(new InvalidOperationException(status));
            }
        };
        window.Viewport.InputFailed += error => errors.Add(error.Message);
        window.Viewport.Tiles.Failed += errors.Add;
        try
        {
            window.Show();
            var adapter = await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.False(FindButton(window, "사각형 그리기").IsEnabled);
            Invoke(window, "8K 예제");
            await UntilAsync(() => model.Scene.Image is not null && model.IsInputEnabled);
            Assert.Equal(8192, model.Scene.Image!.Width);
            Assert.Same(model.Scene, window.Viewport.Scene);
            Assert.Same(model.Input, window.Viewport.PointerInput);
            Assert.Equal(window.Viewport.ActualWidth, layout.Width);
            await CaptureAsync(window, Path.Combine(output, "8k.png"), HasSamplePixels);

            Invoke(window, "사각형 그리기");
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            SendPoint(0x0201, 400, 180);
            SendPoint(0x0200, 560, 280);
            SendPoint(0x0202, 560, 280);
            Assert.Single(editor.State.Rois);
            Assert.True(FindButton(window, "Undo").IsEnabled);
            await CaptureAsync(window, Path.Combine(output, "roi.png"), HasSelectedOutline);
            SendMessage(window.Viewport.Handle, 0x0100, 0x2E, 0); // Delete, native HWND focus path.
            Assert.Empty(editor.State.Rois);
            Invoke(window, "Undo");
            await UntilAsync(() => !editor.State.Rois.IsEmpty && model.IsInputEnabled);

            var imagePath = Path.Combine(output, "24k-input.png");
            Write24KImage(imagePath);
            picker.Reference = new(imagePath, "24K", 0, 0, ImageFileVersion.Read(imagePath));
            Invoke(window, "파일 열기");
            await UntilAsync(() => model.Scene.Image?.Width == 24576 && model.IsInputEnabled);
            Assert.Equal(8192, model.Scene.Image!.Height);
            Assert.Empty(editor.State.Rois);
            await CaptureAsync(window, Path.Combine(output, "24k.png"), pixels => pixels.Count(value => value == 176) > 10000);
            Invoke(window, "Undo");
            await UntilAsync(() => model.Scene.Image?.Width == 8192 && model.IsInputEnabled);
            Assert.Single(editor.State.Rois);
            Invoke(window, "Redo");
            await UntilAsync(() => model.Scene.Image?.Width == 24576 && model.IsInputEnabled);
            Assert.Empty(editor.State.Rois);
            window.Width = 1000;
            window.Height = 700;
            window.UpdateLayout();
            Assert.Equal(window.Viewport.ActualWidth, layout.Width);
            Assert.Equal(window.Viewport.ActualHeight, layout.Height);
            Assert.Empty(errors);
            Assert.Equal(string.Empty, bindingErrors.ToString());
            File.WriteAllText(Path.Combine(output, "result.txt"), $"PASS\n{adapter}\n8K / 24576 × 8192 PNG / HWND ROI drawing and Delete / image Undo, Redo / resize / bindings\n");
        }
        finally
        {
            model.Dispose();
            window.Close();
            window.Viewport.Dispose();
            trace.Listeners.Remove(listener);
            trace.Switch.Level = previousLevel;
        }
        Assert.Equal(nint.Zero, window.Viewport.Handle);

        void SendPoint(int message, int x, int y)
        {
            var px = (int)Math.Round(x * window.Viewport.Dpi);
            var py = (int)Math.Round(y * window.Viewport.Dpi);
            SendMessage(window.Viewport.Handle, message, message == 0x0202 ? 0 : 1, (py << 16) | px);
        }
    });

    private static void Write24KImage(string path)
    {
        var pixels = new byte[24576 * 8192];
        Array.Fill(pixels, (byte)176);
        var bitmap = BitmapSource.Create(24576, 8192, 96, 96, PixelFormats.Gray8, null, pixels, 24576);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static bool HasSamplePixels(byte[] pixels)
    {
        var boardPixels = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] < 100 && pixels[i + 1] is > 60 and < 130 && pixels[i + 2] < 70)
            {
                boardPixels++;
            }
        }
        return boardPixels > 10000;
    }

    private static bool HasSelectedOutline(byte[] pixels)
    {
        for (var i = 0; i < pixels.Length; i += 4)
        {
            // Standard.Child.SelectedColor is mint, including a strong blue channel.
            if (pixels[i + 1] > 230 && pixels[i] is > 150 and < 220 && pixels[i + 2] < 50)
            {
                return true;
            }
        }
        return false;
    }

    private static Task CaptureAsync(EditorWindow window, string path, Func<byte[], bool> accepts)
        => CaptureViewportAsync(window.Viewport, path, accepts);

    private static async Task CaptureViewportAsync(Dx12Viewport viewport, string path, Func<byte[], bool> accepts)
    {
        var started = Stopwatch.StartNew();
        while (started.Elapsed < TimeSpan.FromSeconds(20))
        {
            // A unique name avoids reading the previous completed capture during a retry.
            var candidate = Path.ChangeExtension(path, $"{Guid.NewGuid():N}.png");
            viewport.Capture(candidate);
            await Task.Delay(150);
            try
            {
                using var stream = File.OpenRead(candidate);
                var bitmap = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                if (accepts(pixels))
                {
                    File.Copy(candidate, path, true);
                    return;
                }
            }
            catch (IOException)
            {
                // The renderer may still be writing this capture.
            }
        }
        throw new TimeoutException($"Expected pixels were not rendered: {path}");
    }

    private static Button FindButton(DependencyObject parent, string content)
        => Descendants(parent).OfType<Button>().Single(button => Equals(button.Content, content));

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

    private static void Invoke(DependencyObject window, string content)
    {
        var button = FindButton(window, content);
        Assert.True(button.IsEnabled);
        var peer = new ButtonAutomationPeer(button);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var started = Stopwatch.StartNew();
        while (!condition())
        {
            if (started.Elapsed > TimeSpan.FromSeconds(30))
            {
                throw new TimeoutException("WPF command did not complete.");
            }
            await Task.Delay(20);
        }
    }

    private static Task OnStaAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            try
            {
                var task = action();
                _ = task.ContinueWith(finished =>
                {
                    if (finished.IsFaulted)
                    {
                        completion.TrySetException(finished.Exception.InnerExceptions);
                    }
                    else if (finished.IsCanceled)
                    {
                        completion.TrySetCanceled();
                    }
                    else
                    {
                        completion.TrySetResult();
                    }
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }, TaskScheduler.Default);
                Dispatcher.Run();
            }
            catch (Exception error)
            {
                completion.TrySetException(error);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(120));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TeachingToolWpf.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class Picker : IImageFilePicker
    {
        public ImageReference? Reference { get; set; }
        public ImageReference? Pick() => Reference;
    }

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
}
