using VisionImaging.Abstractions;
using VisionImaging.Codecs;
using VisionImaging.Pyramid;

namespace VisionImaging;

/// <summary>기존 공개 진입점을 유지하는 자체 raw 피라미드 어댑터. 이전 VIPS 캐시와 키를 공유하지 않는다.</summary>
public sealed class DiskPyramidSource : ITileSource
{
    private readonly RawPyramidSource source;
    private DiskPyramidSource(RawPyramidSource source) => this.source = source;
    public ImageInfo Info => source.Info;
    public static string Version(string path) => ImageFileVersion.Read(path);
    public static DiskPyramidSource Open(string path, string cacheRoot, IProgress<string> progress, CancellationToken cancellationToken)
        => new(RawPyramidSource.Open(path, cacheRoot, new NativeRasterDecoder(), progress, cancellationToken));
    public PixelBuffer Read(TileKey key, CancellationToken cancellationToken) => source.Read(key, cancellationToken);
    public void Dispose() => source.Dispose();
}
