using System.IO;
using VisionImaging.Codecs;
using VisionImaging.Pyramid;
using VisionImaging.Skia;

namespace VisionImaging;

/// <summary>호스트 전용 형식 선택. TIFF/WebP는 기본 코덱을 사용하고 PNG/JPEG 오류는 그대로 전달한다.</summary>
internal sealed class SkiaFileDecoder : IRasterDecoder
{
    public string CacheIdentity => "Skia-comparison-v1-" + new SkiaRasterDecoder().CacheIdentity + new NativeRasterDecoder().CacheIdentity;

    public IRasterReader Open(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var input = File.OpenRead(path);
        Span<byte> magic = stackalloc byte[12];
        var count = input.Read(magic);
        var tiff = count >= 4 && ((magic[0] == 73 && magic[1] == 73 && magic[3] == 0 && magic[2] is 42 or 43)
            || (magic[0] == 77 && magic[1] == 77 && magic[2] == 0 && magic[3] is 42 or 43));
        var webp = count == 12 && magic[..4].SequenceEqual("RIFF"u8) && magic[8..].SequenceEqual("WEBP"u8);
        return (tiff || webp ? (IRasterDecoder)new NativeRasterDecoder() : new SkiaRasterDecoder()).Open(path, token);
    }
}
