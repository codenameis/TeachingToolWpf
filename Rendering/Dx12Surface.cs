using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace TeachingToolWpf.Rendering;

/// <summary>렌더 스레드에서만 사용하는 화면 버퍼와 프레임별 GPU 작업의 소유자.</summary>
internal sealed class Dx12Surface : IDisposable
{
    private const int BackBufferCount = 2;
    private readonly Dx12Device gpu;
    private readonly IDXGISwapChain3 swapChain;
    private readonly ID3D12DescriptorHeap renderTargets;
    private readonly ID3D12Resource?[] buffers = new ID3D12Resource[BackBufferCount];
    private readonly ID3D12CommandAllocator[] allocators;
    private readonly ulong[] frameCompletionFences = new ulong[BackBufferCount];
    private readonly List<IDisposable>[] resourcesAwaitingGpu = [[], []];
    private readonly uint descriptorSize;
    public ID3D12GraphicsCommandList Commands
    {
        get;
    }
    public int FrameIndex
    {
        get; private set;
    }
    public int Width
    {
        get; private set;
    }
    public int Height
    {
        get; private set;
    }
    public ID3D12Resource CurrentBuffer => buffers[FrameIndex]!;

    public Dx12Surface(Dx12Device gpu, nint hwnd, int width, int height)
    {
        this.gpu = gpu;
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
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
            AlphaMode = AlphaMode.Ignore
        });
        swapChain = chain.QueryInterface<IDXGISwapChain3>();
        renderTargets = gpu.Device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, BackBufferCount));
        descriptorSize = gpu.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
        allocators = [gpu.Device.CreateCommandAllocator(CommandListType.Direct), gpu.Device.CreateCommandAllocator(CommandListType.Direct)];
        Commands = gpu.Device.CreateCommandList<ID3D12GraphicsCommandList>(CommandListType.Direct, allocators[0]);
        Commands.Close();
        CreateBuffers();
    }
    private CpuDescriptorHandle RenderTargetView(int index) =>
        renderTargets.GetCPUDescriptorHandleForHeapStart() + index * (int)descriptorSize;
    private void CreateBuffers()
    {
        for (var i = 0; i < BackBufferCount; i++)
        {
            buffers[i] = swapChain.GetBuffer<ID3D12Resource>((uint)i);
            gpu.Device.CreateRenderTargetView(buffers[i]!, null, RenderTargetView(i));
        }
    }
    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0 || (Width == width && Height == height))
            return;
        // 화면 크기를 바꿀 때는 이전 크기의 버퍼를 사용하는 모든 GPU 작업이 끝나야 한다.
        gpu.Flush();
        foreach (var buffer in buffers)
            buffer?.Dispose();
        swapChain.ResizeBuffers(BackBufferCount, (uint)width, (uint)height, Format.B8G8R8A8_UNorm, SwapChainFlags.None).CheckError();
        Width = width;
        Height = height;
        CreateBuffers();
    }
    public void Begin()
    {
        FrameIndex = (int)swapChain.CurrentBackBufferIndex;
        // 같은 프레임 슬롯의 명령 메모리와 업로드 버퍼는 fence 완료 후에만 재사용한다.
        gpu.Wait(frameCompletionFences[FrameIndex]);
        DisposeRetiredResources(FrameIndex);
        allocators[FrameIndex].Reset();
        Commands.Reset(allocators[FrameIndex]);
        Commands.ResourceBarrierTransition(CurrentBuffer, ResourceStates.Present, ResourceStates.RenderTarget);
        Commands.OMSetRenderTargets(RenderTargetView(FrameIndex));
        Commands.ClearRenderTargetView(RenderTargetView(FrameIndex), new Color4(0.045f, 0.06f, 0.085f, 1));
        Commands.RSSetViewport(new Viewport(Width, Height));
        Commands.RSSetScissorRect(new RectI(0, 0, Width, Height));
    }
    /// <summary>이번 프레임까지 제출된 GPU 참조가 끝난 뒤 해제하도록 예약한다.</summary>
    public void Retire(IDisposable resource) => resourcesAwaitingGpu[FrameIndex].Add(resource);

    private void DisposeRetiredResources(int frameIndex)
    {
        foreach (var resource in resourcesAwaitingGpu[frameIndex])
            resource.Dispose();
        resourcesAwaitingGpu[frameIndex].Clear();
    }
    public void Present()
    {
        Commands.ResourceBarrierTransition(CurrentBuffer, ResourceStates.RenderTarget, ResourceStates.Present);
        Commands.Close();
        gpu.Queue.ExecuteCommandList(Commands);
        swapChain.Present(1, PresentFlags.None).CheckError();
        frameCompletionFences[FrameIndex] = gpu.Signal();
    }
    public void Dispose()
    {
        try
        {
            gpu.Flush();
        }
        finally
        {
            foreach (var frame in resourcesAwaitingGpu)
            {
                foreach (var resource in frame)
                    resource.Dispose();
            }
            foreach (var buffer in buffers)
                buffer?.Dispose();
            foreach (var allocator in allocators)
                allocator.Dispose();
            Commands.Dispose();
            renderTargets.Dispose();
            swapChain.Dispose();
        }
    }
}
