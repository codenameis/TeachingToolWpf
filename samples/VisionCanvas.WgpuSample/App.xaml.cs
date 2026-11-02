using System.IO;
using System.Windows;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Interaction;
using VisionCanvas.Presentation;
using VisionCanvas.WgpuSample.Services;
using VisionCanvas.WgpuSample.ViewModels;
using VisionCanvas.WgpuSample.Views;
using VisionImaging;

namespace VisionCanvas.WgpuSample;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var window = new EditorWindow();
        var editor = new EditorSession();
        var input = new InteractionController(editor);
        var layout = new ViewportLayout();
        var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VisionCanvas.WgpuSample", "ImageCache");
        var images = new ImageCoordinator(editor, window.Viewport, layout, new ImageLoader(cache));
        var model = new EditorViewModel(editor, input, layout, images, new ImageFilePicker(window));
        window.DataContext = model;
        window.Closing += (_, _) => model.Dispose();
        MainWindow = window;
        window.Show();
    }
}

