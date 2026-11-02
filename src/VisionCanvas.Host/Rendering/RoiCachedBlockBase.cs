using VisionGeometry;

namespace VisionCanvas.Rendering;

/// <summary>렌더 백엔드별 ROI 블록 엔트리의 공통 기본 계약이다.</summary>
internal abstract class RoiCachedBlockBase(PointD origin, int count, RoiRoleCache roles, double aggregateSize) : IDisposable
{
    public PointD Origin { get; } = origin;

    public int Count { get; } = count;

    public RoiRoleCache Roles { get; } = roles;

    public double AggregateSize { get; } = aggregateSize;

    public double FullDetailAggregateSize { get; set; }

    public long LastUsed { get; set; }

    public abstract long OwnedBytes { get; }

    public virtual object? PageIdentity => null;

    public virtual object? SecondaryPageIdentity => null;

    public abstract void Dispose();
}

/// <summary>형상 버퍼와 역할 버퍼 쌍으로 구성된 GPU ROI 블록 엔트리.</summary>
internal sealed class DualBufferRoiBlock<TBuffer>(
    TBuffer geometry,
    TBuffer roles,
    PointD origin,
    int count,
    RoiRoleCache roleCache,
    double aggregateSize,
    Func<TBuffer, ulong> getAllocatedBytes)
    : RoiCachedBlockBase(origin, count, roleCache, aggregateSize)
    where TBuffer : class, IDisposable
{
    public TBuffer Geometry { get; } = geometry;

    public TBuffer RoleBuffer { get; set; } = roles;

    public override long OwnedBytes => checked((long)(getAllocatedBytes(Geometry) + getAllocatedBytes(RoleBuffer)) + ((long)Count * 8));

    public override void Dispose()
    {
        RoleBuffer.Dispose();
        Geometry.Dispose();
    }
}
