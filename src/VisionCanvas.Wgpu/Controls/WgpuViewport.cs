using VisionCanvas.Controls;
using VisionCanvas.Imaging;
using VisionCanvas.Rendering;
using VisionCanvas.Wgpu.Rendering;

namespace VisionCanvas.Wgpu.Controls;

/// <summary>공통 WPF/Win32 입력·레이어·수명을 공유하는 독립 Wgpu 전용 컨트롤.</summary>
public sealed class WgpuViewport : ViewportControlBase
{
    protected override IRenderWorker CreateRenderer(
        nint handle,
        TileWorker tiles,
        ITextRasterizer rasterizer,
        LatestFrameMailbox frames,
        IFramePreparer preparer)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        ArgumentNullException.ThrowIfNull(rasterizer);
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(preparer);
        return new WgpuRenderWorker(handle, tiles, rasterizer, frames, preparer);
    }
}
