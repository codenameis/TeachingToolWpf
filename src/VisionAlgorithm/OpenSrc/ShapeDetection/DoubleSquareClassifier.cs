using VisionAlgorithm.OpenSrc.PatternMatching;

namespace VisionAlgorithm.OpenSrc.ShapeDetection;

/// <summary>크기와 자세가 같은 대각선 이웃 두 개만 묶는다. 여러 짝이 가능한 배열은 묶지 않는다.</summary>
internal static class DoubleSquareClassifier
{
    public static List<PrimitiveShapeDetection> Combine(List<PrimitiveShapeDetection> shapes, PrimitiveDetectionOptions options,
        CancellationToken token)
    {
        var squares = shapes.Select((shape, index) => Square(shape, index)).Where(square => square is not null)
            .Select(square => square!).ToArray();
        var cells = new Dictionary<(int Level, int X, int Y), List<int>>();
        for (var index = 0; index < squares.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var square = squares[index];
            var level = (int)Math.Floor(Math.Log2(square.Side));
            var key = Cell(square.Shape.Center, level);
            if (!cells.TryGetValue(key, out var bucket))
            {
                cells.Add(key, bucket = []);
            }
            bucket.Add(index);
        }
        var neighbors = new int[squares.Length];
        var partner = new int[squares.Length];
        for (var index = 0; index < squares.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var first = squares[index];
            var level = (int)Math.Floor(Math.Log2(first.Side));
            for (var adjacent = level - 1; adjacent <= level + 1; adjacent++)
            {
                var cell = Cell(first.Shape.Center, adjacent);
                for (var y = cell.Y - 1; y <= cell.Y + 1; y++)
                {
                    for (var x = cell.X - 1; x <= cell.X + 1; x++)
                    {
                        if (!cells.TryGetValue((adjacent, x, y), out var bucket))
                        {
                            continue;
                        }
                        foreach (var other in bucket)
                        {
                            if (other <= index || !Pair(first, squares[other], options.MaximumSize))
                            {
                                continue;
                            }
                            neighbors[index]++;
                            neighbors[other]++;
                            partner[index] = other;
                            partner[other] = index;
                        }
                    }
                }
            }
        }
        var consumed = new HashSet<int>();
        var combined = new List<PrimitiveShapeDetection>();
        for (var index = 0; index < squares.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var other = partner[index];
            if (neighbors[index] != 1 || neighbors[other] != 1 || other <= index)
            {
                continue;
            }
            var first = squares[index];
            var second = squares[other];
            var a = first.Shape;
            var b = second.Shape;
            var center = new ImagePoint((a.Center.X + b.Center.X) / 2, (a.Center.Y + b.Center.Y) / 2);
            var angle = Math.IEEERemainder(Math.Atan2(b.Center.Y - a.Center.Y, b.Center.X - a.Center.X) + (Math.PI / 4), Math.PI);
            combined.Add(new(PrimitiveShapeKind.DoubleSquare, center, angle, Math.Min(a.Score, b.Score),
                a.AreaPixels + b.AreaPixels, a.Contour)
            {
                AdditionalContours = [b.Contour]
            });
            consumed.Add(first.Index);
            consumed.Add(second.Index);
        }
        combined.AddRange(shapes.Where((_, index) => !consumed.Contains(index)));
        return combined;
    }

    private static (int Level, int X, int Y) Cell(ImagePoint center, int level)
    {
        var size = Math.Pow(2, level + 2);
        return (level, (int)Math.Floor(center.X / size), (int)Math.Floor(center.Y / size));
    }

    private sealed record SquareCandidate(PrimitiveShapeDetection Shape, int Index, double Side);

    internal static bool IsSquare(PrimitiveShapeDetection shape) => Square(shape, 0) is not null;

    private static SquareCandidate? Square(PrimitiveShapeDetection shape, int index)
    {
        if (shape.Kind is not (PrimitiveShapeKind.Rectangle or PrimitiveShapeKind.Diamond) || !shape.InnerContours.IsEmpty)
        {
            return null;
        }
        var cosine = Math.Cos(shape.AngleRadians);
        var sine = Math.Sin(shape.AngleRadians);
        var xs = shape.Contour.Select(point => (cosine * point.X) + (sine * point.Y)).ToArray();
        var ys = shape.Contour.Select(point => (-sine * point.X) + (cosine * point.Y)).ToArray();
        var width = xs.Max() - xs.Min();
        var height = ys.Max() - ys.Min();
        return Math.Min(width, height) / Math.Max(width, height) < .9 || shape.AreaPixels / (width * height) < .9
            ? null : new(shape, index, (width + height) / 2);
    }

    private static bool Pair(SquareCandidate first, SquareCandidate second, double maximumSize)
    {
        if (Math.Min(first.Side, second.Side) / Math.Max(first.Side, second.Side) < .9 ||
            Math.Abs(Math.IEEERemainder(first.Shape.AngleRadians - second.Shape.AngleRadians, Math.PI / 2)) > Math.PI / 60)
        {
            return false;
        }
        var dx = second.Shape.Center.X - first.Shape.Center.X;
        var dy = second.Shape.Center.Y - first.Shape.Center.Y;
        var cosine = Math.Cos(first.Shape.AngleRadians);
        var sine = Math.Sin(first.Shape.AngleRadians);
        var side = (first.Side + second.Side) / 2;
        var x = Math.Abs((cosine * dx) + (sine * dy)) / side;
        var y = Math.Abs((-sine * dx) + (cosine * dy)) / side;
        if (x is < .96 or > 1.4 || y is < .96 or > 1.4 || Math.Abs(x - y) > .12)
        {
            return false;
        }
        var points = first.Shape.Contour.Concat(second.Shape.Contour).ToArray();
        return Math.Max(points.Max(p => p.X) - points.Min(p => p.X), points.Max(p => p.Y) - points.Min(p => p.Y)) <= maximumSize;
    }
}
