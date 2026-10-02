using Vortice.Direct3D12;

namespace TeachingToolWpf.Rendering;

/// <summary>프레임 슬롯별 CPU → GPU 전송 공간. 해당 슬롯의 fence 완료 후에만 Reset한다.</summary>
internal sealed class FrameUploadArena(Dx12Device gpu) : IDisposable
{
    private const ulong UploadOffsetAlignmentBytes = 512;
    private const ulong BufferCapacityAlignmentBytes = 64 * 1024;
    private const ulong MinimumBlockCapacityBytes = 4 * 1024 * 1024;

    internal readonly record struct Allocation(ID3D12Resource Buffer, ulong Offset, nint Pointer);
    private sealed record Block(ID3D12Resource Buffer, ulong Size, nint Pointer) : IDisposable
    {
        public void Dispose()
        {
            Buffer.Unmap(0);
            Buffer.Dispose();
        }
    }
    private readonly List<Block> blocks = [];
    private int currentBlockIndex;
    private ulong usedBytes;

    public void Reset()
    {
        currentBlockIndex = 0;
        usedBytes = 0;
    }

    public unsafe Allocation Allocate(ulong bytes)
    {
        // 텍스처와 ROI가 같은 전송 공간을 쓰므로 텍스처 복사에 필요한 정렬을 유지한다.
        usedBytes = AlignUp(usedBytes, UploadOffsetAlignmentBytes);
        while (currentBlockIndex < blocks.Count && usedBytes + bytes > blocks[currentBlockIndex].Size)
        {
            currentBlockIndex++;
            usedBytes = 0;
        }
        if (currentBlockIndex == blocks.Count)
        {
            var size = Math.Max(MinimumBlockCapacityBytes, AlignUp(bytes, BufferCapacityAlignmentBytes));
            var resource = gpu.Device.CreateCommittedResource(HeapType.Upload, ResourceDescription.Buffer(size), ResourceStates.GenericRead);
            blocks.Add(new(resource, size, (nint)resource.Map<byte>(0)));
        }
        var block = blocks[currentBlockIndex];
        var offset = usedBytes;
        usedBytes += bytes;
        return new(block.Buffer, offset, block.Pointer + checked((nint)offset));
    }

    // 이 클래스에서 사용하는 alignment는 모두 2의 거듭제곱이다.
    private static ulong AlignUp(ulong value, ulong alignment) => (value + alignment - 1) & ~(alignment - 1);

    public void Dispose()
    {
        foreach (var block in blocks)
            block.Dispose();
    }
}
