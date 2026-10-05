namespace TeachingToolWpf.Imaging;

/// <summary>표시 RGBA와 원본 Gray8이 공유하는 샘플의 형상·색 정의. 뒤 구간이 앞 구간을 덮는다.</summary>
internal static class ProceduralPattern
{
    public const int Period = 2048;
    public const uint Background = 0xff535328;
    internal readonly record struct Segment(int Start, int End, uint Color);

    public static int WriteRow(int y, Span<Segment> segments)
    {
        var count = 0;
        segments[count++] = new(y - 11, y + 12, 0xff74855f);
        segments[count++] = new(Period - y - 11, Period - y + 12, 0xff74855f);
        if (Math.Min(y, Period - y) < 24)
        {
            segments[count++] = new(0, Period, 0xff2c2514);
        }
        else
        {
            segments[count++] = new(0, 24, 0xff2c2514);
            segments[count++] = new(2025, Period, 0xff2c2514);
        }
        if ((y > 490 && y < 640) || (y > 1400 && y < 1550))
        {
            for (var pin = 1; pin < 10; pin++)
            {
                segments[count++] = new(Math.Max(251, pin * 180), Math.Min(1800, (pin * 180) + 80), 0xff87b5c3);
            }
        }
        if (y > 640 && y < 1400)
        {
            segments[count++] = new(361, 1680, 0xff392b24);
        }
        return count;
    }

    public static byte Gray(uint color)
        => (byte)(((77 * (color & 255)) + (150 * ((color >> 8) & 255)) + (29 * ((color >> 16) & 255)) + 128) >> 8);
}
