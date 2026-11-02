using VisionCanvas.Controls;
using VisionCanvas.Imaging;
using VisionCanvas.Rendering;
using VisionCanvas.Vulkan.Rendering;

namespace VisionCanvas.Vulkan.Controls;

/// <summary>공통 WPF/Win32 입력·레이어·수명을 공유하는 독립 Vulkan 전용 컨트롤.</summary>
public sealed class VulkanViewport : ViewportControlBase
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
        return new VulkanRenderWorker(handle, tiles, rasterizer, frames, preparer);
    }
}
