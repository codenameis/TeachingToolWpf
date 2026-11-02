namespace VisionGeometry;

public readonly record struct QuadPoints(PointD A, PointD B, PointD C, PointD D) : IReadOnlyList<PointD>
{
    public int Count => 4;

    public int Length => 4;

    public PointD this[int index] => index switch
    {
        0 => A,
        1 => B,
        2 => C,
        3 => D,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    public QuadPoints Select(Func<PointD, PointD> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        return new(selector(A), selector(B), selector(C), selector(D));
    }

    public QuadPoints Select<TState>(TState state, Func<TState, PointD, PointD> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        return new(
            selector(state, A),
            selector(state, B),
            selector(state, C),
            selector(state, D));
    }

    public void CopyTo(Span<PointD> destination)
    {
        if (destination.Length < 4)
        {
            throw new ArgumentException("버퍼 크기는 최소 4여야 합니다.", nameof(destination));
        }

        destination[0] = A;
        destination[1] = B;
        destination[2] = C;
        destination[3] = D;
    }

    public PointD[] ToArray() => [A, B, C, D];

    public Enumerator GetEnumerator() => new(this);

    IEnumerator<PointD> IEnumerable<PointD>.GetEnumerator()
    {
        yield return A;
        yield return B;
        yield return C;
        yield return D;
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => ((IEnumerable<PointD>)this).GetEnumerator();

    public struct Enumerator(QuadPoints quad)
    {
        private int _index = -1;

        public PointD Current => quad[_index];

        public bool MoveNext()
        {
            _index++;
            return _index < 4;
        }
    }
}
