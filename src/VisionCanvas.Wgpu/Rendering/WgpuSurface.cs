using VisionCanvas.Rendering.Abstractions;
using WebGPU;
using static WebGPU.WebGPU;

namespace VisionCanvas.Wgpu.Rendering;

/// <summary>두 제출 슬롯을 유지한다. 슬롯 완료를 기다린 다음에만 그 슬롯의 버퍼와 퇴출 자원을 재사용한다.</summary>
internal sealed unsafe class WgpuSurface : IRenderSurface, IDisposable
{
    private readonly WgpuDevice _gpu;
    private readonly ulong[] _submissions = new ulong[2];
    private readonly List<IDisposable>[] _retired = [[], []];
    private WGPUTexture _texture;
    private WGPUTextureView _view;
    private bool _reconfigure;
    private bool _disposed;

    public int Width { get; private set; }

    public int Height { get; private set; }

    public int FrameIndex { get; private set; }

    public WGPUTextureFormat Format { get; }

    public WGPUCommandEncoder Commands { get; private set; }

    public WGPURenderPassEncoder Pass { get; private set; }

    public WGPUTexture CurrentTexture => _texture;

    public WgpuSurface(WgpuDevice gpu, int width, int height)
    {
        _gpu = gpu;
        wgpuSurfaceGetCapabilities(gpu.Surface, gpu.Adapter, out var capabilities);
        try
        {
            var formats = new ReadOnlySpan<WGPUTextureFormat>(capabilities.formats, checked((int)capabilities.formatCount));
            Format = formats.Contains(WGPUTextureFormat.BGRA8Unorm) ? WGPUTextureFormat.BGRA8Unorm : WGPUTextureFormat.RGBA8Unorm;
            if (!formats.Contains(Format) || (capabilities.usages & WGPUTextureUsage.CopySrc) == 0)
            {
                throw new NotSupportedException("원본 색상과 캡처를 보존하는 wgpu surface를 지원하지 않습니다.");
            }
        }
        finally
        {
            wgpuSurfaceCapabilitiesFreeMembers(capabilities);
        }
        Configure(width, height);
    }

    private void Configure(int width, int height)
    {
        _gpu.WaitIdle();
        var configuration = new WGPUSurfaceConfiguration
        {
            device = _gpu.Handle,
            format = Format,
            usage = WGPUTextureUsage.RenderAttachment | WGPUTextureUsage.CopySrc,
            width = (uint)Math.Max(1, width),
            height = (uint)Math.Max(1, height),
            alphaMode = WGPUCompositeAlphaMode.Opaque,
            presentMode = WGPUPresentMode.Fifo
        };
        wgpuSurfaceConfigure(_gpu.Surface, &configuration);
        _gpu.CheckErrors();
        Width = (int)configuration.width;
        Height = (int)configuration.height;
        _reconfigure = false;
    }

    public bool Begin(int width, int height)
    {
        _gpu.Wait(_submissions[FrameIndex]);
        foreach (var resource in _retired[FrameIndex])
        {
            resource.Dispose();
        }
        _retired[FrameIndex].Clear();
        if (Width != width || Height != height || _reconfigure)
        {
            Configure(width, height);
        }
        WGPUSurfaceTexture acquired;
        wgpuSurfaceGetCurrentTexture(_gpu.Surface, &acquired);
        if (acquired.status is not (WGPUSurfaceGetCurrentTextureStatus.SuccessOptimal or WGPUSurfaceGetCurrentTextureStatus.SuccessSuboptimal))
        {
            if (acquired.texture.IsNotNull)
            {
                wgpuTextureRelease(acquired.texture);
            }
            if (acquired.status is WGPUSurfaceGetCurrentTextureStatus.Outdated or WGPUSurfaceGetCurrentTextureStatus.Lost or WGPUSurfaceGetCurrentTextureStatus.Timeout)
            {
                _reconfigure = acquired.status != WGPUSurfaceGetCurrentTextureStatus.Timeout;
                return false;
            }
            throw new InvalidOperationException($"wgpu surface 획득 실패: {acquired.status}");
        }
        _texture = acquired.texture;
        _reconfigure = acquired.status == WGPUSurfaceGetCurrentTextureStatus.SuccessSuboptimal;
        _view = wgpuTextureCreateView(_texture, null);
        Commands = wgpuDeviceCreateCommandEncoder(_gpu.Handle, null);
        _gpu.CheckErrors();
        return true;
    }

    public void BeginDrawing()
    {
        var attachment = new WGPURenderPassColorAttachment
        {
            view = _view,
            depthSlice = uint.MaxValue,
            loadOp = WGPULoadOp.Clear,
            storeOp = WGPUStoreOp.Store,
            clearValue = new() { r = .045f, g = .06f, b = .085f, a = 1 }
        };
        var descriptor = new WGPURenderPassDescriptor { colorAttachmentCount = 1, colorAttachments = &attachment };
        Pass = wgpuCommandEncoderBeginRenderPass(Commands, &descriptor);
        _gpu.CheckErrors();
    }

    public void EndDrawing()
    {
        wgpuRenderPassEncoderEnd(Pass);
        wgpuRenderPassEncoderRelease(Pass);
        Pass = default;
    }

    public ulong Present()
    {
        var command = wgpuCommandEncoderFinish(Commands, null);
        ulong submission;
        try
        {
            _gpu.CheckErrors();
            submission = wgpuQueueSubmitForIndex(_gpu.Queue, 1, &command);
            _submissions[FrameIndex] = submission;
            _gpu.CheckErrors();
            var status = wgpuSurfacePresent(_gpu.Surface);
            if (status != WGPUStatus.Success)
            {
                throw new InvalidOperationException($"wgpu Present 실패: {status}");
            }
        }
        finally
        {
            wgpuCommandBufferRelease(command);
            ReleaseFrame();
        }
        FrameIndex = (FrameIndex + 1) % 2;
        return submission;
    }

    public void Retire(IDisposable resource) => _retired[FrameIndex].Add(resource);

    private void ReleaseFrame()
    {
        if (Pass.IsNotNull)
        {
            wgpuRenderPassEncoderRelease(Pass);
            Pass = default;
        }
        if (Commands.IsNotNull)
        {
            wgpuCommandEncoderRelease(Commands);
            Commands = default;
        }
        if (_view.IsNotNull)
        {
            wgpuTextureViewRelease(_view);
            _view = default;
        }
        if (_texture.IsNotNull)
        {
            wgpuTextureRelease(_texture);
            _texture = default;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _gpu.WaitIdle();
        ReleaseFrame();
        foreach (var slot in _retired)
        {
            foreach (var resource in slot)
            {
                resource.Dispose();
            }
            slot.Clear();
        }
        wgpuSurfaceUnconfigure(_gpu.Surface);
    }
}
