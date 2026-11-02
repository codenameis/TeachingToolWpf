using System.Runtime.InteropServices;
using WebGPU;
using static WebGPU.WebGPU;

namespace VisionCanvas.Wgpu.Rendering;

/// <summary>완료된 제출 슬롯에서만 Reset한다. 상수/정점의 위치를 프레임 안에서 덮어쓰지 않는다.</summary>
internal sealed unsafe class WgpuFrameResources(WgpuDevice gpu) : IDisposable
{
    private readonly List<WgpuBuffer> _pages = [];
    private readonly List<ulong> _used = [];
    private readonly List<WGPUBindGroup> _groups = [];
    private const ulong PageBytes = 1024 * 1024;
    private int _page;

    public WgpuBufferSlice Write<T>(ReadOnlySpan<T> values) where T : unmanaged
    {
        var bytes = MemoryMarshal.AsBytes(values);
        var size = (ulong)Math.Max(16, bytes.Length);
        var alignment = Math.Max(256, Math.Max(gpu.Limits.minStorageBufferOffsetAlignment, gpu.Limits.minUniformBufferOffsetAlignment));
        while (_page < _pages.Count && _used[_page] + size > _pages[_page].Size)
        {
            _page++;
        }
        if (_page == _pages.Count)
        {
            _pages.Add(new(gpu, Math.Max(PageBytes, size), WGPUBufferUsage.CopyDst | WGPUBufferUsage.Uniform | WGPUBufferUsage.Storage | WGPUBufferUsage.Vertex));
            _used.Add(0);
        }
        var page = _pages[_page];
        var offset = _used[_page];
        page.Write(bytes, offset);
        _used[_page] = checked((offset + size + alignment - 1) / alignment * alignment);
        return new(page.Handle, offset, size);
    }

    public void BindBuffers<T>(WgpuPipeline pipeline, WgpuSurface surface, in T constants, WgpuBufferSlice first, WgpuBufferSlice second) where T : unmanaged
    {
        var uniform = Write(MemoryMarshal.CreateReadOnlySpan(in constants, 1));
        var entries = stackalloc WGPUBindGroupEntry[3]
        {
            BufferEntry(0, first), BufferEntry(1, second), BufferEntry(2, uniform)
        };
        Bind(pipeline, surface, new ReadOnlySpan<WGPUBindGroupEntry>(entries, 3));
    }

    public static WGPUBindGroupEntry BufferEntry(uint binding, WgpuBufferSlice slice) => new()
    {
        binding = binding,
        buffer = slice.Buffer,
        offset = slice.Offset,
        size = slice.Size
    };

    public void Bind(WgpuPipeline pipeline, WgpuSurface surface, ReadOnlySpan<WGPUBindGroupEntry> entries)
    {
        fixed (WGPUBindGroupEntry* pointer = entries)
        {
            var descriptor = new WGPUBindGroupDescriptor { layout = pipeline.SetLayout, entryCount = (nuint)entries.Length, entries = pointer };
            var group = wgpuDeviceCreateBindGroup(gpu.Handle, &descriptor);
            _groups.Add(group);
            gpu.CheckErrors();
            wgpuRenderPassEncoderSetBindGroup(surface.Pass, 0, group, 0, null);
        }
    }

    public void Reset()
    {
        foreach (var group in _groups)
        {
            wgpuBindGroupRelease(group);
        }
        _groups.Clear();
        // 일시적으로 큰 장면에 사용한 페이지를 계속 붙잡지 않는다.
        for (var index = _pages.Count - 1; index >= 0; index--)
        {
            if (index >= 8 || _pages[index].Size > PageBytes)
            {
                _pages[index].Dispose();
                _pages.RemoveAt(index);
                _used.RemoveAt(index);
            }
            else
            {
                _used[index] = 0;
            }
        }
        _page = 0;
    }

    public void Dispose()
    {
        foreach (var group in _groups)
        {
            wgpuBindGroupRelease(group);
        }
        _groups.Clear();
        foreach (var page in _pages)
        {
            page.Dispose();
        }
        _pages.Clear();
        _used.Clear();
    }
}
