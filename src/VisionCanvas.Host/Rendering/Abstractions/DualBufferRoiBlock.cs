using VisionGeometry;

namespace VisionCanvas.Rendering.Abstractions;

/// <summary>형상 버퍼와 역할 버퍼 쌍으로 구성된 GPU ROI 블록 엔트리.</summary>
public sealed class DualBufferRoiBlock<TBuffer>(
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
    private const long RoleShadowBytesPerInstance = 8;

    public TBuffer Geometry { get; } = geometry;

    public TBuffer RoleBuffer { get; set; } = roles;

    public override long OwnedBytes => checked((long)(getAllocatedBytes(Geometry) + getAllocatedBytes(RoleBuffer)) + ((long)Count * RoleShadowBytesPerInstance));

    public override void Dispose()
    {
        RoleBuffer.Dispose();
        Geometry.Dispose();
    }
}
