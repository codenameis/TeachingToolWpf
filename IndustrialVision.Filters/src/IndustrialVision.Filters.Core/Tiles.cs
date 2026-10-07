namespace IndustrialVision.Filters.Core;

public readonly record struct ImageRegion(int X, int Y, int Width, int Height);
public enum RoiBoundaryMode
{
    SourceContext, Isolated
}

public interface IImageTileSource
{
    ImageDescriptor Descriptor
    {
        get;
    }
    void Read(ImageRegion region, HostImage destination, CancellationToken token);
}

/// <summary>타일은 Write 동안만 유효하다. 실패/취소 시 부분 결과를 완료로 게시하지 않는다.</summary>
public interface IImageTileSink
{
    void Write(int x, int y, ImageView tile, CancellationToken token);
    void Complete();
    void Abort();
}

public sealed class MemoryTileSource(ImageView image) : IImageTileSource
{
    public ImageDescriptor Descriptor => image.Descriptor;

    public void Read(ImageRegion region, HostImage destination, CancellationToken token)
    {
        var view = image.Region(region.X, region.Y, region.Width, region.Height);
        if (destination.Descriptor.Width != region.Width || destination.Descriptor.Height != region.Height ||
            destination.Descriptor.Format != Descriptor.Format)
        {
            throw new ArgumentException("Tile descriptor mismatch.");
        }
        for (var y = 0; y < region.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            view.Data.Span.Slice(y * view.Descriptor.StrideBytes, region.Width * Descriptor.BytesPerPixel)
                .CopyTo(destination.Data.Span.Slice(y * destination.Descriptor.StrideBytes));
            var mask = destination.Validity.Span.Slice(y * region.Width, region.Width);
            if (view.Validity.IsEmpty)
            {
                mask.Fill(255);
            }
            else
            {
                view.Validity.Span.Slice(y * view.MaskStride, region.Width).CopyTo(mask);
            }
        }
    }
}
