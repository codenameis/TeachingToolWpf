using VisionImaging.Abstractions;

namespace VisionImaging;

/// <summary>앱/CLI/샘플 조립 지점. Canvas나 검사는 디코더 종류를 선택하지 않는다.</summary>
internal static class ImageLoaderHost
{
#if SKIA_IMAGE_LOADER
    public const string Backend = "Skia JPEG Scanline";
    public static IImageLoader Create(string cacheDirectory)
        => new SourceImageLoader(new Pyramid.RawImageLoader(cacheDirectory, new SkiaFileDecoder()));
#else
    public const string Backend = "Streaming";
    public static IImageLoader Create(string cacheDirectory)
        => new ImageLoader(cacheDirectory);
#endif
}
