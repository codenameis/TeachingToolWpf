namespace VisionCanvas.Rendering;

/// <summary>같은 공간 블록의 CPU 원본은 확대 단계 사이에 공유하고, 역할 배열은 각각 계산한다.</summary>
internal sealed class RoiCacheMemory
{
    private const int EstimatedSourceRoiBytes = 256;
    private readonly Dictionary<object, Source> _sources = new(ReferenceEqualityComparer.Instance);

    public long Bytes
    {
        get;
        private set;
    }

    public void Add(object identity, int sourceCount, long shapeBytes, long ownedBytes)
    {
        if (_sources.TryGetValue(identity, out var source))
        {
            _sources[identity] = source with
            {
                References = source.References + 1
            };
            Bytes += ownedBytes;
            return;
        }

        var sourceBytes = ((long)sourceCount * EstimatedSourceRoiBytes) + shapeBytes;
        _sources.Add(identity, new(1, sourceBytes));
        Bytes += sourceBytes + ownedBytes;
    }

    public void Remove(object identity, long ownedBytes)
    {
        var source = _sources[identity];
        if (source.References == 1)
        {
            _sources.Remove(identity);
            Bytes -= source.Bytes;
        }
        else
        {
            _sources[identity] = source with
            {
                References = source.References - 1
            };
        }

        Bytes -= ownedBytes;
    }

    public void Clear()
    {
        _sources.Clear();
        Bytes = 0;
    }

    private readonly record struct Source(int References, long Bytes);
}
