using Vortice.Direct3D12;

namespace VisionCanvas.Rendering;

/// <summary>렌더 스레드의 ROI 보관 버퍼. 퇴출과 fence 이후 해제를 별도로 계산한다.</summary>
internal sealed class RoiBufferArena(Dx12Device gpu) : IDisposable
{
    private const ulong PageCapacityBytes = 1024 * 1024;
    private const ulong CapacityAlignmentBytes = 64 * 1024;
    private const ulong OffsetAlignmentBytes = 32;
    private Page? _current;
    private bool _disposed;

    public long ActiveAllocationBytes
    {
        get; private set;
    }
    public long ResidentAllocationBytes
    {
        get; private set;
    }

    public Lease Allocate(ulong bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfZero(bytes);
        var alignedBytes = AlignUp(bytes, OffsetAlignmentBytes);
        if (_current is null || !_current.CanAllocate(alignedBytes))
        {
            var capacity = Math.Max(PageCapacityBytes, AlignUp(alignedBytes, CapacityAlignmentBytes));
            var description = ResourceDescription.Buffer(capacity);
            var allocationBytes = checked((long)gpu.Device.GetResourceAllocationInfo(0, description).SizeInBytes);
            var buffer = gpu.Device.CreateCommittedResource(HeapType.Default, description, ResourceStates.Common);
            try
            {
                var page = new Page(this, buffer, capacity, allocationBytes);
                var lease = page.Allocate(alignedBytes);
                _current = page;
                return lease;
            }
            catch
            {
                buffer.Dispose();
                throw;
            }
        }

        return _current.Allocate(alignedBytes);
    }

    private static ulong AlignUp(ulong value, ulong alignment)
        => checked(value + alignment - 1) & ~(alignment - 1);

    public void Dispose()
    {
        // Pipeline은 Flush 후 활성 lease를 해제한다. Surface가 보관한 퇴출 lease는 그 뒤에도 살아 있다.
        _disposed = true;
        _current = null;
    }

    internal sealed class Page(RoiBufferArena owner, ID3D12Resource buffer, ulong capacity, long allocationBytes)
    {
        private ulong _usedBytes;
        private int _activeLeases;
        private int _liveLeases;

        public ID3D12Resource Buffer { get; } = buffer;
        public ResourceStates State { get; private set; } = ResourceStates.Common;

        internal bool CanAllocate(ulong bytes) => bytes <= capacity - _usedBytes;

        internal Lease Allocate(ulong bytes)
        {
            var lease = new Lease(this, _usedBytes, bytes);
            _usedBytes += bytes;
            if (_activeLeases++ == 0)
            {
                owner.ActiveAllocationBytes += allocationBytes;
            }
            if (_liveLeases++ == 0)
            {
                owner.ResidentAllocationBytes += allocationBytes;
            }
            return lease;
        }

        public void Transition(ID3D12GraphicsCommandList commands, ResourceStates state)
        {
            if (State == state)
            {
                return;
            }
            // 서로 다른 블록의 copy/draw가 교대해도 상태는 공유 리소스 전체에 속한다.
            commands.ResourceBarrierTransition(Buffer, State, state);
            State = state;
        }

        internal void Retire()
        {
            if (--_activeLeases == 0)
            {
                owner.ActiveAllocationBytes -= allocationBytes;
                if (ReferenceEquals(owner._current, this))
                {
                    owner._current = null;
                }
            }
        }

        internal void Release()
        {
            if (--_liveLeases == 0)
            {
                Buffer.Dispose();
                owner.ResidentAllocationBytes -= allocationBytes;
            }
        }
    }

    internal sealed class Lease(Page page, ulong offset, ulong length) : IDisposable
    {
        private bool _retired;
        private bool _disposed;

        public Page Page { get; } = page;
        public ulong Offset { get; } = offset;
        public ulong Length { get; } = length;
        public ID3D12Resource Buffer => Page.Buffer;

        public void Retire()
        {
            if (_retired)
            {
                return;
            }
            _retired = true;
            Page.Retire();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            Retire();
            _disposed = true;
            Page.Release();
        }
    }
}
