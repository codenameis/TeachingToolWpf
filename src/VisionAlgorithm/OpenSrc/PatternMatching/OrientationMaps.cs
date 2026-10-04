namespace VisionAlgorithm.OpenSrc.PatternMatching;

/// <summary>P1 §2.3–2.5 기반 독립 구현. 8개 unsigned 방향, 중심 확산, 0..100 cosine LUT.</summary>
internal sealed class OrientationMaps
{
    private static readonly byte[] s_responses = BuildLookup();
    private readonly byte[][] _linear;
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

    private OrientationMaps(int width, int height, int step)
    {
        Width = width;
        Height = height;
        Step = step;
        Columns = (width + step - 1) / step;
        Rows = (height + step - 1) / step;
        _linear = new byte[8][];
        for (var bin = 0; bin < 8; bin++)
        {
            _linear[bin] = new byte[checked(step * step * PlaneLength)];
        }
    }

    public static int Quantize(double x, double y)
        => (((int)Math.Round(Math.Atan2(y, x) * 8 / Math.PI) % 8) + 8) % 8;

    public static OrientationMaps Create(byte[] bits, int width, int height, int radius, int step, CancellationToken token = default)
    {
        var map = new OrientationMaps(width, height, step);
        var horizontal = new byte[bits.Length];
        for (var y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                byte value = 0;
                for (var dx = Math.Max(0, x - radius); dx <= Math.Min(width - 1, x + radius); dx++)
                {
                    value |= bits[(y * width) + dx];
                }
                horizontal[(y * width) + x] = value;
            }
        }
        for (var y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                byte value = 0;
                for (var dy = Math.Max(0, y - radius); dy <= Math.Min(height - 1, y + radius); dy++)
                {
                    value |= horizontal[(dy * width) + x];
                }
                var offset = map.Offset(x, y);
                for (var bin = 0; bin < 8; bin++)
                {
                    map._linear[bin][offset] = s_responses[(bin * 256) + value];
                }
            }
        }
        return map;
    }

    public byte At(int bin, int x, int y)
        => x < 0 || y < 0 || x >= Width || y >= Height ? (byte)0 : _linear[bin][Offset(x, y)];

    public ReadOnlySpan<byte> Row(int bin, int x, int y, int count)
        => _linear[bin].AsSpan(Offset(x, y), count);

    private int Offset(int x, int y)
        => (((y % Step * Step) + (x % Step)) * PlaneLength) + (y / Step * Columns) + (x / Step);

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
