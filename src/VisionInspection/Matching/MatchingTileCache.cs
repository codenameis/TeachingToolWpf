using TeachingToolWpf.Core.Imaging;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>독립 검사 reader가 단독 소유하는 원본 Gray8 타일 LRU. 예산은 실제 빌린 배열 바이트다.</summary>
internal sealed class MatchingTileCache : MatchingTileCache<TileKey>
{
    public MatchingTileCache() : base(16 * 1024 * 1024)
    {
    }
}

/// <summary>원본/축소 타일이 같은 버퍼 소유권·최근 사용·실제 대여 바이트 규칙을 공유한다.</summary>
internal class MatchingTileCache<TKey>(int maximumBytes) where TKey : notnull
{
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _entries = new();
    private readonly LinkedList<Entry> _recency = new();
    private int _bytes;

    public bool TryGet(TKey key, out PixelBuffer image)
    {
        if (!_entries.TryGetValue(key, out var node))
        {
            image = null!;
            return false;
        }
        _recency.Remove(node);
        _recency.AddLast(node);
        image = node.Value.Image;
        return true;
    }

    public void Add(TKey key, PixelBuffer image)
    {
        var bytes = image.Bytes.Length;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bytes, maximumBytes);
        while (_bytes + bytes > maximumBytes && _recency.First is { } oldest)
        {
            _bytes -= oldest.Value.Image.Bytes.Length;
            _entries.Remove(oldest.Value.Key);
            _recency.RemoveFirst();
            oldest.Value.Image.Dispose();
        }
        _entries.Add(key, _recency.AddLast(new Entry(key, image)));
        _bytes += bytes;
    }

    public void Clear()
    {
        foreach (var entry in _recency)
        {
            entry.Image.Dispose();
        }
        _entries.Clear();
        _recency.Clear();
        _bytes = 0;
    }

    private sealed record Entry(TKey Key, PixelBuffer Image);
}
