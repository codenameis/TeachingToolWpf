using System.Diagnostics;
using System.IO;
using VisionCanvas.Core.Geometry;
using VisionCanvas.Rendering.Abstractions;
using VisionGeometry;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace VisionCanvas.Rendering;

/// <summary>형상 버퍼를 유지하며 역할 구간만 별도로 갱신한다. 색·두께·화면 이동은 draw 상수로 전달한다.</summary>
internal sealed class RoiPipeline : IDisposable
{
    // Root parameter 번호와 HLSL register 번호는 서로 다른 계약이다.
    private const int GeometryRootParameter = 0;
    private const int DrawConstantsRootParameter = 1;
    private const int RolesRootParameter = 2;
    private const int GeometryShaderRegister = 0;
    private const int RolesShaderRegister = 1;
    private const int OptionsShaderRegister = 0;
    private const int RegisterSpace = 0;

    private sealed class Block(
        RoiBufferArena.Lease geometryAllocation,
        RoiBufferArena.Lease roleAllocation,
        PointD origin,
        int count,
        RoiRoleCache roles,
        double aggregateSize)
        : RoiCachedBlockBase(origin, count, roles, aggregateSize)
    {
        public RoiBufferArena.Lease GeometryAllocation { get; } = geometryAllocation;

        public RoiBufferArena.Lease RoleAllocation { get; set; } = roleAllocation;

        // 현재/직전 계층의 역할 배열 두 벌을 보수적으로 예약한다.
        public override long OwnedBytes => (long)Count * RoiInstance.RoleSizeInBytes * 2;

        public override object? PageIdentity => GeometryAllocation.Page;

        public override object? SecondaryPageIdentity
            => ReferenceEquals(RoleAllocation.Page, GeometryAllocation.Page) ? null : RoleAllocation.Page;

        public void Retire()
        {
            GeometryAllocation.Retire();
            RoleAllocation.Retire();
        }

        public override void Dispose()
        {
            RoleAllocation.Dispose();
            GeometryAllocation.Dispose();
        }
    }

    private readonly bool _profileEnabled;
    private readonly ID3D12RootSignature _root;
    private readonly ID3D12PipelineState _pipeline;
    private readonly FrameUploadArena[] _uploads;
    private readonly RoiBufferArena _buffers;
    private readonly RoiBlockCache<Block> _cache;

    public bool HasPendingDetails => _cache.HasPendingDetails;

    public int DeferredBlocks => _cache.DeferredBlocks;

    public long UploadedBytes
    {
        get;
        private set;
    }

    public long InstanceCount => _cache.InstanceCount;

    public long CacheBytes => _cache.MemoryBytes + _buffers.ActiveAllocationBytes;

    public double PackMilliseconds => _cache.PackMilliseconds;

    public double CreateMilliseconds { get; private set; }

    public double UploadMilliseconds { get; private set; }

    public RoiPipeline(Dx12Device gpu, bool profileEnabled = false)
    {
        _profileEnabled = profileEnabled;
        _cache = new(profileEnabled);
        _uploads = [new(gpu), new(gpu)];
        _buffers = new(gpu);
        _root = gpu.Device.CreateRootSignature(new RootSignatureDescription1(RootSignatureFlags.None,
            [new(RootParameterType.ShaderResourceView, new RootDescriptor1(GeometryShaderRegister, RegisterSpace), ShaderVisibility.Vertex),
             new(new RootConstants(OptionsShaderRegister, RegisterSpace, RoiDrawConstants.RootConstantCount), ShaderVisibility.All),
             new(RootParameterType.ShaderResourceView, new RootDescriptor1(RolesShaderRegister, RegisterSpace), ShaderVisibility.Vertex)]));
        try
        {
            var shaderPath = Path.Combine(AppContext.BaseDirectory, "Shaders", "Roi.hlsl");
            _pipeline = gpu.Device.CreateGraphicsPipelineState<ID3D12PipelineState>(new GraphicsPipelineStateDescription
            {
                RootSignature = _root,
                VertexShader = ShaderCompiler.CompileFile(shaderPath, "VSMain", "vs_5_1"),
                PixelShader = ShaderCompiler.CompileFile(shaderPath, "PSMain", "ps_5_1"),
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                RasterizerState = RasterizerDescription.CullNone,
                BlendState = BlendDescription.NonPremultiplied,
                DepthStencilState = DepthStencilDescription.None,
                RenderTargetFormats = [Format.B8G8R8A8_UNorm],
                SampleDescription = new(1, 0)
            });
        }
        catch
        {
            _root.Dispose();
            throw;
        }
    }

    public void Draw(RenderScene scene, Dx12Surface surface)
    {
        _cache.BeginFrame();
        UploadedBytes = 0;
        CreateMilliseconds = 0;
        UploadMilliseconds = 0;
        _uploads[surface.FrameIndex].Reset();

        var lod = RoiSpatialTraversal.ComputeLodMetrics(scene);

        surface.Commands.SetGraphicsRootSignature(_root);
        surface.Commands.SetPipelineState(_pipeline);
        surface.Commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        RoiSpatialTraversal.ForEachIndex(scene, DrawIndex);
        _cache.EvictInactive(
            memoryBytes => memoryBytes + _buffers.ActiveAllocationBytes,
            block =>
            {
                block.Retire();
                surface.Retire(block);
            });

        void DrawIndex(RoiSpatialIndex index, bool selected, RigidTransform2D transform, bool searchRange)
        {
            var query = transform.InverseTransformBounds(lod.VisibleBounds);
            foreach (var region in index.Blocks(query))
            {
                var block = _cache.GetOrUpload(
                    region,
                    lod.Level,
                    lod.AggregateSize,
                    scene.Document,
                    (r, agg, instances, roles) => UploadBlock(r, agg, instances, roles, surface),
                    b => UploadRoleChanges(b, surface));
                if (searchRange)
                {
                    DrawBlock(block, scene, surface, RoiDrawPass.SearchRange, selected, transform);
                }
                else
                {
                    DrawBlock(block, scene, surface, RoiDrawPass.Shadow, selected, transform);
                    DrawBlock(block, scene, surface, RoiDrawPass.Stroke, selected, transform);
                }
            }
        }
    }

    private unsafe Block UploadBlock(RoiSpatialBlock region, double aggregateSize, ReadOnlySpan<RoiInstance> instances, RoiRoleCache roles, Dx12Surface surface)
    {
        var createStart = _profileEnabled ? Stopwatch.GetTimestamp() : 0;
        var geometryBytes = checked((ulong)instances.Length * RoiInstance.SizeInBytes);
        var roleBytes = checked((ulong)roles.Values.Length * RoiInstance.RoleSizeInBytes);
        var geometryAllocation = _buffers.Allocate(Math.Max(geometryBytes, RoiInstance.SizeInBytes));
        RoiBufferArena.Lease? roleAllocation = null;
        try
        {
            roleAllocation = _buffers.Allocate(Math.Max(roleBytes, RoiInstance.RoleSizeInBytes));
            if (_profileEnabled)
            {
                CreateMilliseconds += Stopwatch.GetElapsedTime(createStart).TotalMilliseconds;
            }

            var uploadStart = _profileEnabled ? Stopwatch.GetTimestamp() : 0;
            geometryAllocation.Page.Transition(surface.Commands, ResourceStates.CopyDest);
            if (!ReferenceEquals(roleAllocation.Page, geometryAllocation.Page))
            {
                roleAllocation.Page.Transition(surface.Commands, ResourceStates.CopyDest);
            }

            if (geometryBytes > 0)
            {
                var geometryUpload = _uploads[surface.FrameIndex].Allocate(geometryBytes);
                instances.CopyTo(new Span<RoiInstance>((void*)geometryUpload.Pointer, instances.Length));
                surface.Commands.CopyBufferRegion(geometryAllocation.Buffer, geometryAllocation.Offset, geometryUpload.Buffer, geometryUpload.Offset, geometryBytes);
            }

            if (roleBytes > 0)
            {
                var roleUpload = _uploads[surface.FrameIndex].Allocate(roleBytes);
                roles.Values.AsSpan().CopyTo(new Span<uint>((void*)roleUpload.Pointer, roles.Values.Length));
                surface.Commands.CopyBufferRegion(roleAllocation.Buffer, roleAllocation.Offset, roleUpload.Buffer, roleUpload.Offset, roleBytes);
            }

            geometryAllocation.Page.Transition(surface.Commands, ResourceStates.NonPixelShaderResource);
            if (!ReferenceEquals(roleAllocation.Page, geometryAllocation.Page))
            {
                roleAllocation.Page.Transition(surface.Commands, ResourceStates.NonPixelShaderResource);
            }

            UploadedBytes += (long)(geometryBytes + roleBytes);
            if (_profileEnabled)
            {
                UploadMilliseconds += Stopwatch.GetElapsedTime(uploadStart).TotalMilliseconds;
            }

            return new(geometryAllocation, roleAllocation, region.Bounds.Center, instances.Length, roles, aggregateSize);
        }
        catch
        {
            // 이미 기록한 복사 명령이 있을 수 있으므로 실패한 구간도 GPU 완료까지 보관한다.
            geometryAllocation.Retire();
            surface.Retire(geometryAllocation);
            if (roleAllocation is not null)
            {
                roleAllocation.Retire();
                surface.Retire(roleAllocation);
            }

            throw;
        }
    }

    private unsafe void UploadRoleChanges(Block block, Dx12Surface surface)
    {
        var roleBytes = checked((ulong)block.Roles.Values.Length * RoiInstance.RoleSizeInBytes);
        if (roleBytes == 0)
        {
            return;
        }

        var roleAllocation = _buffers.Allocate(Math.Max(roleBytes, RoiInstance.RoleSizeInBytes));
        try
        {
            var upload = _uploads[surface.FrameIndex].Allocate(roleBytes);
            block.Roles.Values.AsSpan().CopyTo(new Span<uint>((void*)upload.Pointer, block.Roles.Values.Length));
            roleAllocation.Page.Transition(surface.Commands, ResourceStates.CopyDest);
            surface.Commands.CopyBufferRegion(roleAllocation.Buffer, roleAllocation.Offset, upload.Buffer, upload.Offset, roleBytes);
            roleAllocation.Page.Transition(surface.Commands, ResourceStates.NonPixelShaderResource);
            UploadedBytes += (long)roleBytes;
        }
        catch
        {
            roleAllocation.Retire();
            surface.Retire(roleAllocation);
            throw;
        }

        var previousRoleAllocation = block.RoleAllocation;
        block.RoleAllocation = roleAllocation;
        previousRoleAllocation.Retire();
        surface.Retire(previousRoleAllocation);
    }

    private unsafe void DrawBlock(Block block, RenderScene scene, Dx12Surface surface, RoiDrawPass pass, bool selected, RigidTransform2D transform)
    {
        var constants = new RoiDrawConstants(scene, block.Origin, transform, selected, pass);
        surface.Commands.SetGraphicsRoot32BitConstants(DrawConstantsRootParameter, RoiDrawConstants.RootConstantCount, &constants, 0);
        surface.Commands.SetGraphicsRootShaderResourceView(GeometryRootParameter, block.GeometryAllocation.Buffer.GPUVirtualAddress + block.GeometryAllocation.Offset);
        surface.Commands.SetGraphicsRootShaderResourceView(RolesRootParameter, block.RoleAllocation.Buffer.GPUVirtualAddress + block.RoleAllocation.Offset);
        surface.Commands.DrawInstanced(RoiDrawConstants.VerticesPerInstance, (uint)block.Count, 0, 0);
    }

    public void Dispose()
    {
        _cache.Dispose();
        _buffers.Dispose();
        foreach (var upload in _uploads)
        {
            upload.Dispose();
        }

        _pipeline.Dispose();
        _root.Dispose();
    }
}
