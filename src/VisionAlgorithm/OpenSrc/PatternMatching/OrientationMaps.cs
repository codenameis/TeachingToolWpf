using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

/// <summary>P1 §2.3–2.5 기반 독립 구현. 8개 unsigned 방향, 중심 확산, 0..100 cosine LUT.</summary>
internal sealed partial class OrientationMaps
{
    private static readonly byte[] s_responses = BuildLookup();
    private readonly byte[][] _linear;
    private readonly byte[] _spread;
    private readonly int _stepShift;
    public int Width
    {
        get;
    }
    public int Height
    {
        get;
    }
    public int Step
    {
        get;
    }
    public int Columns
    {
        get;
    }
    public int Rows
    {
        get;
    }
    public int PlaneLength => Columns * Rows;
    public bool HasLinearResponses => _linear.Length != 0;
    public long EstimatedBytes => _linear.Sum(plane => plane.LongLength) + _spread.LongLength;

    private OrientationMaps(int width, int height, int step, bool createLinearResponses)
    {
        Width = width;
        Height = height;
        Step = step;
        _stepShift = (step & (step - 1)) == 0 ? BitOperations.Log2((uint)step) : -1;
        Columns = (width + step - 1) / step;
        Rows = (height + step - 1) / step;
        _spread = new byte[checked(width * height)];
        _linear = createLinearResponses ? new byte[8][] : [];
        for (var bin = 0; bin < _linear.Length; bin++)
        {
            _linear[bin] = new byte[checked(step * step * PlaneLength)];
        }
    }

    public static int Quantize(double x, double y)
        => (((int)Math.Round(Math.Atan2(y, x) * 8 / Math.PI) % 8) + 8) % 8;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static OrientationMaps Create(byte[] bits, int width, int height, int radius, int step,
        CancellationToken token = default, bool createLinearResponses = true)
    {
        var map = new OrientationMaps(width, height, step, createLinearResponses);
        var horizontal = new byte[bits.Length];
        for (var y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            var x = 0;
            for (; x < Math.Min(radius, width); x++)
            {
                horizontal[(y * width) + x] = SpreadHorizontal(bits, width, y, x, radius);
            }
            if (Vector.IsHardwareAccelerated)
            {
                for (; x <= width - radius - Vector<byte>.Count; x += Vector<byte>.Count)
                {
                    var value = Vector<byte>.Zero;
                    for (var dx = -radius; dx <= radius; dx++)
                    {
                        value |= new Vector<byte>(bits, (y * width) + x + dx);
                    }
                    value.CopyTo(horizontal, (y * width) + x);
                }
            }
            for (; x < width; x++)
            {
                horizontal[(y * width) + x] = SpreadHorizontal(bits, width, y, x, radius);
            }
        }
        var spread = map._spread;
        for (var y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            var x = 0;
            if (Vector.IsHardwareAccelerated)
            {
                for (; x <= width - Vector<byte>.Count; x += Vector<byte>.Count)
                {
                    var value = Vector<byte>.Zero;
                    for (var dy = Math.Max(0, y - radius); dy <= Math.Min(height - 1, y + radius); dy++)
                    {
                        value |= new Vector<byte>(horizontal, (dy * width) + x);
                    }
                    value.CopyTo(spread, (y * width) + x);
                }
            }
            for (; x < width; x++)
            {
                byte value = 0;
                for (var dy = Math.Max(0, y - radius); dy <= Math.Min(height - 1, y + radius); dy++)
                {
                    value |= horizontal[(dy * width) + x];
                }
                spread[(y * width) + x] = value;
            }
        }
        if (!createLinearResponses)
        {
            // GPU 후보 점수는 원본 방향 bit를 소비하고 CPU 국소 탐색은 raster 확산/LUT만 읽는다.
            // 사용하지 않는 CPU 누적용 phase 배열과 8개 선형 응답 평면을 생성하지 않는다.
            token.ThrowIfCancellationRequested();
            return map;
        }
        // 나머지 연산을 픽셀마다 반복하지 않고 각 phase를 연속 버퍼로 한 번 정렬한다.
        var linearBits = new byte[map._linear[0].Length];
        for (var phaseY = 0; phaseY < step; phaseY++)
        {
            for (var phaseX = 0; phaseX < step; phaseX++)
            {
                for (var y = phaseY; y < height; y += step)
                {
                    token.ThrowIfCancellationRequested();
                    var target = (((phaseY * step) + phaseX) * map.PlaneLength) + (y / step * map.Columns);
                    for (var x = phaseX; x < width; x += step)
                    {
                        linearBits[target++] = spread[(y * width) + x];
                    }
                }
            }
        }
        for (var bin = 0; bin < 8; bin++)
        {
            token.ThrowIfCancellationRequested();
            FillResponses(linearBits, map._linear[bin], bin, token);
        }
        return map;
    }

    private static byte SpreadHorizontal(byte[] bits, int width, int y, int x, int radius)
    {
        byte value = 0;
        for (var dx = Math.Max(0, x - radius); dx <= Math.Min(width - 1, x + radius); dx++)
        {
            value |= bits[(y * width) + dx];
        }
        return value;
    }

    private static void FillResponses(byte[] bits, byte[] output, int bin, CancellationToken token)
    {
        var index = 0;
        var start = bin * 256;
        if (Avx2.IsSupported)
        {
            Span<byte> lower = stackalloc byte[32];
            Span<byte> upper = stackalloc byte[32];
            for (var value = 0; value < 16; value++)
            {
                lower[value] = lower[value + 16] = s_responses[start + value];
                upper[value] = upper[value + 16] = s_responses[start + (value << 4)];
            }
            var lowerLookup = Vector256.Create(lower);
            var upperLookup = Vector256.Create(upper);
            var mask = Vector256.Create((byte)15);
            for (; index <= bits.Length - Vector256<byte>.Count; index += Vector256<byte>.Count)
            {
                if ((index & 65535) == 0)
                {
                    token.ThrowIfCancellationRequested();
                }
                var values = Vector256.Create(bits.AsSpan(index));
                var low = Avx2.Shuffle(lowerLookup, values & mask);
                var high = Avx2.Shuffle(upperLookup, Avx2.ShiftRightLogical(values.AsUInt16(), 4).AsByte() & mask);
                Avx2.Max(low, high).CopyTo(output.AsSpan(index));
            }
        }
        for (; index < bits.Length; index++)
        {
            if ((index & 65535) == 0)
            {
                token.ThrowIfCancellationRequested();
            }
            output[index] = s_responses[start + bits[index]];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte At(int bin, int x, int y)
        => x < 0 || y < 0 || x >= Width || y >= Height ? (byte)0 : s_responses[(bin * 256) + _spread[(y * Width) + x]];

    public ReadOnlySpan<byte> Row(int bin, int x, int y, int count)
    {
        if (!HasLinearResponses)
        {
            throw new InvalidOperationException("GPU 후보용 응답맵에는 CPU 누적 검색용 선형 응답이 없습니다.");
        }
        return _linear[bin].AsSpan(Offset(x, y), count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Offset(int x, int y)
    {
        if (_stepShift >= 0)
        {
            var mask = Step - 1;
            return ((((y & mask) << _stepShift) + (x & mask)) * PlaneLength) + ((y >> _stepShift) * Columns) + (x >> _stepShift);
        }
        return (((y % Step * Step) + (x % Step)) * PlaneLength) + (y / Step * Columns) + (x / Step);
    }

    private static byte[] BuildLookup()
    {
        var result = new byte[8 * 256];
        for (var bin = 0; bin < 8; bin++)
        {
            for (var bits = 1; bits < 256; bits++)
            {
                var best = 0d;
                for (var other = 0; other < 8; other++)
                {
                    if ((bits & (1 << other)) != 0)
                    {
                        best = Math.Max(best, Math.Abs(Math.Cos((bin - other) * Math.PI / 8)));
                    }
                }
                result[(bin * 256) + bits] = (byte)Math.Round(best * 100);
            }
        }
        return result;
    }
}
