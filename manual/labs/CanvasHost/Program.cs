using System.Windows;

namespace VisionCanvas.Manual;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var application = new Application();
        return application.Run(new LessonWindow(args.Contains("--software-rendering")));
    }
}
