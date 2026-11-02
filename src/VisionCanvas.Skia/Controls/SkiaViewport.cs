using VisionCanvas.Controls;
using VisionCanvas.Imaging;
using VisionCanvas.Rendering;
using VisionCanvas.Skia.Rendering;

namespace VisionCanvas.Skia.Controls;

/// <summary>공통 HWND 입력·바인딩·문서 수명을 사용하고 모든 캔버스 픽셀은 Skia로 그리는 독립 컨트롤.</summary>
public sealed class SkiaViewport : ViewportControlBase
{
    private const int PaintMessage = 0x000F;

    private SkiaRenderWorker? _worker;

    protected override IRenderWorker CreateRenderer(
        nint handle,
        TileWorker tiles,
        ITextRasterizer rasterizer,
        LatestFrameMailbox frames,
        IFramePreparer preparer)
    {
        _worker = new(handle, tiles, frames, preparer);
        return _worker;
    }

    protected override nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        var result = base.WndProc(hwnd, message, wParam, lParam, ref handled);
        if (message == PaintMessage)
        {
            _worker?.Invalidate();
        }
        return result;
    }
}
