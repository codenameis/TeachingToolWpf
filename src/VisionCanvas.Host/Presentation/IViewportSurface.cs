using VisionCanvas.Rendering;

namespace VisionCanvas.Presentation;

public interface IViewportSurface : IImageDisplay
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
    void Submit(RenderScene scene);
    event Action? LayoutChanged;
}
