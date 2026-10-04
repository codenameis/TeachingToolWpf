using System.IO;
using System.Runtime.InteropServices;
using TeachingToolWpf.Core.Imaging;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace TeachingToolWpf.Rendering;

internal sealed class GpuTexture(ID3D12Resource resource, int slot, PixelKind kind, int width, int height, Action<int> releaseSlot) : IDisposable
{
    public ID3D12Resource Resource
    {
        get;
    } = resource;

    public int Slot
    {
        get;
    } = slot;

    public PixelKind Kind
    {
        get;
    } = kind;

    public int Width
    {
        get;
    } = width;

    public int Height
    {
        get;
    } = height;

    public bool HasPixels
    {
        get;
        set;
    }

    public long Bytes => (long)Width * Height * (Kind == PixelKind.Gray8 ? 1 : Kind == PixelKind.Gray16 ? 2 : 4);

    public void Dispose()
    {
        Resource.Dispose();
        releaseSlot(Slot);
    }
}

/// <summary>Owns descriptor allocation and frame-local vertex buffers. Called only by the render thread.</summary>
internal sealed class SpritePipeline : IDisposable
{
    private readonly Dx12Device _gpu;
    private readonly ID3D12RootSignature _root;
    private readonly ID3D12PipelineState _pipeline;
    private readonly ID3D12DescriptorHeap _heap;
    private readonly Stack<int> _free = new(Enumerable.Range(0, 2048).Reverse());
    private readonly ID3D12Resource?[] _vertices = new ID3D12Resource[2];
    private readonly int[] _capacities = new int[2];
    private readonly uint _increment;
    private readonly FrameUploadArena[] _uploads;

    public SpritePipeline(Dx12Device gpu)
    {
        _gpu = gpu;
        _uploads = [new(gpu), new(gpu)];
        _heap = gpu.Device.CreateDescriptorHeap(new(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 2048, DescriptorHeapFlags.ShaderVisible));
        _increment = gpu.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
        _root = gpu.Device.CreateRootSignature(new RootSignatureDescription1(RootSignatureFlags.AllowInputAssemblerInputLayout,
            [new(new RootDescriptorTable1(new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, 0)), ShaderVisibility.Pixel),
             new(new RootConstants(0, 0, 2), ShaderVisibility.Pixel)],
            [new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) { Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp },
             new StaticSamplerDescription(ShaderVisibility.Pixel, 1, 0) { Filter = Filter.MinMagMipPoint, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp }]));
        var shaderPath = Path.Combine(AppContext.BaseDirectory, "Shaders", "Viewport.hlsl");
        _pipeline = gpu.Device.CreateGraphicsPipelineState<ID3D12PipelineState>(new GraphicsPipelineStateDescription
        {
            RootSignature = _root,
            VertexShader = ShaderCompiler.CompileFile(shaderPath, "VSMain", "vs_5_1"),
            PixelShader = ShaderCompiler.CompileFile(shaderPath, "PSMain", "ps_5_1"),
            InputLayout = new InputLayoutDescription(new InputElementDescription("POSITION", 0, Format.R32G32_Float, 0, 0),
                new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 8, 0), new InputElementDescription("COLOR", 0, Format.R32G32B32A32_Float, 16, 0)),
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RasterizerState = RasterizerDescription.CullNone,
            BlendState = BlendDescription.NonPremultiplied,
            DepthStencilState = DepthStencilDescription.None,
            RenderTargetFormats = [Format.B8G8R8A8_UNorm],
            SampleDescription = new(1, 0)
        });
    }

    private CpuDescriptorHandle Cpu(int slot) => _heap.GetCPUDescriptorHandleForHeapStart() + (slot * (int)_increment);

    private GpuDescriptorHandle Gpu(int slot) => _heap.GetGPUDescriptorHandleForHeapStart() + (slot * (int)_increment);

    public void BeginFrame(Dx12Surface surface) => _uploads[surface.FrameIndex].Reset();

    public GpuTexture CreateTexture(int width, int height, PixelKind kind)
    {
        if (_free.Count == 0)
        {
            throw new InvalidOperationException("GPU 타일 슬롯이 소진되었습니다.");
        }

        var format = kind switch
        {
            PixelKind.Gray8 => Format.R8_UNorm,
            PixelKind.Gray16 => Format.R16_UNorm,
            PixelKind.Bgra32 => Format.B8G8R8A8_UNorm,
            _ => Format.R8G8B8A8_UNorm
        };
        var description = ResourceDescription.Texture2D(format, (uint)width, (uint)height, 1, 1);
        var texture = _gpu.Device.CreateCommittedResource(HeapType.Default, description, ResourceStates.CopyDest);
        try
        {
            var slot = _free.Pop();
            _gpu.Device.CreateShaderResourceView(texture, new ShaderResourceViewDescription
            {
                Format = format,
                ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
                Shader4ComponentMapping = 5768,
                Texture2D = new Texture2DShaderResourceView { MipLevels = 1 }
            }, Cpu(slot));
            return new(texture, slot, kind, width, height, _free.Push);
        }
        catch
        {
            texture.Dispose();
            throw;
        }
    }

    public GpuTexture Upload(PixelBuffer pixels, Dx12Surface surface)
    {
        var texture = CreateTexture(pixels.Width, pixels.Height, pixels.Kind);
        try
        {
            Update(texture, pixels, surface);
            return texture;
        }
        catch
        {
            texture.Dispose();
            throw;
        }
    }

    public unsafe void Update(GpuTexture texture, PixelBuffer pixels, Dx12Surface surface)
    {
        if (texture.Width != pixels.Width || texture.Height != pixels.Height || texture.Kind != pixels.Kind)
        {
            throw new ArgumentException("텍스처와 프레임 형식이 다릅니다.");
        }

        var footprint = new PlacedSubresourceFootPrint();
        _gpu.Device.GetCopyableFootprints(texture.Resource.Description, 0, 1, 0, &footprint, null, null, out var bytes);
        var upload = _uploads[surface.FrameIndex].Allocate(bytes);
        var target = (byte*)upload.Pointer;
        if (pixels.Stride == footprint.Footprint.RowPitch)
        {
            pixels.Span.CopyTo(new Span<byte>(target, pixels.Length));
        }
        else
        {
            for (var y = 0;
y < pixels.Height;
y++)
            {
                pixels.Span.Slice(y * pixels.Stride, pixels.Stride).CopyTo(new Span<byte>(target + (y * footprint.Footprint.RowPitch), pixels.Stride));
            }
        }

        footprint.Offset = upload.Offset;
        if (texture.HasPixels)
        {
            surface.Commands.ResourceBarrierTransition(texture.Resource, ResourceStates.PixelShaderResource, ResourceStates.CopyDest);
        }

        surface.Commands.CopyTextureRegion(new TextureCopyLocation(texture.Resource, 0), 0, 0, 0, new TextureCopyLocation(upload.Buffer, footprint));
        surface.Commands.ResourceBarrierTransition(texture.Resource, ResourceStates.CopyDest, ResourceStates.PixelShaderResource);
        texture.HasPixels = true;
    }

    public unsafe void Draw(SpriteMesh mesh, Dx12Surface surface, int middleBatch = -1, Action? middle = null)
    {
        if (mesh.Vertices.Count == 0)
        {
            middle?.Invoke();
            return;
        }
        var index = surface.FrameIndex;
        var size = checked(mesh.Vertices.Count * 32);
        if (_capacities[index] < size)
        {
            _vertices[index]?.Dispose();
            _capacities[index] = Math.Max(size * 2, 1024 * 1024);
            _vertices[index] = _gpu.Device.CreateCommittedResource(HeapType.Upload, ResourceDescription.Buffer((ulong)_capacities[index]), ResourceStates.GenericRead);
        }
        var buffer = _vertices[index]!;
        CollectionsMarshal.AsSpan(mesh.Vertices).CopyTo(new Span<SpriteVertex>(buffer.Map<SpriteVertex>(0), mesh.Vertices.Count));
        buffer.Unmap(0);
        var commands = surface.Commands;
        Bind();
        for (var i = 0;
i < mesh.Batches.Count;
i++)
        {
            if (i == middleBatch)
            {
                middle?.Invoke();
                Bind();
            }
            var batch = mesh.Batches[i];
            commands.SetGraphicsRootDescriptorTable(0, Gpu(batch.Texture.Slot));
            commands.SetGraphicsRoot32BitConstant(1, batch.Texture.Kind is PixelKind.Gray8 or PixelKind.Gray16 ? 1u : 0u, 0);
            commands.SetGraphicsRoot32BitConstant(1, batch.PointSampling ? 1u : 0u, 1);
            commands.DrawInstanced((uint)batch.Count, 1, (uint)batch.Start, 0);
        }
        if (middleBatch == mesh.Batches.Count)
        {
            middle?.Invoke();
        }

        void Bind()
        {
            commands.SetDescriptorHeaps(_heap);
            commands.SetGraphicsRootSignature(_root);
            commands.SetPipelineState(_pipeline);
            commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            commands.IASetVertexBuffers(0, new VertexBufferView(buffer.GPUVirtualAddress, (uint)size, 32));
        }
    }

    public void Dispose()
    {
        foreach (var buffer in _vertices)
        {
            buffer?.Dispose();
        }

        foreach (var arena in _uploads)
        {
            arena.Dispose();
        }

        _pipeline.Dispose();
        _root.Dispose();
        _heap.Dispose();
    }
}
