using VisionCanvas.Diagnostics;

namespace VisionCanvas.Rendering;

/// <summary>네이티브 화면의 렌더 스레드 수명. Dispose 완료 후에만 HWND를 파괴한다.</summary>
public interface IRenderWorker : IDisposable
{
    event Action<string>? StatusChanged;
    event Action<RenderMetrics>? MetricsChanged;
    event Action<OverlayPresentation>? OverlayPresented;

    void Start();
    void Resize(int width, int height);
    void Submit(RenderScene scene);
    void Capture(string path);
}
