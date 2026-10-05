using VisionCanvas.Rendering;
using Vortice.Direct3D12;

namespace VisionCanvas.Tests;

public sealed class RoiBufferArenaTests
{
    [Fact]
    public void SmallBlocksShareOneAllocationWithAlignedNonOverlappingRanges()
    {
        using var gpu = new Dx12Device(false, RenderDevicePreference.Software);
        using var arena = new RoiBufferArena(gpu);
        using var first = arena.Allocate(36);
        var pageBytes = arena.ActiveAllocationBytes;
        using var second = arena.Allocate(72);

        Assert.Same(first.Buffer, second.Buffer);
        Assert.Equal(0UL, first.Offset);
        Assert.Equal(0UL, second.Offset % 32);
        Assert.True(first.Offset + first.Length <= second.Offset);
        Assert.True(second.Length >= 72);
        Assert.Equal(pageBytes, arena.ActiveAllocationBytes);
        Assert.Equal(pageBytes, arena.ResidentAllocationBytes);
        Assert.Equal(checked((long)gpu.Device.GetResourceAllocationInfo(0,
            ResourceDescription.Buffer(1024 * 1024)).SizeInBytes), pageBytes);
    }

    [Fact]
    public void RetirementReleasesActiveCostOnlyAfterLastActiveLeaseAndKeepsPendingBufferAlive()
    {
        using var gpu = new Dx12Device(false, RenderDevicePreference.Software);
        using var arena = new RoiBufferArena(gpu);
        using var first = arena.Allocate(36);
        using var second = arena.Allocate(36);
        var buffer = first.Buffer;
        var pageBytes = arena.ActiveAllocationBytes;

        first.Retire();
        first.Retire();
        Assert.Equal(pageBytes, arena.ActiveAllocationBytes);
        Assert.Equal(pageBytes, arena.ResidentAllocationBytes);
        Assert.NotEqual(nint.Zero, buffer.NativePointer);

        second.Retire();
        Assert.Equal(0, arena.ActiveAllocationBytes);
        Assert.Equal(pageBytes, arena.ResidentAllocationBytes);
        Assert.NotEqual(nint.Zero, buffer.NativePointer);

        using var next = arena.Allocate(36);
        Assert.NotSame(buffer, next.Buffer);
        Assert.Equal(pageBytes, arena.ActiveAllocationBytes);
        Assert.Equal(2 * pageBytes, arena.ResidentAllocationBytes);

        first.Dispose();
        Assert.NotEqual(nint.Zero, buffer.NativePointer);
        second.Dispose();
        second.Dispose();
        Assert.Equal(nint.Zero, buffer.NativePointer);
        Assert.Equal(pageBytes, arena.ResidentAllocationBytes);
        next.Dispose();
        Assert.Equal(0, arena.ActiveAllocationBytes);
        Assert.Equal(0, arena.ResidentAllocationBytes);
    }

    [Fact]
    public void ReleasedRangeIsNotReusedWhileAnotherLeaseKeepsThePageActive()
    {
        using var gpu = new Dx12Device(false, RenderDevicePreference.Software);
        using var arena = new RoiBufferArena(gpu);
        using var first = arena.Allocate(36);
        using var second = arena.Allocate(36);
        first.Retire();
        first.Dispose();
        using var third = arena.Allocate(36);

        Assert.Same(second.Buffer, third.Buffer);
        Assert.True(third.Offset >= second.Offset + second.Length);
        Assert.NotEqual(first.Offset, third.Offset);
    }

    [Fact]
    public void ClosingArenaBeforeSurfaceRetiredLeasesDoesNotDisposeTheirBuffer()
    {
        using var gpu = new Dx12Device(false, RenderDevicePreference.Software);
        using var arena = new RoiBufferArena(gpu);
        using var retired = arena.Allocate(36);
        using var active = arena.Allocate(36);
        var buffer = retired.Buffer;
        retired.Retire();
        active.Dispose();
        arena.Dispose();

        Assert.Throws<ObjectDisposedException>(() => arena.Allocate(36));
        Assert.Equal(0, arena.ActiveAllocationBytes);
        Assert.True(arena.ResidentAllocationBytes > 0);
        Assert.NotEqual(nint.Zero, buffer.NativePointer);
        retired.Dispose();
        retired.Dispose();
        arena.Dispose();
        Assert.Equal(nint.Zero, buffer.NativePointer);
        Assert.Equal(0, arena.ResidentAllocationBytes);
    }

    [Fact]
    public void FullPageAndOversizedBlockReceiveSeparateResources()
    {
        using var gpu = new Dx12Device(false, RenderDevicePreference.Software);
        using var arena = new RoiBufferArena(gpu);
        using var full = arena.Allocate(1024 * 1024);
        using var small = arena.Allocate(36);
        using var large = arena.Allocate((4 * 1024 * 1024) + 33);

        Assert.NotSame(full.Buffer, small.Buffer);
        Assert.NotSame(small.Buffer, large.Buffer);
        Assert.Equal(0UL, small.Offset);
        Assert.Equal(0UL, large.Offset);
        Assert.True(large.Length >= (4 * 1024 * 1024) + 33);
        Assert.Equal(0UL, large.Length % 32);
    }

    [Fact]
    public void InterleavedBlockTransitionsUseTheSharedPageState()
    {
        using var gpu = new Dx12Device(false, RenderDevicePreference.Software);
        using var arena = new RoiBufferArena(gpu);
        using var first = arena.Allocate(36);
        using var second = arena.Allocate(36);
        using var allocator = gpu.Device.CreateCommandAllocator(CommandListType.Direct);
        using var commands = gpu.Device.CreateCommandList<ID3D12GraphicsCommandList>(CommandListType.Direct, allocator);

        first.Page.Transition(commands, ResourceStates.CopyDest);
        Assert.Equal(ResourceStates.CopyDest, second.Page.State);
        first.Page.Transition(commands, ResourceStates.NonPixelShaderResource);
        second.Page.Transition(commands, ResourceStates.CopyDest);
        Assert.Equal(ResourceStates.CopyDest, first.Page.State);
        second.Page.Transition(commands, ResourceStates.NonPixelShaderResource);
        Assert.Equal(ResourceStates.NonPixelShaderResource, first.Page.State);
        commands.Close();
        gpu.Queue.ExecuteCommandList(commands);
        gpu.Flush();
    }

    [Fact]
    public void InvalidSizesDoNotCreateOrChargeAPage()
    {
        using var gpu = new Dx12Device(false, RenderDevicePreference.Software);
        using var arena = new RoiBufferArena(gpu);

        Assert.Throws<ArgumentOutOfRangeException>(() => arena.Allocate(0));
        Assert.Throws<OverflowException>(() => arena.Allocate(ulong.MaxValue));
        Assert.Equal(0, arena.ActiveAllocationBytes);
        Assert.Equal(0, arena.ResidentAllocationBytes);
    }
}
