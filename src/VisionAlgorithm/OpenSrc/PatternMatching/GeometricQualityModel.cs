using System.Runtime.CompilerServices;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

/// <summary>전체 학습 윤곽과 마스크의 소유 스냅샷. 산포 특징과 달리 잡음 측정에서 윤곽을 생략하지 않는다.</summary>
internal sealed class GeometricQualityModel
{
    public const int MaximumContourPoints = 262_144;
    public const int MaximumContrastReferences = 128;
    private readonly EdgePoint[] _contour;
    private readonly EdgePoint[] _contrastReferences;
    private readonly int[] _rowStarts;
    private readonly byte[] _mask;
    private readonly int _width;
    private readonly int _height;

    public ReadOnlySpan<EdgePoint> ContrastReferences => _contrastReferences;

    public long EstimatedBytes => ((_contour.LongLength + _contrastReferences.LongLength) * Unsafe.SizeOf<EdgePoint>()) +
        (_rowStarts.LongLength * sizeof(int)) + _mask.LongLength;

    public GeometricQualityModel(int width, int height, ReadOnlyMemory<byte> mask, List<EdgePoint> contour,
        CancellationToken token)
    {
        if (width < 1 || height < 1 || (long)width * height > ShapeTrainer.MaximumPixels ||
            (!mask.IsEmpty && mask.Length != (long)width * height))
        {
            throw new ArgumentException("기하 품질용 학습 영상은 8 Mpx 이하여야 하며 마스크 크기가 일치해야 합니다.", nameof(mask));
        }
        if (contour.Count > MaximumContourPoints)
        {
            throw new ArgumentException("기하 품질용 전체 윤곽이 262,144점 예산을 초과했습니다. 학습 ROI를 줄이십시오.", nameof(contour));
        }
        token.ThrowIfCancellationRequested();
        _width = width;
        _height = height;
        _mask = mask.ToArray();
        _contour = contour.OrderBy(edge => (int)edge.Y).ThenBy(edge => edge.X).ToArray();
        var maximumStrength = contour.Count == 0 ? 0 : contour.Max(edge => edge.Strength);
        var strongContour = _contour.Where(edge => edge.Strength >= maximumStrength * .5).ToArray();
        var referenceCount = Math.Min(MaximumContrastReferences, strongContour.Length);
        _contrastReferences = new EdgePoint[referenceCount];
        for (var reference = 0; reference < referenceCount; reference++)
        {
            _contrastReferences[reference] = strongContour[(int)((long)reference * strongContour.Length / referenceCount)];
        }
        _rowStarts = new int[height + 1];
        var index = 0;
        for (var row = 0; row < height; row++)
        {
            token.ThrowIfCancellationRequested();
            _rowStarts[row] = index;
            while (index < _contour.Length && (int)_contour[index].Y == row)
            {
                index++;
            }
        }
        _rowStarts[height] = index;
    }

    public bool Contains(double x, double y)
        => x >= 0 && y >= 0 && x < _width && y < _height &&
            (_mask.Length == 0 || _mask[((int)y * _width) + (int)x] != 0);

    public bool Explains(double x, double y, double normalX, double normalY, double radius,
        ContrastPolarity polarity, CancellationToken token)
        => Explain(x, y, normalX, normalY, radius, polarity, 0, token).AnyContour;

    public ContourExplanation Explain(double x, double y, double normalX, double normalY, double radius,
        ContrastPolarity polarity, double minimumEnergyStrength, CancellationToken token)
    {
        var anyContour = false;
        var firstRow = (int)Math.Max(0, Math.Floor(y - radius));
        var lastRow = (int)Math.Min(_height - 1, Math.Floor(y + radius));
        var squaredRadius = radius * radius;
        for (var row = firstRow; row <= lastRow; row++)
        {
            token.ThrowIfCancellationRequested();
            var start = _rowStarts[row];
            var end = _rowStarts[row + 1];
            var low = start;
            var high = end;
            while (low < high)
            {
                var middle = low + ((high - low) / 2);
                if (_contour[middle].X < x - radius)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }
            for (var index = low; index < end && _contour[index].X <= x + radius; index++)
            {
                if ((index & 255) == 0)
                {
                    token.ThrowIfCancellationRequested();
                }
                var edge = _contour[index];
                var dx = edge.X - x;
                var dy = edge.Y - y;
                if ((dx * dx) + (dy * dy) <= squaredRadius &&
                    GeometricQualityEvaluator.Compatible(normalX, normalY, edge, polarity))
                {
                    anyContour = true;
                    if (edge.Strength >= minimumEnergyStrength)
                    {
                        return new(true, true);
                    }
                }
            }
        }
        return new(anyContour, false);
    }
}

internal readonly record struct ContourExplanation(bool AnyContour, bool EnergyContour);
