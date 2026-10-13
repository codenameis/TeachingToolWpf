using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace VisionCanvas.Rendering;

/// <summary>렌더 스레드에서만 사용하는 화면 버퍼와 프레임별 GPU 작업의 소유자.</summary>
internal sealed class Dx12Surface : IDisposable
{
    private const int BackBufferCount = 2;
    private const SwapChainFlags Flags = SwapChainFlags.FrameLatencyWaitableObject;
    private readonly Dx12Device _gpu;
    private readonly IDXGISwapChain3 _swapChain;
    private readonly ID3D12DescriptorHeap _renderTargets;
    private readonly ID3D12Resource?[] _buffers = new ID3D12Resource[BackBufferCount];
    private readonly ID3D12CommandAllocator[] _allocators = new ID3D12CommandAllocator[BackBufferCount];
    private readonly ulong[] _frameCompletionFences = new ulong[BackBufferCount];
    private readonly List<IDisposable>[] _resourcesAwaitingGpu = [[], []];
    private readonly uint _descriptorSize;
    private readonly AutoResetEvent _frameReady = new(false);

    public ID3D12GraphicsCommandList Commands
    {
        get;
    }

    public int FrameIndex
    {
        get;
        private set;
    }

    public int Width
    {
        get;
        private set;
    }

    public int Height
    {
        get;
        private set;
    }

    public ID3D12Resource CurrentBuffer => _buffers[FrameIndex]!;

    public double FenceWaitMilliseconds
    {
        get; private set;
    }

    public WaitHandle FrameReady => _frameReady;

    public FrameStatistics? ReadPresentationStatistics()
        => _swapChain.GetFrameStatistics(out var statistics).Success ? statistics : null;

    public Dx12Surface(Dx12Device gpu, nint hwnd, int width, int height)
    {
        _gpu = gpu;
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        try
        {
            using var chain = gpu.Factory.CreateSwapChainForHwnd(gpu.Queue, hwnd, new SwapChainDescription1
            {
                Width = (uint)Width,
                Height = (uint)Height,
                Format = Format.B8G8R8A8_UNorm,
                BufferCount = BackBufferCount,
                BufferUsage = Usage.RenderTargetOutput,
                SampleDescription = new SampleDescription(1, 0),
                SwapEffect = SwapEffect.FlipDiscard,
                Scaling = Scaling.Stretch,
                AlphaMode = AlphaMode.Ignore,
                Flags = Flags
            });
            _swapChain = chain.QueryInterface<IDXGISwapChain3>();
            _swapChain.MaximumFrameLatency = 1;
            var frameReady = _swapChain.FrameLatencyWaitableObject;
            if (frameReady == 0)
            {
                throw new InvalidOperationException("DXGI frame latency wait handle is unavailable.");
            }
            _frameReady.SafeWaitHandle.Dispose();
            _frameReady.SafeWaitHandle = new SafeWaitHandle(frameReady, true);
            _renderTargets = gpu.Device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, BackBufferCount));
            _descriptorSize = gpu.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
            for (var i = 0; i < BackBufferCount; i++)
            {
                _allocators[i] = gpu.Device.CreateCommandAllocator(CommandListType.Direct);
            }
            Commands = gpu.Device.CreateCommandList<ID3D12GraphicsCommandList>(CommandListType.Direct, _allocators[0]);
            Commands.Close();
            CreateBuffers();
        }
        catch
        {
            ReleaseResources();
            throw;
        }
    }

    private CpuDescriptorHandle RenderTargetView(int index) =>
        _renderTargets.GetCPUDescriptorHandleForHeapStart() + (index * (int)_descriptorSize);

    private void CreateBuffers()
    {
        for (var i = 0; i < BackBufferCount; i++)
        {
            _buffers[i] = _swapChain.GetBuffer<ID3D12Resource>((uint)i);
            _gpu.Device.CreateRenderTargetView(_buffers[i]!, null, RenderTargetView(i));
        }
    }

    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0 || (Width == width && Height == height))
        {
            return;
        }
        // 화면 크기를 바꿀 때는 이전 크기의 버퍼를 사용하는 모든 GPU 작업이 끝나야 한다.
        _gpu.Flush();
        foreach (var buffer in _buffers)
        {
            buffer?.Dispose();
        }

        _swapChain.ResizeBuffers(BackBufferCount, (uint)width, (uint)height, Format.B8G8R8A8_UNorm, Flags).CheckError();
        Width = width;
        Height = height;
        CreateBuffers();
    }

    public void Begin()
    {
        FrameIndex = (int)_swapChain.CurrentBackBufferIndex;
        // 같은 프레임 슬롯의 명령 메모리와 업로드 버퍼는 fence 완료 후에만 재사용한다.
        var waitStart = Stopwatch.GetTimestamp();
        _gpu.Wait(_frameCompletionFences[FrameIndex]);
        FenceWaitMilliseconds = Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds;
        DisposeRetiredResources(FrameIndex);
        _allocators[FrameIndex].Reset();
        Commands.Reset(_allocators[FrameIndex]);
        Commands.ResourceBarrierTransition(CurrentBuffer, ResourceStates.Present, ResourceStates.RenderTarget);
        Commands.OMSetRenderTargets(RenderTargetView(FrameIndex));
        Commands.ClearRenderTargetView(RenderTargetView(FrameIndex), new Color4(0.045f, 0.06f, 0.085f, 1));
        Commands.RSSetViewport(new Viewport(Width, Height));
        Commands.RSSetScissorRect(new RectI(0, 0, Width, Height));
    }

    /// <summary>이번 프레임까지 제출된 GPU 참조가 끝난 뒤 해제하도록 예약한다.</summary>
    public void Retire(IDisposable resource) => _resourcesAwaitingGpu[FrameIndex].Add(resource);

    private void DisposeRetiredResources(int frameIndex)
    {
        foreach (var resource in _resourcesAwaitingGpu[frameIndex])
        {
            resource.Dispose();
        }

        _resourcesAwaitingGpu[frameIndex].Clear();
    }

    public ulong Present()
    {
        Commands.ResourceBarrierTransition(CurrentBuffer, ResourceStates.RenderTarget, ResourceStates.Present);
        Commands.Close();
        _gpu.Queue.ExecuteCommandList(Commands);
        _swapChain.Present(1, PresentFlags.None).CheckError();
        _frameCompletionFences[FrameIndex] = _gpu.Signal();
        return _frameCompletionFences[FrameIndex];
    }

    public void Dispose()
    {
        try
        {
            _gpu.Flush();
        }
        finally
        {
            ReleaseResources();
        }
    }

    private void ReleaseResources()
    {
        foreach (var frame in _resourcesAwaitingGpu)
        {
            foreach (var resource in frame)
            {
                resource.Dispose();
            }
        }
        foreach (var buffer in _buffers)
        {
            buffer?.Dispose();
        }

        foreach (var allocator in _allocators)
        {
            allocator?.Dispose();
        }

        Commands?.Dispose();
        _renderTargets?.Dispose();
        _swapChain?.Dispose();
        _frameReady.Dispose();
    }
}
