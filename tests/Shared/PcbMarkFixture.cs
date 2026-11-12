using VisionAlgorithm.OpenSrc.ShapeDetection;

namespace VisionAlgorithm.Tests;

internal static class PcbMarkFixture
{
    public static ShapeGrayImage Create(PrimitiveShapeKind kind, double angle = 0, double scale = 1, bool dark = false,
        int rays = 6, Func<double, double, bool>? contains = null)
    {
        const int Size = 240;
        const int Stride = 247;
        var pixels = new byte[Size * Stride];
        var cosine = Math.Cos(angle * Math.PI / 180);
        var sine = Math.Sin(angle * Math.PI / 180);
        var random = new Random(356);
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var dx = (x + .5 - 120) / scale;
                var dy = (y + .5 - 120) / scale;
                var localX = (cosine * dx) + (sine * dy);
                var localY = (-sine * dx) + (cosine * dy);
                var inside = contains?.Invoke(localX, localY) ?? Contains(kind, localX, localY, rays);
                pixels[(y * Stride) + x] = (byte)((inside != dark ? 225 : 25) + random.Next(-6, 7));
            }
        }
        return new(pixels, Size, Size, Stride);
    }

    public static bool Contains(PrimitiveShapeKind kind, double x, double y, int rays = 6) => kind switch
    {
        PrimitiveShapeKind.Diamond => (Math.Abs(x) / 48) + (Math.Abs(y) / 32) <= 1,
        PrimitiveShapeKind.CrossHash => Math.Abs(x) <= 48 && Math.Abs(y) <= 48 &&
            (Math.Abs(x) is >= 13 and <= 27 || Math.Abs(y) is >= 13 and <= 27),
        PrimitiveShapeKind.DoubleSquare => (Math.Abs(x - 19) <= 17 && Math.Abs(y + 19) <= 17) ||
            (Math.Abs(x + 19) <= 17 && Math.Abs(y - 19) <= 17),
        PrimitiveShapeKind.Star => InStar(x, y),
        PrimitiveShapeKind.Asterisk => InAsterisk(x, y, rays),
        _ => false
    };

    private static bool InAsterisk(double x, double y, int rays)
    {
        for (var ray = 0; ray < rays / 2; ray++)
        {
            var cosine = Math.Cos(ray * 2 * Math.PI / rays);
            var sine = Math.Sin(ray * 2 * Math.PI / rays);
            if (Math.Abs((x * cosine) + (y * sine)) <= 48 && Math.Abs((-x * sine) + (y * cosine)) <= 8)
            {
                return true;
            }
        }
        return false;
    }

    private static bool InStar(double x, double y)
    {
        var angle = Math.Atan2(y, x);
        var sector = (int)Math.Floor((angle + (2 * Math.PI)) / (Math.PI / 5));
        var firstRadius = sector % 2 == 0 ? 48 : 21;
        var secondRadius = sector % 2 == 0 ? 21 : 48;
        var firstAngle = sector * Math.PI / 5;
        var secondAngle = (sector + 1) * Math.PI / 5;
        var ax = firstRadius * Math.Cos(firstAngle);
        var ay = firstRadius * Math.Sin(firstAngle);
        var bx = secondRadius * Math.Cos(secondAngle);
        var by = secondRadius * Math.Sin(secondAngle);
        return ((bx - ax) * (y - ay)) - ((by - ay) * (x - ax)) >= 0;
    }
}
