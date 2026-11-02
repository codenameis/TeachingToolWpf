using System.Diagnostics;
using System.Runtime.InteropServices;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionGeometry;

namespace VisionCanvas.Rendering;

/// <summary>DX12, Vulkan, Wgpu, Skia가 공유하는 BVH 블록 캐시(fullDetail 재사용, 4ms 프레임 분할 준비, 128MiB LRU 퇴출).</summary>
internal sealed class RoiBlockCache<TBlock>(bool profileEnabled = false, long budgetBytes = RoiBlockCache<TBlock>.DefaultBudgetBytes) : IDisposable
    where TBlock : RoiCachedBlockBase
{
    public const long DefaultBudgetBytes = 128L * 1024 * 1024;
    public const double DetailPreparationBudgetMs = 4;

    public delegate TBlock UploadBlockDelegate(
        RoiSpatialBlock region,
        double aggregateSize,
        ReadOnlySpan<RoiInstance> instances,
        RoiRoleCache roles);

    private readonly Dictionary<(object Identity, int? Level), TBlock> _blocks = new(RoiNullableBlockKeyComparer.Instance);
    private readonly Dictionary<object, TBlock> _lastDetails = new(ReferenceEqualityComparer.Instance);
    private readonly RoiCacheMemory _cacheMemory = new();
    private readonly RoiRoleLookup _roleLookup = new();
    private readonly List<RoiInstance> _packingInstances = [];
    private readonly List<uint> _packingRoles = [];
    private readonly Dictionary<RoiGeometry, int> _packingUnique = [];
    private readonly Dictionary<object, (long MaxLastUsed, bool HasActive)> _pageStats = new(ReferenceEqualityComparer.Instance);
    private readonly List<(object Page, long MaxLastUsed)> _coldPages = [];
    private readonly List<KeyValuePair<(object Identity, int? Level), TBlock>> _evictionCandidates = [];
    private long _frame;
    private long _preparationStart;
    private int _preparedBlocks;

    public bool HasPendingDetails { get; private set; }

    public int DeferredBlocks { get; private set; }

    public long InstanceCount { get; private set; }

    public double PackMilliseconds { get; private set; }

    public long MemoryBytes => _cacheMemory.Bytes + _roleLookup.EstimatedBytes;

    public void BeginFrame()
    {
        _frame++;
        InstanceCount = 0;
        PackMilliseconds = 0;
        HasPendingDetails = false;
        DeferredBlocks = 0;
        _preparedBlocks = 0;
        _preparationStart = Stopwatch.GetTimestamp();
    }

    public TBlock GetOrUpload(
        RoiSpatialBlock region,
        int level,
        double aggregateSize,
        DocumentState document,
        UploadBlockDelegate uploadBlock,
        Action<TBlock> uploadRoleChanges,
        bool allowDeferredFallback = true)
    {
        var key = (region.Identity, (int?)level);
        var fullDetailKey = (region.Identity, (int?)null);
        _blocks.TryGetValue(fullDetailKey, out var fullDetail);
        TBlock block;
        if (TryGetCachedBlock(region, key, fullDetail, aggregateSize, document, uploadRoleChanges) is { } cached)
        {
            block = cached;
        }
        else if (allowDeferredFallback && TryGetDeferredFallbackBlock(region, document, uploadRoleChanges) is { } fallback)
        {
            block = fallback;
        }
        else
        {
            _preparedBlocks++;
            block = PackAndUploadBlock(region, key, fullDetailKey, fullDetail, aggregateSize, document, uploadBlock, uploadRoleChanges);
        }

        _lastDetails[region.Identity] = block;
        block.LastUsed = _frame;
        InstanceCount += block.Count;
        return block;
    }

    private TBlock? TryGetCachedBlock(
        RoiSpatialBlock region,
        (object Identity, int? Level) key,
        TBlock? fullDetail,
        double aggregateSize,
        DocumentState document,
        Action<TBlock> uploadRoleChanges)
    {
        // BVH에서 T 크기에 요약이 없었다면 더 작은 크기에도 요약이 생기지 않는다.
        TBlock? block = null;
        if (fullDetail is not null && aggregateSize <= fullDetail.FullDetailAggregateSize)
        {
            block = fullDetail;
        }
        else
        {
            _blocks.TryGetValue(key, out block);
        }

        if (block is null)
        {
            return null;
        }

        var roleAggregate = ReferenceEquals(block, fullDetail) ? block.FullDetailAggregateSize : aggregateSize;
        if (block.Roles.Refresh(region, roleAggregate, document, block.Count, _roleLookup))
        {
            uploadRoleChanges(block);
        }

        return block;
    }

    private TBlock? TryGetDeferredFallbackBlock(
        RoiSpatialBlock region,
        DocumentState document,
        Action<TBlock> uploadRoleChanges)
    {
        // 같은 불변 형상 블록의 직전 LOD를 현재 view/preview로 그린다. 새 블록은 생략하지 않는다.
        // 최소 한 블록은 진행해 지속 입력/느린 장치에서도 상세화가 굶지 않게 한다.
        if (_preparedBlocks <= 0
            || Stopwatch.GetElapsedTime(_preparationStart).TotalMilliseconds < DetailPreparationBudgetMs
            || !_lastDetails.TryGetValue(region.Identity, out var fallback))
        {
            return null;
        }

        if (fallback.Roles.Refresh(region, fallback.AggregateSize, document, fallback.Count, _roleLookup))
        {
            uploadRoleChanges(fallback);
        }

        HasPendingDetails = true;
        DeferredBlocks++;
        return fallback;
    }

    private TBlock PackAndUploadBlock(
        RoiSpatialBlock region,
        (object Identity, int? Level) key,
        (object Identity, int? Level) fullDetailKey,
        TBlock? fullDetail,
        double aggregateSize,
        DocumentState document,
        UploadBlockDelegate uploadBlock,
        Action<TBlock> uploadRoleChanges)
    {
        var packStart = profileEnabled ? Stopwatch.GetTimestamp() : 0;
        TBlock block;
        try
        {
            var hasSummary = RoiInstance.PackInto(region, aggregateSize, document, _packingInstances, _packingRoles, _packingUnique, _roleLookup);
            var packedRoles = _packingRoles.ToArray();
            if (!hasSummary && fullDetail is not null)
            {
                return ReuseFullDetailBlock(fullDetail, document, packedRoles, aggregateSize, packStart, uploadRoleChanges);
            }

            var roles = new RoiRoleCache(document, packedRoles);
            if (profileEnabled)
            {
                PackMilliseconds += Stopwatch.GetElapsedTime(packStart).TotalMilliseconds;
            }

            block = uploadBlock(region, aggregateSize, CollectionsMarshal.AsSpan(_packingInstances), roles);
            if (!hasSummary)
            {
                block.FullDetailAggregateSize = aggregateSize;
                key = fullDetailKey;
            }
        }
        finally
        {
            RoiInstance.ReleasePackingBuffers(_packingInstances, _packingRoles, _packingUnique);
        }

        _blocks.Add(key, block);
        _cacheMemory.Add(region.Identity, region.Count, region.ShapeBytes, block.OwnedBytes);
        return block;
    }

    private TBlock ReuseFullDetailBlock(
        TBlock fullDetail,
        DocumentState document,
        uint[] packedRoles,
        double aggregateSize,
        long packStart,
        Action<TBlock> uploadRoleChanges)
    {
        var rolesChanged = fullDetail.Roles.RefreshPacked(document, packedRoles, fullDetail.Count);
        if (profileEnabled)
        {
            PackMilliseconds += Stopwatch.GetElapsedTime(packStart).TotalMilliseconds;
        }

        if (rolesChanged)
        {
            uploadRoleChanges(fullDetail);
        }

        fullDetail.FullDetailAggregateSize = aggregateSize;
        return fullDetail;
    }

    public void EvictInactive(Func<long, long> getTotalBytes, Action<TBlock> retireBlock)
    {
        if (getTotalBytes(MemoryBytes) <= budgetBytes)
        {
            return;
        }

        // 페이지 기반 할당기를 쓰는 백엔드는 완전히 비활성인 페이지부터 비워 GPU 할당 비용도 함께 줄인다.
        _pageStats.Clear();
        foreach (var pair in _blocks)
        {
            var block = pair.Value;
            var isActive = block.LastUsed == _frame;
            RecordPageStat(block.PageIdentity, block.LastUsed, isActive);
            RecordPageStat(block.SecondaryPageIdentity, block.LastUsed, isActive);
        }

        if (_pageStats.Count > 0)
        {
            _coldPages.Clear();
            foreach (var stat in _pageStats)
            {
                if (!stat.Value.HasActive)
                {
                    _coldPages.Add((stat.Key, stat.Value.MaxLastUsed));
                }
            }

            _coldPages.Sort(static (a, b) => a.MaxLastUsed.CompareTo(b.MaxLastUsed));
            for (var p = 0; p < _coldPages.Count; p++)
            {
                if (getTotalBytes(MemoryBytes) <= budgetBytes)
                {
                    return;
                }

                var targetPage = _coldPages[p].Page;
                _evictionCandidates.Clear();
                foreach (var pair in _blocks)
                {
                    if (ReferenceEquals(pair.Value.PageIdentity, targetPage)
                        || ReferenceEquals(pair.Value.SecondaryPageIdentity, targetPage))
                    {
                        _evictionCandidates.Add(pair);
                    }
                }

                for (var i = 0; i < _evictionCandidates.Count; i++)
                {
                    var pair = _evictionCandidates[i];
                    RetireEntry(pair.Key, pair.Value, retireBlock);
                }
            }
        }

        _evictionCandidates.Clear();
        foreach (var pair in _blocks)
        {
            if (pair.Value.LastUsed != _frame)
            {
                _evictionCandidates.Add(pair);
            }
        }

        _evictionCandidates.Sort(static (a, b) => a.Value.LastUsed.CompareTo(b.Value.LastUsed));
        for (var i = 0; i < _evictionCandidates.Count; i++)
        {
            if (getTotalBytes(MemoryBytes) <= budgetBytes)
            {
                break;
            }

            var pair = _evictionCandidates[i];
            RetireEntry(pair.Key, pair.Value, retireBlock);
        }
    }

    private void RecordPageStat(object? page, long lastUsed, bool isActive)
    {
        if (page is null)
        {
            return;
        }

        if (_pageStats.TryGetValue(page, out var stat))
        {
            _pageStats[page] = (Math.Max(stat.MaxLastUsed, lastUsed), stat.HasActive || isActive);
        }
        else
        {
            _pageStats.Add(page, (lastUsed, isActive));
        }
    }

    private void RetireEntry((object Identity, int? Level) key, TBlock block, Action<TBlock> retireBlock)
    {
        _blocks.Remove(key);
        if (_lastDetails.TryGetValue(key.Identity, out var last) && ReferenceEquals(last, block))
        {
            _lastDetails.Remove(key.Identity);
            ReassignLastDetailFallback(key.Identity);
        }

        _cacheMemory.Remove(key.Identity, block.OwnedBytes);
        retireBlock(block);
    }

    private void ReassignLastDetailFallback(object identity)
    {
        foreach (var remaining in _blocks)
        {
            if (ReferenceEquals(remaining.Key.Identity, identity))
            {
                _lastDetails[identity] = remaining.Value;
                break;
            }
        }
    }

    public void Dispose()
    {
        foreach (var block in _blocks.Values)
        {
            block.Dispose();
        }

        _blocks.Clear();
        _lastDetails.Clear();
        _cacheMemory.Clear();
    }
}
