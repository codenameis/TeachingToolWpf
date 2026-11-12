namespace VisionCanvas.Rendering.Abstractions;

/// <summary>공통 FrameLoop가 호출하는 그래픽스 백엔드 프레임 실행 계약.</summary>
public interface IFrameBackend
{
    IRenderSurface Surface { get; }

    ITexturePipeline Textures { get; }

    ITextRasterizer TextRasterizer { get; }

    IFramePreparer FramePreparer { get; }

    bool HasPendingRoiDetails { get; }

    double LastFenceWaitMilliseconds { get; }

    bool TryBeginFrame(int width, int height);

    void BeginDrawing();

    void DrawScene(RenderScene scene, SpriteMesh mesh, int roiBatchBoundary);

    void EndDrawing();

    IDisposable? CreateCaptureReadback();

    ulong Present();

    void WaitForCompletion(ulong completionToken);

    void SaveCapture(IDisposable? readback, string path);

    void OnAfterFrame();
}
