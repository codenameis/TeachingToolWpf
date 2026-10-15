namespace PoseMatch.Tdcm;

/// <summary>연산 중 호출자가 변경하지 않는 Gray8 또는 BGR24 영상.</summary>
public sealed class PixelImage
{
    public ReadOnlyMemory<byte> Pixels
    {
        get;
    }
    public int Width
    {
        get;
    }
    public int Height
    {
        get;
    }
    public int Stride
    {
        get;
    }
    public int Channels
    {
        get;
    }

    public PixelImage(ReadOnlyMemory<byte> pixels, int width, int height, int stride, int channels = 1)
    {
        if (width < 1 || height < 1 || channels is not (1 or 3) || stride < checked(width * channels) ||
            pixels.Length < checked((height - 1) * stride + width * channels))
        {
            throw new ArgumentException("Gray8/BGR24 크기, stride, 버퍼를 확인하세요.");
        }

        (Pixels, Width, Height, Stride, Channels) = (pixels, width, height, stride, channels);
    }

    internal float[] Normalize(int width, int height)
    {
        var result = new float[checked(width * height * 3)];
        ReadOnlySpan<float> means = [.485f, .456f, .406f];
        ReadOnlySpan<float> deviations = [.229f, .224f, .225f];
        var pixels = Pixels.Span;
        if (width == Width && height == Height)
        {
            // 검색 창은 이미 목표 크기다. bilinear 좌표/네 번의 픽셀 읽기는 필요 없다.
            Span<float> values = stackalloc float[256];
            for (var c = 0; c < 3; c++)
            {
                for (var value = 0; value < 256; value++)
                {
                    values[value] = (value / 255f - means[c]) / deviations[c];
                }
                var channel = Channels == 1 ? 0 : c;
                var destination = result.AsSpan(c * width * height, width * height);
                for (var y = 0; y < height; y++)
                {
                    var source = pixels.Slice(y * Stride, width * Channels);
                    for (var x = 0; x < width; x++)
                    {
                        destination[y * width + x] = values[source[x * Channels + channel]];
                    }
                }
            }
            return result;
        }
        for (var y = 0; y < height; y++)
        {
            var sy = Math.Clamp((y + .5) * Height / height - .5, 0, Height - 1);
            var y0 = (int)sy;
            var y1 = Math.Min(y0 + 1, Height - 1);
            var fy = (float)(sy - y0);
            for (var x = 0; x < width; x++)
            {
                var sx = Math.Clamp((x + .5) * Width / width - .5, 0, Width - 1);
                var x0 = (int)sx;
                var x1 = Math.Min(x0 + 1, Width - 1);
                var fx = (float)(sx - x0);
                for (var c = 0; c < 3; c++)
                {
                    // 공식 test.py는 cv2 BGR을 RGB로 바꾸지 않고 정규화한다.
                    var channel = Channels == 1 ? 0 : c;
                    var a = pixels[y0 * Stride + x0 * Channels + channel];
                    var b = pixels[y0 * Stride + x1 * Channels + channel];
                    var d = pixels[y1 * Stride + x0 * Channels + channel];
                    var e = pixels[y1 * Stride + x1 * Channels + channel];
                    var value = (a + (b - a) * fx) * (1 - fy) + (d + (e - d) * fx) * fy;
                    result[c * width * height + y * width + x] = (value / 255 - means[c]) / deviations[c];
                }
            }
        }
        return result;
    }
}
