using Vortice.Direct3D12;

namespace TeachingToolWpf.Rendering;

/// <summary>Timestamp results are read only after the frame fence, with no extra GPU stall.</summary>
internal sealed class GpuTimer : IDisposable
{
    private readonly ID3D12QueryHeap _queries;
    private readonly ID3D12Resource _readback;
    private readonly ulong _frequency;
    private readonly bool[] _pending = new bool[2];

    public GpuTimer(Dx12Device gpu)
    {
        gpu.Queue.GetTimestampFrequency(out _frequency).CheckError();
        _queries = gpu.Device.CreateQueryHeap<ID3D12QueryHeap>(new(QueryHeapType.Timestamp, 4, 0));
        _readback = gpu.Device.CreateCommittedResource(HeapType.Readback, ResourceDescription.Buffer(32), ResourceStates.CopyDest);
    }

    public unsafe double? Begin(Dx12Surface surface)
    {
        var index = surface.FrameIndex;
        double? elapsed = null;
        if (_pending[index])
        {
            var data = _readback.Map<ulong>(0);
            elapsed = (data[(index * 2) + 1] - data[index * 2]) * 1000d / _frequency;
            _readback.Unmap(0);
        }
        surface.Commands.EndQuery(_queries, QueryType.Timestamp, (uint)index * 2);
        return elapsed;
    }

    public void End(Dx12Surface surface)
    {
        var index = (uint)surface.FrameIndex;
        surface.Commands.EndQuery(_queries, QueryType.Timestamp, (index * 2) + 1);
        surface.Commands.ResolveQueryData(_queries, QueryType.Timestamp, index * 2, 2, _readback, index * 16);
        _pending[index] = true;
    }

    public void Dispose()
    {
        _readback.Dispose();
        _queries.Dispose();
    }
}
