using VisionCanvas.Rendering.Abstractions;
using VisionImaging.Abstractions;

namespace VisionCanvas.Skia.Rendering;

internal sealed class SkiaTexturePipeline : TexturePipelineBase<SkiaTexture, SkiaSurface>
{
    public override SkiaTexture CreateTypedTexture(int width, int height, PixelKind kind)
        => new(width, height, kind);

    public override SkiaTexture UploadTyped(PixelBuffer pixels, SkiaSurface surface)
    {
        var texture = new SkiaTexture(pixels.Width, pixels.Height, pixels.Kind);
        try
        {
            texture.Update(pixels);
            return texture;
        }
        catch
        {
            texture.Dispose();
            throw;
        }
    }

    public override void UpdateTyped(SkiaTexture texture, PixelBuffer pixels, SkiaSurface surface)
        => texture.Update(pixels);

    internal static SkiaTexture GetTexture(in SpriteBatch batch) => RequireTexture(batch.Texture);
}
