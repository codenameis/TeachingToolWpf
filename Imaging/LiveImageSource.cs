using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Imaging;

namespace TeachingToolWpf.Imaging;

/// <summary>Image identity/bounds for a live source. Black tiles are a waiting surface before the first frame.</summary>
public sealed class LiveImageSource : ITileSource
{
    public ImageInfo Info { get; }
    public LiveImageSource(ImageReference reference)
    {
        if (!TryKind(reference, out var kind)) throw new ArgumentException("지원하지 않는 라이브 소스입니다.");
        Info = new(reference.Key, reference.Name, reference.Width, reference.Height, kind, TilePlanner.LevelCount(reference.Width, reference.Height));
    }
    public static bool TryKind(ImageReference? reference, out PixelKind kind)
    {
        kind = default;
        return reference is not null && reference.Key.StartsWith("live:", StringComparison.Ordinal) &&
            Enum.TryParse(reference.Key[5..], out kind) && Enum.IsDefined(kind);
    }
    public PixelBuffer Read(TileKey key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var factor = Math.Pow(2, key.Level);
        var width = Math.Min(512, (int)Math.Ceiling(Info.Width / factor) - key.X * 512);
        var height = Math.Min(512, (int)Math.Ceiling(Info.Height / factor) - key.Y * 512);
        var pixels = new PixelBuffer(width, height, Info.Pixels); pixels.Span.Clear(); return pixels;
    }
    public void Dispose() { }
}
