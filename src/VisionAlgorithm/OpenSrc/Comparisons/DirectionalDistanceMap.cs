using VisionAlgorithm.OpenSrc.PatternMatching;

namespace VisionAlgorithm.OpenSrc.Comparisons;

/// <summary>E1 식 5–12. 8개 격자 방향의 Euclidean 공간 거리 + 원형 방향 비용 + 선분 prefix.</summary>
internal sealed class DirectionalDistanceMap
{
    internal static readonly (int X, int Y)[] s_directions = [(1, 0), (2, 1), (1, 1), (1, 2), (0, 1), (-1, 2), (-1, 1), (-2, 1)];
    private readonly double[][] _distance;
    private readonly double[][] _integrals;
    public int Width
    {
        get;
    }
    public int Height
    {
        get;
    }

    public DirectionalDistanceMap(int width, int height, IEnumerable<LineSegment> lines, double directionWeight, CancellationToken token)
    {
        Width = width;
        Height = height;
        _distance = new double[8][];
        _integrals = new double[8][];
        for (var bin = 0; bin < 8; bin++)
        {
            _distance[bin] = Enumerable.Repeat(1e12, width * height).ToArray();
        }
        foreach (var line in lines)
        {
            var bin = Bin(line.Angle);
            var length = Math.Max(1, (int)Math.Ceiling(line.Length));
            for (var i = 0; i <= length; i++)
            {
                var x = (int)Math.Round(line.Start.X + ((line.End.X - line.Start.X) * i / length) - 0.5);
                var y = (int)Math.Round(line.Start.Y + ((line.End.Y - line.Start.Y) * i / length) - 0.5);
                if (x >= 0 && y >= 0 && x < width && y < height)
                {
                    _distance[bin][(y * width) + x] = 0;
                }
            }
        }
        foreach (var plane in _distance)
        {
            var column = new double[height];
            for (var x = 0; x < width; x++)
            {
                token.ThrowIfCancellationRequested();
                for (var y = 0; y < height; y++)
                {
                    column[y] = plane[(y * width) + x];
                }
                var transformed = SquaredDistance(column);
                for (var y = 0; y < height; y++)
                {
                    plane[(y * width) + x] = transformed[y];
                }
            }
            for (var y = 0; y < height; y++)
            {
                var transformed = SquaredDistance(plane.AsSpan(y * width, width));
                for (var x = 0; x < width; x++)
                {
                    plane[(y * width) + x] = Math.Sqrt(transformed[x]);
                }
            }
        }
        Span<double> distances = stackalloc double[8];
        for (var pixel = 0; pixel < width * height; pixel++)
        {
            if (pixel % width == 0)
            {
                token.ThrowIfCancellationRequested();
            }
            for (var bin = 0; bin < 8; bin++)
            {
                distances[bin] = _distance[bin][pixel];
            }
            for (var bin = 0; bin < 8; bin++)
            {
                for (var other = 0; other < 8; other++)
                {
                    _distance[bin][pixel] = Math.Min(_distance[bin][pixel], distances[other] + (directionWeight * AngleDistance(Angle(bin), Angle(other))));
                }
            }
        }
        for (var bin = 0; bin < 8; bin++)
        {
            var prefix = new double[width * height];
            var (dx, dy) = s_directions[bin];
            for (var y = 0; y < height; y++)
            {
                token.ThrowIfCancellationRequested();
                for (var x = 0; x < width; x++)
                {
                    var px = x - dx;
                    var py = y - dy;
                    prefix[(y * width) + x] = _distance[bin][(y * width) + x] +
                        (px >= 0 && py >= 0 && px < width && py < height ? prefix[(py * width) + px] : 0);
                }
            }
            _integrals[bin] = prefix;
        }
    }

    public double At(int bin, int x, int y) => _distance[bin][(y * Width) + x];

    public double Sum(int bin, int x, int y, int count)
    {
        var (dx, dy) = s_directions[bin];
        var endX = x + ((count - 1) * dx);
        var endY = y + ((count - 1) * dy);
        if (x < 0 || y < 0 || x >= Width || y >= Height || endX < 0 || endY < 0 || endX >= Width || endY >= Height)
        {
            return double.PositiveInfinity;
        }
        var previousX = x - dx;
        var previousY = y - dy;
        return _integrals[bin][(endY * Width) + endX] -
            (previousX >= 0 && previousY >= 0 && previousX < Width && previousY < Height ? _integrals[bin][(previousY * Width) + previousX] : 0);
    }

    internal static int Bin(double angle) => Enumerable.Range(0, 8).MinBy(i => AngleDistance(Angle(i), angle));
    internal static double Angle(int bin) => Math.Atan2(s_directions[bin].Y, s_directions[bin].X);
    internal static double AngleDistance(double a, double b)
    {
        var difference = Math.Abs(a - b) % Math.PI;
        return Math.Min(difference, Math.PI - difference);
    }

    private static double[] SquaredDistance(ReadOnlySpan<double> input)
    {
        var indices = new int[input.Length];
        var boundaries = new double[input.Length + 1];
        var count = 0;
        boundaries[0] = double.NegativeInfinity;
        boundaries[1] = double.PositiveInfinity;
        for (var q = 1; q < input.Length; q++)
        {
            var intersection = 0d;
            do
            {
                var p = indices[count];
                intersection = (input[q] + ((double)q * q) - (input[p] + ((double)p * p))) / (2 * (q - p));
                if (intersection <= boundaries[count])
                {
                    count--;
                }
                else
                {
                    break;
                }
            }
            while (count >= 0);
            count++;
            indices[count] = q;
            boundaries[count] = intersection;
            boundaries[count + 1] = double.PositiveInfinity;
        }
        var output = new double[input.Length];
        var k = 0;
        for (var q = 0; q < input.Length; q++)
        {
            while (boundaries[k + 1] < q)
            {
                k++;
            }
            var delta = q - indices[k];
            output[q] = ((double)delta * delta) + input[indices[k]];
        }
        return output;
    }
}
