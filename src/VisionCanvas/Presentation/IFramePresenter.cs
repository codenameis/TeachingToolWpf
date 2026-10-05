using VisionImaging.Abstractions;

namespace VisionCanvas.Presentation;

public interface IFramePresenter
{
    void PublishFrame(VideoFrame frame);
    void ClearFrames();
}
