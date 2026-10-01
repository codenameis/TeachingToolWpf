using Vortice.Direct3D12;

namespace TeachingToolWpf.Rendering;

/// <summary>Timestamp results are read only after the frame fence, with no extra GPU stall.</summary>
internal sealed class GpuTimer : IDisposable
{
    private readonly ID3D12QueryHeap queries;
    private readonly ID3D12Resource readback;
    private readonly ulong frequency;
    private readonly bool[] pending = new bool[2];
    public GpuTimer(Dx12Device gpu)
    {
        gpu.Queue.GetTimestampFrequency(out frequency).CheckError();
        queries = gpu.Device.CreateQueryHeap<ID3D12QueryHeap>(new(QueryHeapType.Timestamp, 4, 0));
        readback = gpu.Device.CreateCommittedResource(HeapType.Readback, ResourceDescription.Buffer(32), ResourceStates.CopyDest);
    }
    public unsafe double? Begin(Dx12Surface surface)
    {
        var index = surface.FrameIndex;
        double? elapsed = null;
        if (pending[index])
        {
            var data = readback.Map<ulong>(0);
            elapsed = (data[index * 2 + 1] - data[index * 2]) * 1000d / frequency;
            readback.Unmap(0);
        }
        surface.Commands.EndQuery(queries, QueryType.Timestamp, (uint)index * 2);
        return elapsed;
    }
    public void End(Dx12Surface surface)
    {
        var index = (uint)surface.FrameIndex;
        surface.Commands.EndQuery(queries, QueryType.Timestamp, index * 2 + 1);
        surface.Commands.ResolveQueryData(queries, QueryType.Timestamp, index * 2, 2, readback, index * 16);
        pending[index] = true;
    }
    public void Dispose() { readback.Dispose(); queries.Dispose(); }
}
