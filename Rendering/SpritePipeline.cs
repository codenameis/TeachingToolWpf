using System.IO;
using System.Runtime.InteropServices;
using TeachingToolWpf.Core.Imaging;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.D3DCompiler;
using Vortice.DXGI;

namespace TeachingToolWpf.Rendering;

internal sealed class GpuTexture(ID3D12Resource resource, int slot, PixelKind kind, int width, int height, Action<int> releaseSlot) : IDisposable
{
    public ID3D12Resource Resource { get; } = resource;
    public int Slot { get; } = slot;
    public PixelKind Kind { get; } = kind;
    public int Width { get; } = width;
    public int Height { get; } = height;
    public bool HasPixels { get; set; }
    public long Bytes => (long)Width * Height * (Kind == PixelKind.Gray8 ? 1 : Kind == PixelKind.Gray16 ? 2 : 4);
    public void Dispose() { Resource.Dispose(); releaseSlot(Slot); }
}

/// <summary>Owns descriptor allocation and frame-local vertex buffers. Called only by the render thread.</summary>
internal sealed class SpritePipeline : IDisposable
{
    private readonly Dx12Device gpu;
    private readonly ID3D12RootSignature root;
    private readonly ID3D12PipelineState pipeline;
    private readonly ID3D12DescriptorHeap heap;
    private readonly Stack<int> free = new(Enumerable.Range(0, 2048).Reverse());
    private readonly ID3D12Resource?[] vertices = new ID3D12Resource[2];
    private readonly int[] capacities = new int[2];
    private readonly uint increment;
    private readonly FrameUploadArena[] uploads;
    public SpritePipeline(Dx12Device gpu)
    {
        this.gpu = gpu;
        uploads = [new(gpu), new(gpu)];
        heap = gpu.Device.CreateDescriptorHeap(new(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 2048, DescriptorHeapFlags.ShaderVisible));
        increment = gpu.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
        root = gpu.Device.CreateRootSignature(new RootSignatureDescription1(RootSignatureFlags.AllowInputAssemblerInputLayout,
            [new(new RootDescriptorTable1(new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, 0)), ShaderVisibility.Pixel),
             new(new RootConstants(0, 0, 2), ShaderVisibility.Pixel)],
            [new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) { Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp },
             new StaticSamplerDescription(ShaderVisibility.Pixel, 1, 0) { Filter = Filter.MinMagMipPoint, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp }]));
        var shader = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "Viewport.hlsl"));
        pipeline = gpu.Device.CreateGraphicsPipelineState<ID3D12PipelineState>(new GraphicsPipelineStateDescription
        {
            RootSignature = root,
            VertexShader = Compiler.Compile(shader, "VSMain", "Viewport.hlsl", "vs_5_1"),
            PixelShader = Compiler.Compile(shader, "PSMain", "Viewport.hlsl", "ps_5_1"),
            InputLayout = new InputLayoutDescription(new InputElementDescription("POSITION", 0, Format.R32G32_Float, 0, 0),
                new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 8, 0), new InputElementDescription("COLOR", 0, Format.R32G32B32A32_Float, 16, 0)),
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, RasterizerState = RasterizerDescription.CullNone,
            BlendState = BlendDescription.NonPremultiplied, DepthStencilState = DepthStencilDescription.None,
            RenderTargetFormats = [Format.B8G8R8A8_UNorm], SampleDescription = new(1, 0)
        });
    }
    private CpuDescriptorHandle Cpu(int slot) => heap.GetCPUDescriptorHandleForHeapStart() + slot * (int)increment;
    private GpuDescriptorHandle Gpu(int slot) => heap.GetGPUDescriptorHandleForHeapStart() + slot * (int)increment;
    public void BeginFrame(Dx12Surface surface) => uploads[surface.FrameIndex].Reset();
    public GpuTexture CreateTexture(int width, int height, PixelKind kind)
    {
        if (free.Count == 0) throw new InvalidOperationException("GPU 타일 슬롯이 소진되었습니다.");
        var format = kind switch { PixelKind.Gray8 => Format.R8_UNorm, PixelKind.Gray16 => Format.R16_UNorm, PixelKind.Bgra32 => Format.B8G8R8A8_UNorm, _ => Format.R8G8B8A8_UNorm };
        var description = ResourceDescription.Texture2D(format, (uint)width, (uint)height, 1, 1);
        var texture = gpu.Device.CreateCommittedResource(HeapType.Default, description, ResourceStates.CopyDest);
        try
        {
            var slot = free.Pop();
            gpu.Device.CreateShaderResourceView(texture, new ShaderResourceViewDescription
            {
                Format = format, ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D, Shader4ComponentMapping = 5768,
                Texture2D = new Texture2DShaderResourceView { MipLevels = 1 }
            }, Cpu(slot));
            return new(texture, slot, kind, width, height, free.Push);
        }
        catch { texture.Dispose(); throw; }
    }
    public GpuTexture Upload(PixelBuffer pixels, Dx12Surface surface)
    {
        var texture = CreateTexture(pixels.Width, pixels.Height, pixels.Kind);
        try { Update(texture, pixels, surface); return texture; }
        catch { texture.Dispose(); throw; }
    }
    public unsafe void Update(GpuTexture texture, PixelBuffer pixels, Dx12Surface surface)
    {
        if (texture.Width != pixels.Width || texture.Height != pixels.Height || texture.Kind != pixels.Kind) throw new ArgumentException("텍스처와 프레임 형식이 다릅니다.");
        var footprint = new PlacedSubresourceFootPrint();
        gpu.Device.GetCopyableFootprints(texture.Resource.Description, 0, 1, 0, &footprint, null, null, out var bytes);
        var upload = uploads[surface.FrameIndex].Allocate(bytes);
        var target = (byte*)upload.Pointer;
        if (pixels.Stride == footprint.Footprint.RowPitch) pixels.Span.CopyTo(new Span<byte>(target, pixels.Length));
        else for (var y = 0; y < pixels.Height; y++) pixels.Span.Slice(y * pixels.Stride, pixels.Stride).CopyTo(new Span<byte>(target + y * footprint.Footprint.RowPitch, pixels.Stride));
        footprint.Offset = upload.Offset;
        if (texture.HasPixels) surface.Commands.ResourceBarrierTransition(texture.Resource, ResourceStates.PixelShaderResource, ResourceStates.CopyDest);
        surface.Commands.CopyTextureRegion(new TextureCopyLocation(texture.Resource, 0), 0, 0, 0, new TextureCopyLocation(upload.Buffer, footprint));
        surface.Commands.ResourceBarrierTransition(texture.Resource, ResourceStates.CopyDest, ResourceStates.PixelShaderResource);
        texture.HasPixels = true;
    }
    public unsafe void Draw(SpriteMesh mesh, Dx12Surface surface)
    {
        if (mesh.Vertices.Count == 0) return;
        var index = surface.FrameIndex;
        var size = checked(mesh.Vertices.Count * 32);
        if (capacities[index] < size)
        {
            vertices[index]?.Dispose();
            capacities[index] = Math.Max(size * 2, 1024 * 1024);
            vertices[index] = gpu.Device.CreateCommittedResource(HeapType.Upload, ResourceDescription.Buffer((ulong)capacities[index]), ResourceStates.GenericRead);
        }
        var buffer = vertices[index]!;
        CollectionsMarshal.AsSpan(mesh.Vertices).CopyTo(new Span<SpriteVertex>(buffer.Map<SpriteVertex>(0), mesh.Vertices.Count));
        buffer.Unmap(0);
        var commands = surface.Commands;
        commands.SetDescriptorHeaps(heap);
        commands.SetGraphicsRootSignature(root); commands.SetPipelineState(pipeline);
        commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        commands.IASetVertexBuffers(0, new VertexBufferView(buffer.GPUVirtualAddress, (uint)size, 32));
        foreach (var batch in mesh.Batches)
        {
            commands.SetGraphicsRootDescriptorTable(0, Gpu(batch.Texture.Slot));
            commands.SetGraphicsRoot32BitConstant(1, batch.Texture.Kind is PixelKind.Gray8 or PixelKind.Gray16 ? 1u : 0u, 0);
            commands.SetGraphicsRoot32BitConstant(1, batch.PointSampling ? 1u : 0u, 1);
            commands.DrawInstanced((uint)batch.Count, 1, (uint)batch.Start, 0);
        }
    }
    public void Dispose()
    {
        foreach (var buffer in vertices) buffer?.Dispose();
        foreach (var arena in uploads) arena.Dispose();
        pipeline.Dispose(); root.Dispose(); heap.Dispose();
    }
}
