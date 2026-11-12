using VisionCanvas.Core.Geometry;

namespace VisionCanvas.Rendering.Abstractions;

/// <summary>이중 GPU 버퍼(형상 + 역할)를 사용하는 ROI 파이프라인(Vulkan, Wgpu 등)의 공통 캐시·업로드·퇴출 기반 클래스.</summary>
public abstract class RoiPipelineBase<TBuffer, TSurface> : IDisposable
    where TBuffer : class, IDisposable
    where TSurface : IRenderSurface
{
    private readonly RoiBlockCache<DualBufferRoiBlock<TBuffer>> _cache = new();

    public bool HasPendingDetails => _cache.HasPendingDetails;

    protected abstract TBuffer CreateStorageBuffer<TElement>(ReadOnlySpan<TElement> values)
        where TElement : unmanaged;

    protected abstract ulong GetAllocatedBytes(TBuffer buffer);

    protected void DrawCached(
        RenderScene scene,
        TSurface surface,
        Action<DualBufferRoiBlock<TBuffer>, RoiDrawConstants> drawBlock)
    {
        RoiSpatialTraversal.DrawCachedBlocks(
            _cache,
            scene,
            surface,
            UploadBlock,
            UploadRoleChanges,
            drawBlock);
    }

    private DualBufferRoiBlock<TBuffer> UploadBlock(
        RoiSpatialBlock region,
        double aggregateSize,
        ReadOnlySpan<RoiInstance> instances,
        RoiRoleCache roles)
    {
        TBuffer? geometry = null;
        TBuffer? roleBuffer = null;
        try
        {
            geometry = CreateStorageBuffer(instances);
            roleBuffer = CreateStorageBuffer<uint>(roles.Values);
            var block = new DualBufferRoiBlock<TBuffer>(
                geometry,
                roleBuffer,
                region.Bounds.Center,
                instances.Length,
                roles,
                aggregateSize,
                GetAllocatedBytes);
            geometry = null;
            roleBuffer = null;
            return block;
        }
        finally
        {
            geometry?.Dispose();
            roleBuffer?.Dispose();
        }
    }

    private void UploadRoleChanges(DualBufferRoiBlock<TBuffer> block, TSurface surface)
    {
        var roleBuffer = CreateStorageBuffer<uint>(block.Roles.Values);
        surface.Retire(block.RoleBuffer);
        block.RoleBuffer = roleBuffer;
    }

    protected virtual void DisposeBackendResources()
    {
    }

    public void Dispose()
    {
        _cache.Dispose();
        DisposeBackendResources();
        GC.SuppressFinalize(this);
    }
}
