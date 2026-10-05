using System.Windows;

namespace VisionCanvas.Sample;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var application = new Application();
        return application.Run(new SampleWindow(args));
    }
}
