using WebGPU;
using static WebGPU.WebGPU;

namespace VisionCanvas.Wgpu.Rendering;

internal sealed unsafe class WgpuBuffer : IDisposable
{
    private readonly WgpuDevice _gpu;

    public WGPUBuffer Handle { get; private set; }

    public ulong Size { get; }

    public ulong AllocatedBytes => Size;

    public WgpuBuffer(WgpuDevice gpu, ulong size, WGPUBufferUsage usage)
    {
        _gpu = gpu;
        Size = checked((Math.Max(16, size) + 3) & ~3UL);
        var descriptor = new WGPUBufferDescriptor { size = Size, usage = usage };
        Handle = wgpuDeviceCreateBuffer(gpu.Handle, &descriptor);
        try
        {
            gpu.CheckErrors();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Write(ReadOnlySpan<byte> bytes, ulong offset = 0)
    {
        if (offset > Size || (ulong)bytes.Length > Size - offset || offset % 4 != 0 || bytes.Length % 4 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes));
        }
        if (bytes.IsEmpty)
        {
            return;
        }
        fixed (byte* source = bytes)
        {
            wgpuQueueWriteBuffer(_gpu.Queue, Handle, offset, source, (nuint)bytes.Length);
        }
        _gpu.CheckErrors();
    }

    public void Dispose()
    {
        if (Handle.IsNotNull)
        {
            wgpuBufferRelease(Handle);
            Handle = default;
        }
    }
}

internal readonly record struct WgpuBufferSlice(WGPUBuffer Buffer, ulong Offset, ulong Size);
