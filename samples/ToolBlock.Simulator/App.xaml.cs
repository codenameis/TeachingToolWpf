using System;
using System.IO;
using System.Windows;
using ToolBlock.Simulator.Services;
using ToolBlock.Simulator.ViewModels;
using ToolBlock.Simulator.Views;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Interaction;
using VisionCanvas.Presentation;

namespace ToolBlock.Simulator;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var window = new MainSimulatorWindow();
        var editor = new EditorSession();
        var input = new InteractionController(editor);
        var layout = new ViewportLayout();
        string cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ToolBlock.Simulator",
            "ImageCache");
        var loader = new ToolBlockDynamicImageLoader(cacheDir);
        var images = new ImageCoordinator(editor, window.Viewport, layout, loader);
        var viewModel = new MainSimulatorViewModel(editor, input, layout, images, loader);

        window.DataContext = viewModel;
        window.Closing += (_, _) => viewModel.Dispose();
        MainWindow = window;
        window.Show();
    }
}
