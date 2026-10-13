using System.Collections.Immutable;

namespace VisionCanvas.Core.Editing;

/// <summary>Sorted, disjoint half-open cell ranges. Operations never enumerate individual cells.</summary>
public sealed class RoiCellSet
{
    public readonly record struct Range(int Start, int End);

    public static RoiCellSet Empty { get; } = new([]);
    public ImmutableArray<Range> Ranges
    {
        get;
    }
    public int Count
    {
        get;
    }
    public bool IsEmpty => Ranges.IsEmpty;

    private RoiCellSet(ImmutableArray<Range> ranges)
    {
        Ranges = ranges;
        Count = checked((int)ranges.Sum(range => (long)range.End - range.Start));
    }

    public static RoiCellSet All(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return count == 0 ? Empty : new([new(0, count)]);
    }

    public static RoiCellSet Single(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        return new([new(index, checked(index + 1))]);
    }

    public static RoiCellSet Create(IEnumerable<Range> ranges)
    {
        var result = ImmutableArray.CreateBuilder<Range>();
        foreach (var range in ranges.OrderBy(range => range.Start))
        {
            if (range.Start < 0 || range.End < range.Start)
            {
                throw new ArgumentOutOfRangeException(nameof(ranges));
            }
            Append(result, range);
        }
        return result.Count == 0 ? Empty : new(result.ToImmutable());
    }

    public bool Contains(int index)
    {
        var low = 0;
        var high = Ranges.Length;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            var range = Ranges[middle];
            if (index < range.Start)
            {
                high = middle;
            }
            else if (index >= range.End)
            {
                low = middle + 1;
            }
            else
            {
                return true;
            }
        }
        return false;
    }

    public bool SetEquals(RoiCellSet other) => ReferenceEquals(this, other) || Ranges.AsSpan().SequenceEqual(other.Ranges.AsSpan());

    public RoiCellSet Union(RoiCellSet other)
    {
        if (ReferenceEquals(this, other) || other.IsEmpty)
        {
            return this;
        }

        if (IsEmpty)
        {
            return other;
        }

        var result = ImmutableArray.CreateBuilder<Range>();
        var left = 0;
        var right = 0;
        while (left < Ranges.Length || right < other.Ranges.Length)
        {
            Append(result, right == other.Ranges.Length || (left < Ranges.Length && Ranges[left].Start <= other.Ranges[right].Start)
                ? Ranges[left++] : other.Ranges[right++]);
        }
        return new(result.ToImmutable());
    }

    public RoiCellSet Intersect(RoiCellSet other)
    {
        if (ReferenceEquals(this, other))
        {
            return this;
        }

        var result = ImmutableArray.CreateBuilder<Range>();
        var left = 0;
        var right = 0;
        while (left < Ranges.Length && right < other.Ranges.Length)
        {
            var a = Ranges[left];
            var b = other.Ranges[right];
            Append(result, new(Math.Max(a.Start, b.Start), Math.Min(a.End, b.End)));
            if (a.End <= b.End)
            {
                left++;
            }

            if (b.End <= a.End)
            {
                right++;
            }
        }
        return result.Count == 0 ? Empty : new(result.ToImmutable());
    }

    public RoiCellSet Except(RoiCellSet other)
    {
        if (ReferenceEquals(this, other))
        {
            return Empty;
        }

        if (IsEmpty || other.IsEmpty)
        {
            return this;
        }

        var result = ImmutableArray.CreateBuilder<Range>();
        var right = 0;
        foreach (var range in Ranges)
        {
            var start = range.Start;
            while (right < other.Ranges.Length && other.Ranges[right].End <= start)
            {
                right++;
            }

            var next = right;
            while (next < other.Ranges.Length && other.Ranges[next].Start < range.End)
            {
                var removed = other.Ranges[next++];
                Append(result, new(start, Math.Min(range.End, removed.Start)));
                start = Math.Max(start, removed.End);
                if (start >= range.End)
                {
                    break;
                }
            }
            Append(result, new(start, range.End));
        }
        return result.Count == 0 ? Empty : new(result.ToImmutable());
    }

    private static void Append(ImmutableArray<Range>.Builder result, Range range)
    {
        if (range.End <= range.Start)
        {
            return;
        }

        if (result.Count != 0 && range.Start <= result[^1].End)
        {
            result[^1] = new(result[^1].Start, Math.Max(result[^1].End, range.End));
        }
        else
        {
            result.Add(range);
        }
    }
}
