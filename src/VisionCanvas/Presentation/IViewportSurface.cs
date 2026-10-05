using VisionCanvas.Rendering;
using VisionImaging.Abstractions;

namespace VisionCanvas.Presentation;

public interface IViewportSurface
{
    double ViewWidth
    {
        get;
    }
    double ViewHeight
    {
        get;
    }
    double Dpi
    {
        get;
    }
    long SetSource(ITileSource source);
    void Submit(RenderScene scene);
    Task RestartAsync();
    event Action? LayoutChanged;
}
