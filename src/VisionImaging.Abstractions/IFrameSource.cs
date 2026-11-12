namespace VisionImaging.Abstractions;

public interface IFrameSource
{
    ImageInfo Info
    {
        get;
    }
    IAsyncEnumerable<VideoFrame> Frames(CancellationToken cancellationToken);
}
