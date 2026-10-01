using TeachingToolWpf.Core.Imaging;
using TeachingToolWpf.Rendering;

namespace TeachingToolWpf.Presentation;

public interface IViewportSurface
{
    double ViewWidth { get; }
    double ViewHeight { get; }
    double Dpi { get; }
    long SetSource(ITileSource source);
    void Submit(RenderScene scene);
    Task RestartAsync();
    event Action? LayoutChanged;
}
