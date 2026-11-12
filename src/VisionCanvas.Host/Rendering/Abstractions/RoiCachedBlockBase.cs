using VisionGeometry;

namespace VisionCanvas.Rendering.Abstractions;

/// <summary>렌더 백엔드별 ROI 블록 엔트리의 공통 기본 계약이다.</summary>
public abstract class RoiCachedBlockBase(PointD origin, int count, RoiRoleCache roles, double aggregateSize) : IDisposable
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
