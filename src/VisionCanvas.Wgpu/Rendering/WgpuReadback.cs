using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WebGPU;
using static WebGPU.WebGPU;

namespace VisionCanvas.Wgpu.Rendering;

/// <summary>명시적인 캡처 요청에만 사용한다. 일반 프레임 표시에는 GPU→CPU 복사가 없다.</summary>
internal sealed unsafe class WgpuReadback : IDisposable
{
    private readonly WgpuDevice _gpu;
    private readonly WgpuBuffer _buffer;
    private readonly int _width;
    private readonly int _height;
    private readonly int _stride;
    private readonly bool _rgba;

    private sealed class MapResult
    {
        public WGPUMapAsyncStatus Status;
        public string? Error;
    }

    public WgpuReadback(WgpuDevice gpu, WgpuSurface surface)
    {
        _gpu = gpu;
        _width = surface.Width;
        _height = surface.Height;
        _stride = checked(((_width * 4) + 255) & ~255);
        _rgba = surface.Format == WGPUTextureFormat.RGBA8Unorm;
        _buffer = new(gpu, checked((ulong)_stride * (ulong)_height), WGPUBufferUsage.CopyDst | WGPUBufferUsage.MapRead);
        try
        {
            var source = new WGPUTexelCopyTextureInfo { texture = surface.CurrentTexture, aspect = WGPUTextureAspect.All };
            var destination = new WGPUTexelCopyBufferInfo
            {
                buffer = _buffer.Handle,
                layout = new() { bytesPerRow = (uint)_stride, rowsPerImage = (uint)_height }
            };
            var size = new WGPUExtent3D { width = (uint)_width, height = (uint)_height, depthOrArrayLayers = 1 };
            wgpuCommandEncoderCopyTextureToBuffer(surface.Commands, &source, &destination, &size);
            gpu.CheckErrors();
        }
        catch
        {
            _buffer.Dispose();
            throw;
        }
    }

    public void Save(string path)
    {
        var result = new MapResult();
        var callback = GCHandle.Alloc(result);
        // 콜백이 GCHandle을 반환한다. 매핑 실패/취소에서도 스택 주소를 보관하지 않는다.
        wgpuBufferMapAsync(_buffer.Handle, WGPUMapMode.Read, 0, (nuint)_buffer.Size, new()
        {
            mode = WGPUCallbackMode.AllowProcessEvents,
            callback = &Mapped,
            userdata1 = (void*)GCHandle.ToIntPtr(callback)
        });
        _gpu.WaitIdle();
        if (result.Status != WGPUMapAsyncStatus.Success)
        {
            throw new InvalidOperationException($"wgpu 캡처 매핑 실패: {result.Status} · {result.Error}");
        }
        try
        {
            var pointer = wgpuBufferGetConstMappedRange(_buffer.Handle, 0, (nuint)_buffer.Size);
            var pixels = new byte[checked(_width * _height * 4)];
            for (var row = 0; row < _height; row++)
            {
                new ReadOnlySpan<byte>((byte*)pointer + (row * _stride), _width * 4).CopyTo(pixels.AsSpan(row * _width * 4));
            }
            if (_rgba)
            {
                for (var index = 0; index < pixels.Length; index += 4)
                {
                    (pixels[index], pixels[index + 2]) = (pixels[index + 2], pixels[index]);
                }
            }
            var bitmap = BitmapSource.Create(_width, _height, 96, 96, PixelFormats.Bgra32, null, pixels, _width * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(path);
            encoder.Save(output);
        }
        finally
        {
            wgpuBufferUnmap(_buffer.Handle);
        }
    }

    [UnmanagedCallersOnly]
    private static void Mapped(WGPUMapAsyncStatus status, WGPUStringView message, void* owner, void* unused)
    {
        var handle = GCHandle.FromIntPtr((nint)owner);
        var result = (MapResult)handle.Target!;
        result.Status = status;
        result.Error = message.ToString();
        handle.Free();
    }

    public void Dispose() => _buffer.Dispose();
}
