namespace IndustrialVision.Filters.Core;

public interface IImageTileSource
{
    ImageDescriptor Descriptor
    {
        get;
    }
    void Read(ImageRegion region, HostImage destination, CancellationToken token);
}
