using System.Runtime.InteropServices;
using VisionCanvas.Rendering.Abstractions;
using VisionImaging.Abstractions;
using WebGPU;
using static WebGPU.WebGPU;

namespace VisionCanvas.Wgpu.Rendering;

internal sealed unsafe class WgpuSpritePipeline : TexturePipelineBase<WgpuTexture, WgpuSurface>, IDisposable
{
    private const int UniformUintCount = 4;
    private const int BindGroupEntryCount = 5;

    private readonly WgpuDevice _gpu;
    private readonly WgpuFrameResources[] _frames;
    private readonly WgpuPipeline _pipeline;
    private WGPUSampler _linear;
    private WGPUSampler _point;

    public WgpuSpritePipeline(WgpuDevice gpu, WgpuSurface surface, WgpuFrameResources[] frames)
    {
        _gpu = gpu;
        _frames = frames;
        _pipeline = new(gpu, surface, "Viewport.wgsl", true);
        try
        {
            _linear = CreateSampler(WGPUFilterMode.Linear);
            _point = CreateSampler(WGPUFilterMode.Nearest);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private WGPUSampler CreateSampler(WGPUFilterMode filter)
    {
        var descriptor = new WGPUSamplerDescriptor
        {
            addressModeU = WGPUAddressMode.ClampToEdge,
            addressModeV = WGPUAddressMode.ClampToEdge,
            addressModeW = WGPUAddressMode.ClampToEdge,
            magFilter = filter,
            minFilter = filter,
            mipmapFilter = WGPUMipmapFilterMode.Nearest,
            maxAnisotropy = 1
        };
        var sampler = wgpuDeviceCreateSampler(_gpu.Handle, &descriptor);
        try
        {
            _gpu.CheckErrors();
            return sampler;
        }
        catch
        {
            if (sampler.IsNotNull)
            {
                wgpuSamplerRelease(sampler);
            }

            throw;
        }
    }

    public override WgpuTexture CreateTypedTexture(int width, int height, PixelKind kind)
        => new(_gpu, width, height, kind);

    public override WgpuTexture UploadTyped(PixelBuffer pixels, WgpuSurface surface)
    {
        var texture = new WgpuTexture(_gpu, pixels.Width, pixels.Height, pixels.Kind);
        try
        {
            UpdateTyped(texture, pixels, surface);
            texture.IsOpaque = TextureTransparency.PixelsAreOpaque(pixels);
            return texture;
        }
        catch
        {
            surface.Retire(texture);
            throw;
        }
    }

    public override void UpdateTyped(WgpuTexture texture, PixelBuffer pixels, WgpuSurface surface)
    {
        if (texture.Width != pixels.Width || texture.Height != pixels.Height || texture.Kind != pixels.Kind)
        {
            throw new ArgumentException("텍스처와 프레임 형식이 다릅니다.");
        }

        var destination = new WGPUTexelCopyTextureInfo { texture = texture.Handle, aspect = WGPUTextureAspect.All };
        var layout = new WGPUTexelCopyBufferLayout { bytesPerRow = (uint)(pixels.Length / pixels.Height), rowsPerImage = (uint)pixels.Height };
        var size = new WGPUExtent3D { width = (uint)pixels.Width, height = (uint)pixels.Height, depthOrArrayLayers = 1 };
        fixed (byte* source = pixels.Span)
        {
            wgpuQueueWriteTexture(_gpu.Queue, &destination, source, (nuint)pixels.Length, &layout, &size);
        }

        _gpu.CheckErrors();
        texture.HasPixels = true;
        texture.IsOpaque = pixels.Kind is PixelKind.Gray8 or PixelKind.Gray16;
    }

    public void Draw(SpriteMesh mesh, WgpuSurface surface, int middleBatch, Action middle)
    {
        var frame = _frames[surface.FrameIndex];
        var vertices = frame.Write<SpriteVertex>(CollectionsMarshal.AsSpan(mesh.Vertices));
        // [Perf Why]: 배치마다 uint[] 및 WGPUBindGroupEntry[] 힙 할당이 발생하지 않도록 스택 버퍼를 재사용한다.
        Span<uint> options = stackalloc uint[UniformUintCount];
        Span<WGPUBindGroupEntry> entries = stackalloc WGPUBindGroupEntry[BindGroupEntryCount];
        entries[3] = new() { binding = 3, sampler = _linear };
        entries[4] = new() { binding = 4, sampler = _point };

        Bind();
        for (var index = 0; index < mesh.Batches.Count; index++)
        {
            if (index == middleBatch)
            {
                middle();
                Bind();
            }

            var batch = mesh.Batches[index];
            var texture = RequireTexture(batch.Texture);
            options[0] = texture.Kind is PixelKind.Gray8 or PixelKind.Gray16 ? 1u : 0u;
            options[1] = batch.PointSampling ? 1u : 0u;
            options[2] = 0;
            options[3] = 0;
            var uniform = frame.Write<uint>(options);
            entries[0] = new() { binding = 0, textureView = texture.View };
            entries[1] = WgpuFrameResources.BufferEntry(1, uniform);
            entries[2] = WgpuFrameResources.BufferEntry(2, uniform);
            frame.Bind(_pipeline, surface, entries);
            wgpuRenderPassEncoderDraw(surface.Pass, (uint)batch.Count, 1, (uint)batch.Start, 0);
        }

        if (middleBatch == mesh.Batches.Count)
        {
            middle();
        }

        _gpu.CheckErrors();

        void Bind()
        {
            _pipeline.Bind(surface);
            wgpuRenderPassEncoderSetVertexBuffer(surface.Pass, 0, vertices.Buffer, vertices.Offset, vertices.Size);
        }
    }

    public void Dispose()
    {
        if (_linear.IsNotNull)
        {
            wgpuSamplerRelease(_linear);
            _linear = default;
        }

        if (_point.IsNotNull)
        {
            wgpuSamplerRelease(_point);
            _point = default;
        }

        _pipeline.Dispose();
    }
}
