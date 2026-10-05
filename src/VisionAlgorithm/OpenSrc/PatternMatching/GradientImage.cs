using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

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
    internal double DirectionLengthUpperBound
    {
        get; private set;
    }
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
                output.DirectionLengthUpperBound = Math.Max(output.DirectionLengthUpperBound, tile.DirectionLengthUpperBound);
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

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
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
        result.DirectionLengthUpperBound = result.MeasureDirectionLengthUpperBound(token);
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

    private double MeasureDirectionLengthUpperBound(CancellationToken token)
    {
        var maximum = 0f;
        var index = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var largest = Vector<float>.Zero;
            for (; index <= X.Length - Vector<float>.Count; index += Vector<float>.Count)
            {
                if ((index & 65535) == 0)
                {
                    token.ThrowIfCancellationRequested();
                }
                var x = new Vector<float>(X, index);
                var y = new Vector<float>(Y, index);
                var squared = (x * x) + (y * y);
                if (!Vector.EqualsAll(squared, squared))
                {
                    return double.PositiveInfinity;
                }
                largest = Vector.Max(largest, squared);
            }
            for (var lane = 0; lane < Vector<float>.Count; lane++)
            {
                maximum = Math.Max(maximum, largest[lane]);
            }
        }
        for (; index < X.Length; index++)
        {
            maximum = Math.Max(maximum, (X[index] * X[index]) + (Y[index] * Y[index]));
        }
        // 제곱·합·보간의 float 반올림보다 넓은 상한이다. 공급자 값도 실제 배열에서 측정한다.
        return float.IsFinite(maximum) ? (Math.Sqrt(maximum) * 1.000001) + 1e-12 : double.PositiveInfinity;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal void SampleDirection(double x, double y, out float gradientX, out float gradientY)
    {
        if (x < 0 || y < 0 || x > Width - 1 || y > Height - 1)
        {
            gradientX = 0;
            gradientY = 0;
            return;
        }
        var ix = (int)x;
        var iy = (int)y;
        var jx = Math.Min(ix + 1, Width - 1);
        var jy = Math.Min(iy + 1, Height - 1);
        var tx = x - ix;
        var ty = y - iy;
        var inverseX = 1 - tx;
        var inverseY = 1 - ty;
        var topLeft = (iy * Width) + ix;
        var topRight = (iy * Width) + jx;
        var bottomLeft = (jy * Width) + ix;
        var bottomRight = (jy * Width) + jx;
        // Sample의 곱·합 순서를 유지한다. 가중치를 미리 곱하면 float 반환값이 달라질 수 있다.
        gradientX = (float)((X[topLeft] * inverseX * inverseY) + (X[topRight] * tx * inverseY) +
            (X[bottomLeft] * inverseX * ty) + (X[bottomRight] * tx * ty));
        gradientY = (float)((Y[topLeft] * inverseX * inverseY) + (Y[topRight] * tx * inverseY) +
            (Y[bottomLeft] * inverseX * ty) + (Y[bottomRight] * tx * ty));
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static GradientImage CreateCpuGradient(GrayImage image, float threshold, CancellationToken token, bool extractEdges)
    {
        var result = new GradientImage(image.Width, image.Height, extractEdges: extractEdges);
        if (image.Width < 3 || image.Height < 3)
        {
            return result;
        }
        var planes = new GradientPlanes(result.X, result.Y, result.Magnitude, result.Bits);
        var horizontalRows = Math.Min(5, image.Height);
        var horizontal = new float[checked(image.Width * horizontalRows)];
        var smooth = new float[checked(image.Width * 3)];
        var pixels = image.Pixels.Span;
        var nextHorizontal = 0;
        for (var smoothY = 0; smoothY < image.Height; smoothY++)
        {
            token.ThrowIfCancellationRequested();
            while (nextHorizontal <= Math.Min(image.Height - 1, smoothY + 2))
            {
                SmoothHorizontalRow(pixels.Slice(nextHorizontal * image.Stride, image.Width),
                    horizontal.AsSpan(nextHorizontal % horizontalRows * image.Width, image.Width));
                nextHorizontal++;
            }
            SmoothVerticalRow(horizontal, horizontalRows, image.Width, image.Height, smoothY,
                smooth.AsSpan(smoothY % 3 * image.Width, image.Width));
            if (smoothY >= 2)
            {
                // Gaussian 5행과 Sobel 3행만 유지한다. 새 행은 마지막 소비가 끝난 슬롯을 덮어쓴다.
                ApplySobelRow(smooth.AsSpan((smoothY - 2) % 3 * image.Width, image.Width),
                    smooth.AsSpan((smoothY - 1) % 3 * image.Width, image.Width),
                    smooth.AsSpan(smoothY % 3 * image.Width, image.Width), planes,
                    (smoothY - 1) * image.Width, threshold);
            }
        }
        return result;
    }

    private static void ApplySobelRow(ReadOnlySpan<float> top, ReadOnlySpan<float> middle, ReadOnlySpan<float> bottom,
        GradientPlanes output, int rowOffset, float threshold)
    {
        var x = 1;
        if (Vector.IsHardwareAccelerated)
        {
            for (; x <= middle.Length - 1 - Vector<float>.Count; x += Vector<float>.Count)
            {
                var i = rowOffset + x;
                var dx = (new Vector<float>(top.Slice(x + 1)) + (2 * new Vector<float>(middle.Slice(x + 1))) +
                    new Vector<float>(bottom.Slice(x + 1)) - new Vector<float>(top.Slice(x - 1)) -
                    (2 * new Vector<float>(middle.Slice(x - 1))) - new Vector<float>(bottom.Slice(x - 1))) / 8;
                var dy = (new Vector<float>(bottom.Slice(x - 1)) + (2 * new Vector<float>(bottom.Slice(x))) +
                    new Vector<float>(bottom.Slice(x + 1)) - new Vector<float>(top.Slice(x - 1)) -
                    (2 * new Vector<float>(top.Slice(x))) - new Vector<float>(top.Slice(x + 1))) / 8;
                var magnitude = Vector.SquareRoot((dx * dx) + (dy * dy));
                var supported = Vector.GreaterThanOrEqual(magnitude, new Vector<float>(threshold));
                magnitude.CopyTo(output.Magnitude, i);
                Vector.ConditionalSelect(supported, dx / magnitude, Vector<float>.Zero).CopyTo(output.X, i);
                Vector.ConditionalSelect(supported, dy / magnitude, Vector<float>.Zero).CopyTo(output.Y, i);
                for (var lane = 0; lane < Vector<float>.Count; lane++)
                {
                    if (magnitude[lane] >= threshold)
                    {
                        output.Bits[i + lane] = (byte)(1 << OrientationMaps.Quantize(dx[lane], dy[lane]));
                    }
                }
            }
        }
        for (; x < middle.Length - 1; x++)
        {
            var dx = (top[x + 1] + (2 * middle[x + 1]) + bottom[x + 1] -
                top[x - 1] - (2 * middle[x - 1]) - bottom[x - 1]) / 8;
            var dy = (bottom[x - 1] + (2 * bottom[x]) + bottom[x + 1] -
                top[x - 1] - (2 * top[x]) - top[x + 1]) / 8;
            GradientPlanes.NormalizeSample(output, rowOffset + x, dx, dy, threshold);
        }
    }

    private static void SmoothHorizontalRow(ReadOnlySpan<byte> source, Span<float> target)
    {
        var x = 0;
        for (; x < Math.Min(2, source.Length); x++)
        {
            target[x] = SmoothHorizontalBoundary(source, x);
        }
        if (Vector.IsHardwareAccelerated)
        {
            for (; x <= source.Length - 2 - Vector<byte>.Count; x += Vector<byte>.Count)
            {
                Vector.Widen(new Vector<byte>(source.Slice(x - 2)), out Vector<ushort> a0, out Vector<ushort> a1);
                Vector.Widen(new Vector<byte>(source.Slice(x - 1)), out Vector<ushort> b0, out Vector<ushort> b1);
                Vector.Widen(new Vector<byte>(source.Slice(x)), out Vector<ushort> c0, out Vector<ushort> c1);
                Vector.Widen(new Vector<byte>(source.Slice(x + 1)), out Vector<ushort> d0, out Vector<ushort> d1);
                Vector.Widen(new Vector<byte>(source.Slice(x + 2)), out Vector<ushort> e0, out Vector<ushort> e1);
                // 정수 합의 최댓값은 4080이며 기존 float 5-tap과 정확히 같은 값이다.
                StoreHorizontal((a0 + (b0 * 4) + (c0 * 6) + (d0 * 4) + e0), target.Slice(x));
                StoreHorizontal((a1 + (b1 * 4) + (c1 * 6) + (d1 * 4) + e1), target.Slice(x + Vector<ushort>.Count));
            }
        }
        for (; x < source.Length - 2; x++)
        {
            target[x] = (source[x - 2] + (4 * source[x - 1]) + (6 * source[x]) + (4 * source[x + 1]) + source[x + 2]) / 16f;
        }
        for (; x < source.Length; x++)
        {
            target[x] = SmoothHorizontalBoundary(source, x);
        }
    }

    private static void SmoothVerticalRow(float[] horizontal, int horizontalRows, int width, int height, int y,
        Span<float> target)
    {
        var row0 = Math.Max(0, y - 2) % horizontalRows * width;
        var row1 = Math.Max(0, y - 1) % horizontalRows * width;
        var row2 = y % horizontalRows * width;
        var row3 = Math.Min(height - 1, y + 1) % horizontalRows * width;
        var row4 = Math.Min(height - 1, y + 2) % horizontalRows * width;
        var x = 0;
        if (Vector.IsHardwareAccelerated)
        {
            for (; x <= width - Vector<float>.Count; x += Vector<float>.Count)
            {
                var sum = new Vector<float>(horizontal, row0 + x) + (4 * new Vector<float>(horizontal, row1 + x));
                sum += 6 * new Vector<float>(horizontal, row2 + x);
                sum += 4 * new Vector<float>(horizontal, row3 + x);
                sum += new Vector<float>(horizontal, row4 + x);
                (sum / 16).CopyTo(target.Slice(x));
            }
        }
        for (; x < width; x++)
        {
            target[x] = (horizontal[row0 + x] + (4 * horizontal[row1 + x]) +
                (6 * horizontal[row2 + x]) + (4 * horizontal[row3 + x]) + horizontal[row4 + x]) / 16;
        }
    }

    private static float SmoothHorizontalBoundary(ReadOnlySpan<byte> row, int x)
        => (row[Math.Max(0, x - 2)] + (4 * row[Math.Max(0, x - 1)]) + (6 * row[x]) +
            (4 * row[Math.Min(row.Length - 1, x + 1)]) + row[Math.Min(row.Length - 1, x + 2)]) / 16f;

    private static void StoreHorizontal(Vector<ushort> sum, Span<float> output)
    {
        Vector.Widen(sum, out Vector<uint> low, out Vector<uint> high);
        (Vector.ConvertToSingle(low) / 16).CopyTo(output);
        (Vector.ConvertToSingle(high) / 16).CopyTo(output.Slice(Vector<float>.Count));
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
            var x = 0;
            var top = 2 * y * image.Stride;
            var bottom = Math.Min((2 * y) + 1, image.Height - 1) * image.Stride;
            if (Sse2.IsSupported)
            {
                for (; x <= width - 8; x += 8)
                {
                    var upper = Vector128.Create(input.Slice(top + (2 * x)));
                    var lower = Vector128.Create(input.Slice(bottom + (2 * x)));
                    var left = Sse2.Add(Sse2.UnpackLow(upper, Vector128<byte>.Zero).AsInt16(),
                        Sse2.UnpackLow(lower, Vector128<byte>.Zero).AsInt16());
                    var right = Sse2.Add(Sse2.UnpackHigh(upper, Vector128<byte>.Zero).AsInt16(),
                        Sse2.UnpackHigh(lower, Vector128<byte>.Zero).AsInt16());
                    var leftPairs = Sse2.MultiplyAddAdjacent(left, Vector128.Create((short)1));
                    var rightPairs = Sse2.MultiplyAddAdjacent(right, Vector128.Create((short)1));
                    leftPairs = Sse2.ShiftRightLogical(leftPairs + Vector128.Create(2), 2);
                    rightPairs = Sse2.ShiftRightLogical(rightPairs + Vector128.Create(2), 2);
                    var values = Sse2.PackUnsignedSaturate(Sse2.PackSignedSaturate(leftPairs, rightPairs), Vector128<short>.Zero);
                    var packed = values.AsUInt64().GetElement(0);
                    MemoryMarshal.Write(output.AsSpan((y * width) + x), in packed);
                }
            }
            for (; x < width; x++)
            {
                var a = top + (2 * x);
                var right = Math.Min((2 * x) + 1, image.Width - 1);
                output[(y * width) + x] = (byte)((input[a] + input[top + right] +
                    input[bottom + (2 * x)] + input[bottom + right] + 2) / 4);
            }
        }
        return new(output, width, height, width);
    }
}
