using System.IO;
using System.Windows;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Interaction;
using VisionCanvas.Presentation;
using VisionCanvas.SampleShell.Services;
using VisionCanvas.VulkanSample.ViewModels;
using VisionCanvas.VulkanSample.Views;
using VisionImaging;

namespace VisionCanvas.VulkanSample;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var window = new EditorWindow();
        var editor = new EditorSession();
        var input = new InteractionController(editor);
        var layout = new ViewportLayout();
        string cache = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VisionCanvas.VulkanSample",
            "ImageCache");
        var images = new ImageCoordinator(editor, window.Viewport, layout, new ImageLoader(cache));
        var model = new EditorViewModel(editor, input, layout, images, new ImageFilePicker(window, "Vulkan Canvas · 이미지 열기"));
        window.DataContext = model;
        window.Closing += (_, _) => model.Dispose();
        MainWindow = window;
        window.Show();
    }
}
