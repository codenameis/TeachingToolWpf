namespace VisionAlgorithm.OpenSrc.PatternMatching;

internal readonly record struct EdgePoint(double X, double Y, double NormalX, double NormalY, float Strength);

internal sealed class GradientImage
{
    public int Width
    {
        get;
    }
    public int Height
    {
        get;
    }
    public float[] X
    {
        get;
    }
    public float[] Y
    {
        get;
    }
    public float[] Magnitude
    {
        get;
    }
    public byte[] Bits
    {
        get;
    }
    private readonly int[] _edgeIndex;
    public EdgePoint[] EdgePoints { get; private set; } = [];
    public long EstimatedBytes => ((long)Width * Height * 13) + (_edgeIndex.LongLength * 4) + (EdgePoints.LongLength * 40);

    public EdgePoint? EdgeAt(int x, int y)
    {
        var index = _edgeIndex.Length == 0 ? 0 : _edgeIndex[(y * Width) + x];
        return index == 0 ? null : EdgePoints[index - 1];
    }

    private GradientImage(int width, int height, GradientPlanes? planes = null, bool extractEdges = true)
    {
        Width = width;
        Height = height;
        var count = checked(width * height);
        X = planes?.X ?? new float[count];
        Y = planes?.Y ?? new float[count];
        Magnitude = planes?.Magnitude ?? new float[count];
        Bits = planes?.Bits ?? new byte[count];
        _edgeIndex = extractEdges ? new int[count] : [];
    }

    public static GradientImage CreateTiled(GrayImage image, float threshold, CancellationToken token,
        IImagePreprocessor? preprocessor = null, bool extractEdges = true)
    {
        const int Interior = 128;
        const int Halo = 4;
        var output = new GradientImage(image.Width, image.Height, extractEdges: extractEdges);
        var edges = new List<EdgePoint>();
        for (var top = 0; top < image.Height; top += Interior)
        {
            for (var left = 0; left < image.Width; left += Interior)
            {
                token.ThrowIfCancellationRequested();
                var x0 = Math.Max(0, left - Halo);
                var y0 = Math.Max(0, top - Halo);
                var x1 = Math.Min(image.Width, left + Interior + Halo);
                var y1 = Math.Min(image.Height, top + Interior + Halo);
                var pixels = image.Pixels.Slice((y0 * image.Stride) + x0);
                var tile = Create(new(pixels, x1 - x0, y1 - y0, image.Stride), threshold, token, preprocessor, extractEdges);
                for (var y = top; y < Math.Min(image.Height, top + Interior); y++)
                {
                    for (var x = left; x < Math.Min(image.Width, left + Interior); x++)
                    {
                        var source = ((y - y0) * tile.Width) + x - x0;
                        var target = (y * image.Width) + x;
                        output.X[target] = tile.X[source];
                        output.Y[target] = tile.Y[source];
                        output.Magnitude[target] = tile.Magnitude[source];
                        output.Bits[target] = tile.Bits[source];
                        if (extractEdges && tile.EdgeAt(x - x0, y - y0) is { } edge)
                        {
                            edges.Add(edge with
                            {
                                X = edge.X + x0,
                                Y = edge.Y + y0
                            });
                            output._edgeIndex[target] = edges.Count;
                        }
                    }
                }
            }
        }
        output.EdgePoints = edges.ToArray();
        return output;
    }

    public static GradientImage Create(GrayImage image, float threshold, CancellationToken token,
        IImagePreprocessor? preprocessor = null, bool extractEdges = true)
    {
        token.ThrowIfCancellationRequested();
        GradientImage result;
        if (preprocessor is null)
        {
            result = CreateCpuGradient(image, threshold, token, extractEdges);
        }
        else
        {
            var planes = preprocessor.Prepare(image, threshold, token);
            var count = checked(image.Width * image.Height);
            if (planes?.X?.Length != count || planes.Y?.Length != count ||
                planes.Magnitude?.Length != count || planes.Bits?.Length != count)
            {
                throw new InvalidOperationException("전처리 공급자가 영상 크기와 다른 gradient 배열을 반환했습니다.");
            }
            result = new(image.Width, image.Height, planes, extractEdges);
        }
        token.ThrowIfCancellationRequested();
        if (!extractEdges)
        {
            return result;
        }
        var edges = new List<EdgePoint>();
        for (var y = 2; y < image.Height - 2; y++)
        {
            token.ThrowIfCancellationRequested();
            for (var x = 2; x < image.Width - 2; x++)
            {
                var i = (y * image.Width) + x;
                if (result.Bits[i] == 0)
                {
                    continue;
                }
                var nx = result.X[i];
                var ny = result.Y[i];
                var a = Sample(result.Magnitude, image.Width, image.Height, (double)x - nx, (double)y - ny);
                var b = result.Magnitude[i];
                var c = Sample(result.Magnitude, image.Width, image.Height, (double)x + nx, (double)y + ny);
                if (b < a || b < c)
                {
                    continue;
                }
                var denominator = a - (2 * b) + c;
                var offset = Math.Abs(denominator) > 1e-6 ? Math.Clamp(0.5 * (a - c) / denominator, -0.5, 0.5) : 0;
                edges.Add(new(x + 0.5 + (nx * offset), y + 0.5 + (ny * offset), nx, ny, b));
                result._edgeIndex[i] = edges.Count;
            }
        }
        result.EdgePoints = edges.ToArray();
        return result;
    }

    private static GradientImage CreateCpuGradient(GrayImage image, float threshold, CancellationToken token, bool extractEdges)
    {
        var result = new GradientImage(image.Width, image.Height, extractEdges: extractEdges);
        var planes = new GradientPlanes(result.X, result.Y, result.Magnitude, result.Bits);
        var smooth = Smooth(image, token);
        for (var y = 1; y < image.Height - 1; y++)
        {
            token.ThrowIfCancellationRequested();
            for (var x = 1; x < image.Width - 1; x++)
            {
                var i = (y * image.Width) + x;
                var w = image.Width;
                var dx = (smooth[i - w + 1] + (2 * smooth[i + 1]) + smooth[i + w + 1] -
                    smooth[i - w - 1] - (2 * smooth[i - 1]) - smooth[i + w - 1]) / 8;
                var dy = (smooth[i + w - 1] + (2 * smooth[i + w]) + smooth[i + w + 1] -
                    smooth[i - w - 1] - (2 * smooth[i - w]) - smooth[i - w + 1]) / 8;
                GradientPlanes.NormalizeSample(planes, i, dx, dy, threshold);
            }
        }
        return result;
    }

    private static float[] Smooth(GrayImage image, CancellationToken token)
    {
        var horizontal = new float[checked(image.Width * image.Height)];
        var smooth = new float[horizontal.Length];
        ReadOnlySpan<int> kernel = [1, 4, 6, 4, 1];
        var pixels = image.Pixels.Span;
        for (var y = 0; y < image.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (var x = 0; x < image.Width; x++)
            {
                var sum = 0f;
                for (var k = -2; k <= 2; k++)
                {
                    sum += kernel[k + 2] * pixels[(y * image.Stride) + Math.Clamp(x + k, 0, image.Width - 1)];
                }
                horizontal[(y * image.Width) + x] = sum / 16;
            }
        }
        for (var y = 0; y < image.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (var x = 0; x < image.Width; x++)
            {
                var sum = 0f;
                for (var k = -2; k <= 2; k++)
                {
                    sum += kernel[k + 2] * horizontal[(Math.Clamp(y + k, 0, image.Height - 1) * image.Width) + x];
                }
                smooth[(y * image.Width) + x] = sum / 16;
            }
        }
        return smooth;
    }

    internal static float Sample(float[] values, int width, int height, double x, double y)
    {
        if (x < 0 || y < 0 || x > width - 1 || y > height - 1)
        {
            return 0;
        }
        var ix = (int)x;
        var iy = (int)y;
        var jx = Math.Min(ix + 1, width - 1);
        var jy = Math.Min(iy + 1, height - 1);
        var tx = x - ix;
        var ty = y - iy;
        return (float)((values[(iy * width) + ix] * (1 - tx) * (1 - ty)) +
            (values[(iy * width) + jx] * tx * (1 - ty)) +
            (values[(jy * width) + ix] * (1 - tx) * ty) + (values[(jy * width) + jx] * tx * ty));
    }

    public static GrayImage Reduce(GrayImage image)
    {
        var width = Math.Max(1, image.Width / 2);
        var height = Math.Max(1, image.Height / 2);
        var output = new byte[checked(width * height)];
        var input = image.Pixels.Span;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var a = (2 * y * image.Stride) + (2 * x);
                var b = Math.Min((2 * y) + 1, image.Height - 1) * image.Stride;
                var right = Math.Min((2 * x) + 1, image.Width - 1);
                output[(y * width) + x] = (byte)((input[a] + input[(2 * y * image.Stride) + right] +
                    input[b + (2 * x)] + input[b + right] + 2) / 4);
            }
        }
        return new(output, width, height, width);
    }
}
