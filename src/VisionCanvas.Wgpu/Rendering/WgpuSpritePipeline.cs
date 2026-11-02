using System.Runtime.InteropServices;
using VisionCanvas.Rendering;
using VisionImaging.Abstractions;
using WebGPU;
using static WebGPU.WebGPU;

namespace VisionCanvas.Wgpu.Rendering;

internal sealed unsafe class WgpuSpritePipeline : ITexturePipeline, IDisposable
{
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

    public IRenderTexture CreateTexture(int width, int height, PixelKind kind) => new WgpuTexture(_gpu, width, height, kind);

    public IRenderTexture Upload(PixelBuffer pixels, IRenderSurface surface)
    {
        var texture = new WgpuTexture(_gpu, pixels.Width, pixels.Height, pixels.Kind);
        try
        {
            Update(texture, pixels, surface);
            texture.IsOpaque = TextureTransparency.PixelsAreOpaque(pixels);
            return texture;
        }
        catch
        {
            surface.Retire(texture);
            throw;
        }
    }

    public void Update(IRenderTexture target, PixelBuffer pixels, IRenderSurface surface)
    {
        var texture = (WgpuTexture)target;
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
        Bind();
        for (var index = 0; index < mesh.Batches.Count; index++)
        {
            if (index == middleBatch)
            {
                middle();
                Bind();
            }
            var batch = mesh.Batches[index];
            var texture = (WgpuTexture)batch.Texture;
            uint[] options = [texture.Kind is PixelKind.Gray8 or PixelKind.Gray16 ? 1u : 0u, batch.PointSampling ? 1u : 0u, 0, 0];
            var uniform = frame.Write<uint>(options);
            var entries = new WGPUBindGroupEntry[]
            {
                new() { binding = 0, textureView = texture.View },
                WgpuFrameResources.BufferEntry(1, uniform),
                WgpuFrameResources.BufferEntry(2, uniform),
                new() { binding = 3, sampler = _linear },
                new() { binding = 4, sampler = _point }
            };
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
