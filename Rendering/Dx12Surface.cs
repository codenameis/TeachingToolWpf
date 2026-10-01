using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace TeachingToolWpf.Rendering;

internal sealed class Dx12Surface : IDisposable
{
    private readonly Dx12Device gpu;
    private readonly IDXGISwapChain3 swapChain;
    private readonly ID3D12DescriptorHeap renderTargets;
    private readonly ID3D12Resource?[] buffers = new ID3D12Resource[2];
    private readonly ID3D12CommandAllocator[] allocators;
    private readonly ulong[] fences = new ulong[2];
    private readonly List<IDisposable>[] retired = [[], []];
    private readonly uint descriptorSize;
    public ID3D12GraphicsCommandList Commands { get; }
    public int FrameIndex { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public ID3D12Resource CurrentBuffer => buffers[FrameIndex]!;

    public Dx12Surface(Dx12Device gpu, nint hwnd, int width, int height)
    {
        this.gpu = gpu;
        Width = Math.Max(1, width); Height = Math.Max(1, height);
        using var chain = gpu.Factory.CreateSwapChainForHwnd(gpu.Queue, hwnd, new SwapChainDescription1
        {
            Width = (uint)Width, Height = (uint)Height, Format = Format.B8G8R8A8_UNorm,
            BufferCount = 2, BufferUsage = Usage.RenderTargetOutput, SampleDescription = new SampleDescription(1, 0),
            SwapEffect = SwapEffect.FlipDiscard, Scaling = Scaling.Stretch, AlphaMode = AlphaMode.Ignore
        });
        swapChain = chain.QueryInterface<IDXGISwapChain3>();
        renderTargets = gpu.Device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, 2));
        descriptorSize = gpu.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
        allocators = [gpu.Device.CreateCommandAllocator(CommandListType.Direct), gpu.Device.CreateCommandAllocator(CommandListType.Direct)];
        Commands = gpu.Device.CreateCommandList<ID3D12GraphicsCommandList>(CommandListType.Direct, allocators[0]);
        Commands.Close();
        CreateBuffers();
    }
    private CpuDescriptorHandle Target(int index) => renderTargets.GetCPUDescriptorHandleForHeapStart() + index * (int)descriptorSize;
    private void CreateBuffers()
    {
        for (var i = 0; i < 2; i++)
        {
            buffers[i] = swapChain.GetBuffer<ID3D12Resource>((uint)i);
            gpu.Device.CreateRenderTargetView(buffers[i]!, null, Target(i));
        }
    }
    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0 || (Width == width && Height == height)) return;
        gpu.Flush();
        foreach (var buffer in buffers) buffer?.Dispose();
        swapChain.ResizeBuffers(2, (uint)width, (uint)height, Format.B8G8R8A8_UNorm, SwapChainFlags.None).CheckError();
        Width = width; Height = height;
        CreateBuffers();
    }
    public void Begin()
    {
        FrameIndex = (int)swapChain.CurrentBackBufferIndex;
        gpu.Wait(fences[FrameIndex]);
        foreach (var resource in retired[FrameIndex]) resource.Dispose();
        retired[FrameIndex].Clear();
        allocators[FrameIndex].Reset();
        Commands.Reset(allocators[FrameIndex]);
        Commands.ResourceBarrierTransition(CurrentBuffer, ResourceStates.Present, ResourceStates.RenderTarget);
        Commands.OMSetRenderTargets(Target(FrameIndex));
        Commands.ClearRenderTargetView(Target(FrameIndex), new Color4(0.045f, 0.06f, 0.085f, 1));
        Commands.RSSetViewport(new Viewport(Width, Height));
        Commands.RSSetScissorRect(new RectI(0, 0, Width, Height));
    }
    public void Retire(IDisposable resource) => retired[FrameIndex].Add(resource);
    public void Present()
    {
        Commands.ResourceBarrierTransition(CurrentBuffer, ResourceStates.RenderTarget, ResourceStates.Present);
        Commands.Close();
        gpu.Queue.ExecuteCommandList(Commands);
        swapChain.Present(1, PresentFlags.None).CheckError();
        fences[FrameIndex] = gpu.Signal();
    }
    public void Dispose()
    {
        try { gpu.Flush(); }
        finally
        {
            foreach (var frame in retired) foreach (var item in frame) item.Dispose();
            foreach (var buffer in buffers) buffer?.Dispose();
            foreach (var allocator in allocators) allocator.Dispose();
            Commands.Dispose(); renderTargets.Dispose(); swapChain.Dispose();
        }
    }
}
