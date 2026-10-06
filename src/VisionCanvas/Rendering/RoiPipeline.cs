using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
    private const double ViewportPaddingDip = 8;
    private const double SummarySizeDip = 6;

    private sealed class KeyComparer : IEqualityComparer<(object Identity, int? Level)>
    {
        public bool Equals((object Identity, int? Level) a, (object Identity, int? Level) b)
            => ReferenceEquals(a.Identity, b.Identity) && a.Level == b.Level;

        public int GetHashCode((object Identity, int? Level) key)
            => HashCode.Combine(RuntimeHelpers.GetHashCode(key.Identity), key.Level);
    }
    private sealed record Block(RoiBufferArena.Lease Allocation, PointD Origin, int Count, RoiRoleCache Roles) : IDisposable
    {
        public long LastUsed
        {
            get;
            set;
        }

        public ulong RoleOffset => Allocation.Offset + ((ulong)Count * RoiInstance.SizeInBytes);

        public long OwnedBytes => (long)Count * RoiInstance.RoleSizeInBytes;

        public double FullDetailAggregateSize
        {
            get;
            set;
        }

        public void Dispose() => Allocation.Dispose();
    }
    private readonly bool _profileEnabled;
    private readonly ID3D12RootSignature _root;
    private readonly ID3D12PipelineState _pipeline;
    private readonly FrameUploadArena[] _uploads;
    private readonly RoiBufferArena _buffers;
    // null 단계는 요약 없는 형상 한 벌이다. 다른 단계에 같은 Block 별칭을 등록하지 않는다.
    private readonly Dictionary<(object Identity, int? Level), Block> _blocks = new(new KeyComparer());
    private readonly RoiCacheMemory _cacheMemory = new();
    private readonly List<RoiInstance> _packingInstances = [];
    private readonly List<uint> _packingRoles = [];
    private readonly Dictionary<RoiGeometry, int> _packingUnique = [];
    private long _frame;

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

    public long CacheBytes => _cacheMemory.Bytes + _buffers.ActiveAllocationBytes;

    public double PackMilliseconds
    {
        get; private set;
    }
    public double CreateMilliseconds
    {
        get; private set;
    }
    public double UploadMilliseconds
    {
        get; private set;
    }

    public RoiPipeline(Dx12Device gpu, bool profileEnabled = false)
    {
        _profileEnabled = profileEnabled;
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
        _frame++;
        UploadedBytes = 0;
        InstanceCount = 0;
        PackMilliseconds = 0;
        CreateMilliseconds = 0;
        UploadMilliseconds = 0;
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
        var key = (region.Identity, (int?)level);
        var fullDetailKey = (region.Identity, (int?)null);
        _blocks.TryGetValue(fullDetailKey, out var fullDetail);
        // BVH에서 T 크기에 요약이 없었다면 더 작은 크기에도 요약이 생기지 않는다.
        var block = fullDetail is not null && aggregateSize <= fullDetail.FullDetailAggregateSize
            ? fullDetail : _blocks.GetValueOrDefault(key);
        if (block is not null)
        {
            var roleAggregate = ReferenceEquals(block, fullDetail) ? block.FullDetailAggregateSize : aggregateSize;
            if (block.Roles.Refresh(region, roleAggregate, document, block.Count))
            {
                UploadRoleChanges(block, surface);
            }

            return block;
        }

        var packStart = _profileEnabled ? Stopwatch.GetTimestamp() : 0;
        try
        {
            var hasSummary = RoiInstance.PackInto(region, aggregateSize, document, _packingInstances, _packingRoles, _packingUnique);
            // 역할은 보관 캐시가 소유한다. 형상 scratch는 아래 동기 업로드가 끝나면 바로 재사용한다.
            var packedRoles = _packingRoles.ToArray();
            if (!hasSummary && fullDetail is not null)
            {
                var rolesChanged = fullDetail.Roles.RefreshPacked(document, packedRoles, fullDetail.Count);
                if (_profileEnabled)
                {
                    PackMilliseconds += Stopwatch.GetElapsedTime(packStart).TotalMilliseconds;
                }
                if (rolesChanged)
                {
                    UploadRoleChanges(fullDetail, surface);
                }
                fullDetail.FullDetailAggregateSize = aggregateSize;
                return fullDetail;
            }

            var roles = new RoiRoleCache(document, packedRoles);
            if (_profileEnabled)
            {
                PackMilliseconds += Stopwatch.GetElapsedTime(packStart).TotalMilliseconds;
            }
            block = UploadBlock(region, CollectionsMarshal.AsSpan(_packingInstances), roles, surface);
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

    private void EvictInactiveBlocks(Dx12Surface surface)
    {
        if (CacheBytes <= CacheBudgetBytes)
        {
            return;
        }

        // 완전히 비활성인 페이지부터 비워 GPU 할당 비용도 함께 줄인다.
        var coldPages = _blocks.GroupBy(p => p.Value.Allocation.Page)
            .Where(page => page.All(p => p.Value.LastUsed != _frame))
            .OrderBy(page => page.Max(p => p.Value.LastUsed)).ToArray();
        foreach (var page in coldPages)
        {
            if (CacheBytes <= CacheBudgetBytes)
            {
                return;
            }
            foreach (var pair in page)
            {
                Retire(pair.Key, pair.Value);
            }
        }

        // 같은 페이지의 hot 블록이 큰 CPU 원본까지 붙잡지 않도록 남은 cold 블록도 퇴출한다.
        foreach (var pair in _blocks.Where(p => p.Value.LastUsed != _frame).OrderBy(p => p.Value.LastUsed).ToArray())
        {
            if (CacheBytes <= CacheBudgetBytes)
            {
                break;
            }

            Retire(pair.Key, pair.Value);
        }

        void Retire((object Identity, int? Level) key, Block block)
        {
            _blocks.Remove(key);
            _cacheMemory.Remove(key.Identity, block.OwnedBytes);
            block.Allocation.Retire();
            surface.Retire(block);
        }
    }

    private unsafe Block UploadBlock(RoiSpatialBlock region, ReadOnlySpan<RoiInstance> instances, RoiRoleCache roles, Dx12Surface surface)
    {
        var createStart = _profileEnabled ? Stopwatch.GetTimestamp() : 0;
        var geometryBytes = checked((ulong)instances.Length * RoiInstance.SizeInBytes);
        var roleBytes = checked((ulong)roles.Values.Length * RoiInstance.RoleSizeInBytes);
        // 작은 요약 블록도 공유 페이지의 [형상][역할] 구간만 차지한다.
        var allocation = _buffers.Allocate(Math.Max(geometryBytes + roleBytes, RoiInstance.SizeInBytes));
        if (_profileEnabled)
        {
            CreateMilliseconds += Stopwatch.GetElapsedTime(createStart).TotalMilliseconds;
        }
        var uploadStart = _profileEnabled ? Stopwatch.GetTimestamp() : 0;
        try
        {
            allocation.Page.Transition(surface.Commands, ResourceStates.CopyDest);
            if (geometryBytes > 0)
            {
                var geometryUpload = _uploads[surface.FrameIndex].Allocate(geometryBytes);
                instances.CopyTo(new Span<RoiInstance>((void*)geometryUpload.Pointer, instances.Length));
                surface.Commands.CopyBufferRegion(allocation.Buffer, allocation.Offset, geometryUpload.Buffer, geometryUpload.Offset, geometryBytes);

                var roleUpload = _uploads[surface.FrameIndex].Allocate(roleBytes);
                roles.Values.AsSpan().CopyTo(new Span<uint>((void*)roleUpload.Pointer, roles.Values.Length));
                surface.Commands.CopyBufferRegion(allocation.Buffer, allocation.Offset + geometryBytes, roleUpload.Buffer, roleUpload.Offset, roleBytes);
            }
            allocation.Page.Transition(surface.Commands, ResourceStates.NonPixelShaderResource);
            UploadedBytes += (long)(geometryBytes + roleBytes);
            if (_profileEnabled)
            {
                UploadMilliseconds += Stopwatch.GetElapsedTime(uploadStart).TotalMilliseconds;
            }
            return new(allocation, region.Bounds.Center, instances.Length, roles);
        }
        catch
        {
            // 이미 기록한 복사 명령이 있을 수 있으므로 실패한 구간도 GPU 완료까지 보관한다.
            allocation.Retire();
            surface.Retire(allocation);
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
        block.Allocation.Page.Transition(surface.Commands, ResourceStates.CopyDest);
        surface.Commands.CopyBufferRegion(block.Allocation.Buffer, block.RoleOffset, upload.Buffer, upload.Offset, roleBytes);
        block.Allocation.Page.Transition(surface.Commands, ResourceStates.NonPixelShaderResource);
        UploadedBytes += (long)roleBytes;
    }

    private unsafe void DrawBlock(Block block, RenderScene scene, Dx12Surface surface, RoiDrawPass pass, bool selected, RigidTransform2D transform)
    {
        var constants = new RoiDrawConstants(scene, block.Origin, transform, selected, pass);
        surface.Commands.SetGraphicsRoot32BitConstants(DrawConstantsRootParameter, RoiDrawConstants.RootConstantCount, &constants, 0);
        surface.Commands.SetGraphicsRootShaderResourceView(GeometryRootParameter, block.Allocation.Buffer.GPUVirtualAddress + block.Allocation.Offset);
        surface.Commands.SetGraphicsRootShaderResourceView(RolesRootParameter, block.Allocation.Buffer.GPUVirtualAddress + block.RoleOffset);
        surface.Commands.DrawInstanced(VerticesPerInstance, (uint)block.Count, 0, 0);
    }

    public void Dispose()
    {
        foreach (var block in _blocks.Values)
        {
            block.Dispose();
        }
        _blocks.Clear();
        _cacheMemory.Clear();
        _buffers.Dispose();

        foreach (var upload in _uploads)
        {
            upload.Dispose();
        }

        _pipeline.Dispose();
        _root.Dispose();
    }
}

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
