using System.IO;
using System.Runtime.CompilerServices;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
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
    // 사각형은 네 변, 선분/원은 첫 여섯 정점만 사용한다. 하나의 보관 배치로 제출한다.
    private const int VerticesPerInstance = 4 * 6;
    private const long CacheBudgetBytes = 128L * 1024 * 1024;
    private const int EstimatedSourceRoiBytes = 256;
    private const double ViewportPaddingDip = 8;
    private const double SummarySizeDip = 6;

    private sealed class KeyComparer : IEqualityComparer<(object Identity, int Level)>
    {
        public bool Equals((object Identity, int Level) a, (object Identity, int Level) b)
            => ReferenceEquals(a.Identity, b.Identity) && a.Level == b.Level;

        public int GetHashCode((object Identity, int Level) key)
            => HashCode.Combine(RuntimeHelpers.GetHashCode(key.Identity), key.Level);
    }
    private sealed record Block(ID3D12Resource Buffer, PointD Origin, int Count, int SourceCount, long AllocationBytes, RoiRoleCache Roles, long ShapeBytes) : IDisposable
    {
        public long LastUsed
        {
            get;
            set;
        }

        public ulong RoleOffset => (ulong)Count * RoiInstance.SizeInBytes;

        public long CacheCost => AllocationBytes + ((long)SourceCount * EstimatedSourceRoiBytes) + ((long)Count * RoiInstance.RoleSizeInBytes) + ShapeBytes;

        public void Dispose() => Buffer.Dispose();
    }
    private readonly Dx12Device _gpu;
    private readonly ID3D12RootSignature _root;
    private readonly ID3D12PipelineState _pipeline;
    private readonly FrameUploadArena[] _uploads;
    private readonly Dictionary<(object Identity, int Level), Block> _blocks = new(new KeyComparer());
    private long _frame;

    private long _bytes;

    public long UploadedBytes
    {
        get;
        private set;
    }

    public long InstanceCount
    {
        get;
        private set;
    }

    public long CacheBytes => _bytes;

    public RoiPipeline(Dx12Device gpu)
    {
        _gpu = gpu;
        _uploads = [new(gpu), new(gpu)];
        _root = gpu.Device.CreateRootSignature(new RootSignatureDescription1(RootSignatureFlags.None,
            [new(RootParameterType.ShaderResourceView, new RootDescriptor1(GeometryShaderRegister, RegisterSpace), ShaderVisibility.Vertex),
             new(new RootConstants(OptionsShaderRegister, RegisterSpace, RoiDrawConstants.RootConstantCount), ShaderVisibility.All),
             new(RootParameterType.ShaderResourceView, new RootDescriptor1(RolesShaderRegister, RegisterSpace), ShaderVisibility.Vertex)]));
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

    public void Draw(RenderScene scene, Dx12Surface surface)
    {
        _frame++;
        UploadedBytes = 0;
        InstanceCount = 0;
        _uploads[surface.FrameIndex].Reset();

        var view = scene.Document.Viewport;
        var topLeft = view.ScreenToImage(new(-ViewportPaddingDip, -ViewportPaddingDip));
        var bottomRight = view.ScreenToImage(new(scene.Width + ViewportPaddingDip, scene.Height + ViewportPaddingDip));
        var visible = new BoundsD(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y);
        var level = (int)Math.Floor(Math.Log2(SummarySizeDip / view.Scale));
        var aggregateSize = Math.Pow(2, level);

        surface.Commands.SetGraphicsRootSignature(_root);
        surface.Commands.SetPipelineState(_pipeline);
        surface.Commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        if (scene.Transform is { } preview)
        {
            DrawIndex(preview.Selection.Stationary, selected: false, transform: default);
            if (preview.Selection.Affected.Count != scene.Document.Selection.Count)
            {
                DrawIndex(preview.Selection.Moving, selected: false, preview.Transform);
            }

            DrawIndex(scene.Document.SelectedSpatial, selected: true, preview.Transform);
        }
        else
        {
            if (scene.Document.Selection.Count != scene.Document.Rois.Count)
            {
                DrawIndex(scene.Document.Snapshot.Spatial, selected: false, transform: default);
            }

            if (scene.Document.Selection.Count > 0)
            {
                DrawIndex(scene.Document.SelectedSpatial, selected: true, transform: default);
            }
        }
        EvictInactiveBlocks(surface);

        void DrawIndex(RoiSpatialIndex index, bool selected, RigidTransform2D transform)
        {
            var query = transform.InverseTransformBounds(visible);
            foreach (var region in index.Blocks(query))
            {
                var block = GetOrUploadBlock(region, level, aggregateSize, scene.Document, surface);
                block.LastUsed = _frame;
                InstanceCount += block.Count;
                DrawBlock(block, scene, surface, RoiDrawPass.Shadow, selected, transform);
                DrawBlock(block, scene, surface, RoiDrawPass.Stroke, selected, transform);
            }
        }
    }

    private Block GetOrUploadBlock(RoiSpatialBlock region, int level, double aggregateSize, DocumentState document, Dx12Surface surface)
    {
        var key = (region.Identity, level);
        if (_blocks.TryGetValue(key, out var block))
        {
            if (block.Roles.Refresh(region, aggregateSize, document, block.Count))
            {
                UploadRoleChanges(block, surface);
            }

            return block;
        }

        var instances = RoiInstance.Pack(region, aggregateSize);
        var roles = new RoiRoleCache();
        roles.Refresh(region, aggregateSize, document, instances.Length);
        block = UploadBlock(region, instances, roles, surface);
        _blocks.Add(key, block);
        _bytes += block.CacheCost;
        return block;
    }

    private void EvictInactiveBlocks(Dx12Surface surface)
    {
        if (_bytes <= CacheBudgetBytes)
        {
            return;
        }

        // pan/zoom/Undo에 쓸 최근 형상은 남긴다. GPU가 읽는 리소스의 실제 해제는 fence 이후다.
        foreach (var pair in _blocks.Where(p => p.Value.LastUsed != _frame).OrderBy(p => p.Value.LastUsed).ToArray())
        {
            if (_bytes <= CacheBudgetBytes)
            {
                break;
            }

            _blocks.Remove(pair.Key);
            _bytes -= pair.Value.CacheCost;
            surface.Retire(pair.Value);
        }
    }

    private unsafe Block UploadBlock(RoiSpatialBlock region, RoiInstance[] instances, RoiRoleCache roles, Dx12Surface surface)
    {
        var geometryBytes = checked((ulong)instances.Length * RoiInstance.SizeInBytes);
        var roleBytes = checked((ulong)roles.Values.Length * RoiInstance.RoleSizeInBytes);
        // 하나의 리소스를 [형상][역할]로 나눠 추가 committed allocation을 피한다.
        var description = ResourceDescription.Buffer(Math.Max(geometryBytes + roleBytes, RoiInstance.SizeInBytes));
        var allocation = _gpu.Device.GetResourceAllocationInfo(0, description).SizeInBytes;
        var buffer = _gpu.Device.CreateCommittedResource(HeapType.Default, description, ResourceStates.Common);
        try
        {
            surface.Commands.ResourceBarrierTransition(buffer, ResourceStates.Common, ResourceStates.CopyDest);
            if (geometryBytes > 0)
            {
                var geometryUpload = _uploads[surface.FrameIndex].Allocate(geometryBytes);
                instances.AsSpan().CopyTo(new Span<RoiInstance>((void*)geometryUpload.Pointer, instances.Length));
                surface.Commands.CopyBufferRegion(buffer, 0, geometryUpload.Buffer, geometryUpload.Offset, geometryBytes);

                var roleUpload = _uploads[surface.FrameIndex].Allocate(roleBytes);
                roles.Values.AsSpan().CopyTo(new Span<uint>((void*)roleUpload.Pointer, roles.Values.Length));
                surface.Commands.CopyBufferRegion(buffer, geometryBytes, roleUpload.Buffer, roleUpload.Offset, roleBytes);
            }
            surface.Commands.ResourceBarrierTransition(buffer, ResourceStates.CopyDest, ResourceStates.NonPixelShaderResource);
            UploadedBytes += (long)(geometryBytes + roleBytes);
            // 키가 유지하는 BVH의 폴리곤 꼭짓점 배열도 CPU 추정치에 포함한다. 공유 배열의 중복 계산은 보수적인 예산이다.
            return new(buffer, region.Bounds.Center, instances.Length, region.Count, checked((long)allocation), roles, region.ShapeBytes);
        }
        catch
        {
            buffer.Dispose();
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

        var upload = _uploads[surface.FrameIndex].Allocate(roleBytes);
        block.Roles.Values.AsSpan().CopyTo(new Span<uint>((void*)upload.Pointer, block.Roles.Values.Length));
        // 같은 direct queue에서 이전 draw 뒤에 복사한다. 형상 구간은 쓰지 않는다.
        surface.Commands.ResourceBarrierTransition(block.Buffer, ResourceStates.NonPixelShaderResource, ResourceStates.CopyDest);
        surface.Commands.CopyBufferRegion(block.Buffer, block.RoleOffset, upload.Buffer, upload.Offset, roleBytes);
        surface.Commands.ResourceBarrierTransition(block.Buffer, ResourceStates.CopyDest, ResourceStates.NonPixelShaderResource);
        UploadedBytes += (long)roleBytes;
    }

    private unsafe void DrawBlock(Block block, RenderScene scene, Dx12Surface surface, RoiDrawPass pass, bool selected, RigidTransform2D transform)
    {
        var constants = new RoiDrawConstants(scene, block.Origin, transform, selected, pass);
        surface.Commands.SetGraphicsRoot32BitConstants(DrawConstantsRootParameter, RoiDrawConstants.RootConstantCount, &constants, 0);
        surface.Commands.SetGraphicsRootShaderResourceView(GeometryRootParameter, block.Buffer.GPUVirtualAddress);
        surface.Commands.SetGraphicsRootShaderResourceView(RolesRootParameter, block.Buffer.GPUVirtualAddress + block.RoleOffset);
        surface.Commands.DrawInstanced(VerticesPerInstance, (uint)block.Count, 0, 0);
    }

    public void Dispose()
    {
        foreach (var block in _blocks.Values)
        {
            block.Dispose();
        }

        foreach (var upload in _uploads)
        {
            upload.Dispose();
        }

        _pipeline.Dispose();
        _root.Dispose();
    }
}
