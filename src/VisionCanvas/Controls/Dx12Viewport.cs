using VisionCanvas.Imaging;
using VisionCanvas.Rendering;

namespace VisionCanvas.Controls;

/// <summary>Direct3D 12 전용 뷰포트 컨트롤.</summary>
public sealed class Dx12Viewport : ViewportControlBase
{
    private RenderDevicePreference _devicePreference;

    /// <summary>Set before the viewport creates its native window. Automatic prefers hardware, then WARP.</summary>
    public RenderDevicePreference DevicePreference
    {
        get => _devicePreference;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            if (HasActiveRenderer || Handle != nint.Zero)
            {
                throw new InvalidOperationException("렌더 장치 선택은 뷰포트를 표시하기 전에 설정해야 합니다.");
            }
            _devicePreference = value;
        }
    }

    protected override IRenderWorker CreateRenderer(
        nint handle,
        TileWorker tiles,
        ITextRasterizer rasterizer,
        LatestFrameMailbox frames,
        IFramePreparer preparer)
        => new RenderWorker(handle, tiles, rasterizer, frames, preparer, _devicePreference);
}
