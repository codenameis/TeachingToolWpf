using Vortice.Direct3D12;

namespace TeachingToolWpf.Rendering;

/// <summary>Reused only after the corresponding swap-chain frame fence has completed.</summary>
internal sealed class FrameUploadArena(Dx12Device gpu) : IDisposable
{
    internal readonly record struct Allocation(ID3D12Resource Buffer, ulong Offset, nint Pointer);
    private sealed record Block(ID3D12Resource Buffer, ulong Size, nint Pointer) : IDisposable
    { public void Dispose() { Buffer.Unmap(0); Buffer.Dispose(); } }
    private readonly List<Block> blocks = [];
    private int index;
    private ulong used;
    public void Reset() { index = 0; used = 0; }
    public unsafe Allocation Allocate(ulong bytes)
    {
        used = (used + 511) & ~511UL;
        while (index < blocks.Count && used + bytes > blocks[index].Size) { index++; used = 0; }
        if (index == blocks.Count)
        {
            var size = Math.Max(4UL * 1024 * 1024, (bytes + 65535) & ~65535UL);
            var resource = gpu.Device.CreateCommittedResource(HeapType.Upload, ResourceDescription.Buffer(size), ResourceStates.GenericRead);
            blocks.Add(new(resource, size, (nint)resource.Map<byte>(0)));
        }
        var block = blocks[index]; var offset = used; used += bytes;
        return new(block.Buffer, offset, block.Pointer + checked((nint)offset));
    }
    public void Dispose() { foreach (var block in blocks) block.Dispose(); }
}
