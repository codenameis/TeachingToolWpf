using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TeachingToolWpf.Core.Imaging;

namespace TeachingToolWpf.Imaging;

public sealed class SyntheticFrameSource(PixelKind kind = PixelKind.Gray8, int width = 7680, int height = 4320, int fps = 60) : IFrameSource
{
    public ImageInfo Info { get; } = new("live:synthetic", "8K 실시간 합성 영상", width, height, kind, 1);
    public async IAsyncEnumerable<VideoFrame> Frames([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var template = new PixelBuffer(width, height, kind);
        BuildTemplate(template);
        var timer = Stopwatch.StartNew();
        var period = 1000d / fps;
        var deadline = period;
        long sequence = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            while (deadline > timer.Elapsed.TotalMilliseconds)
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, Math.Ceiling(deadline - timer.Elapsed.TotalMilliseconds))), cancellationToken).ConfigureAwait(false);
            deadline = Math.Max(deadline + period, timer.Elapsed.TotalMilliseconds);
            var pixels = new PixelBuffer(width, height, kind);
            try
            {
                template.Span.CopyTo(pixels.Span);
                var stripe = (int)(sequence * 37 % (width - 64));
                for (var y = 0; y < height; y++) pixels.Span.Slice(y * pixels.Stride + stripe * Info.BytesPerPixel, 64 * Info.BytesPerPixel).Fill(240);
            }
            catch { pixels.Dispose(); throw; }
            yield return new(++sequence, Stopwatch.GetTimestamp(), pixels);
        }
    }
    private static void BuildTemplate(PixelBuffer image)
    {
        for (var y = 0; y < image.Height; y++)
        {
            var row = image.Span.Slice(y * image.Stride, image.Stride);
            for (var x = 0; x < image.Width; x++)
            {
                var intensity = (byte)(32 + ((x / 128 + y / 128) % 2) * 80 + (x % 128) / 2);
                if (image.Kind == PixelKind.Gray8) row[x] = intensity;
                else if (image.Kind == PixelKind.Gray16) MemoryMarshal.Cast<byte, ushort>(row)[x] = (ushort)(intensity * 257);
                else { var offset = x * 4; row[offset] = intensity; row[offset + 1] = (byte)(intensity / 2); row[offset + 2] = (byte)(240 - intensity); row[offset + 3] = 255; }
            }
        }
    }
}
