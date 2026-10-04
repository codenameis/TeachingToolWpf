using TeachingToolWpf.Core.Imaging;

namespace TeachingToolWpf.Presentation;

public interface IFramePresenter
{
    void PublishFrame(VideoFrame frame);
    void ClearFrames();
}
