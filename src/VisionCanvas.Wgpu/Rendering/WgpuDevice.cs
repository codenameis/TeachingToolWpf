using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using WebGPU;
using static WebGPU.WebGPU;

namespace VisionCanvas.Wgpu.Rendering;

/// <summary>전용 렌더 스레드가 소유한다. 콜백은 오류만 기록하며 unmanaged 경계를 넘어 예외를 던지지 않는다.</summary>
internal sealed unsafe class WgpuDevice : IDisposable
{
    private readonly ConcurrentQueue<string> _errors = new();
    private GCHandle _callbackOwner;
    private bool _disposed;

    public WGPUInstance Instance { get; private set; }

    public WGPUAdapter Adapter { get; private set; }

    public WGPUDevice Handle { get; private set; }

    public WGPUQueue Queue { get; private set; }

    public WGPUSurface Surface { get; private set; }

    public string Name { get; private set; } = "";

    public string Driver { get; private set; } = "";

    public bool SupportsGray16 { get; private set; }

    public long TileBudgetBytes => 256L * 1024 * 1024;

    public WGPULimits Limits { get; private set; }

    public WgpuDevice(nint hwnd)
    {
        _callbackOwner = GCHandle.Alloc(this);
        try
        {
            var extras = new WGPUInstanceExtras
            {
                chain = new() { sType = (WGPUSType)WGPUNativeSType.InstanceExtras },
                backends = WGPUInstanceBackend.DX12,
                flags = WGPUInstanceFlag.Validation
            };
            var instance = new WGPUInstanceDescriptor { nextInChain = (WGPUChainedStruct*)&extras };
            Instance = wgpuCreateInstance(&instance);
            if (Instance.IsNull)
            {
                throw new InvalidOperationException("wgpu instance를 생성하지 못했습니다.");
            }
            var windows = new WGPUSurfaceSourceWindowsHWND
            {
                chain = new() { sType = WGPUSType.SurfaceSourceWindowsHWND },
                hinstance = (void*)GetModuleHandle(null),
                hwnd = (void*)hwnd
            };
            var surface = new WGPUSurfaceDescriptor { nextInChain = (WGPUChainedStruct*)&windows };
            Surface = wgpuInstanceCreateSurface(Instance, &surface);
            var adapterOptions = new WGPURequestAdapterOptions
            {
                compatibleSurface = Surface,
                powerPreference = WGPUPowerPreference.HighPerformance,
                backendType = WGPUBackendType.D3D12
            };
            // 고정한 wgpu-native 27의 adapter/device 요청은 콜백을 호출한 뒤 반환한다.
            wgpuInstanceRequestAdapter(Instance, &adapterOptions, new()
            {
                mode = WGPUCallbackMode.AllowSpontaneous,
                callback = &AdapterReady,
                userdata1 = (void*)GCHandle.ToIntPtr(_callbackOwner)
            });
            CheckErrors();
            if (Adapter.IsNull)
            {
                throw new InvalidOperationException("wgpu 하드웨어 adapter를 찾지 못했습니다.");
            }
            wgpuAdapterGetInfo(Adapter, out var info);
            try
            {
                Name = info.device.ToString();
                Driver = info.description.ToString();
                if (info.adapterType is WGPUAdapterType.CPU or WGPUAdapterType.Unknown)
                {
                    throw new NotSupportedException("wgpu Canvas는 하드웨어 GPU가 필요합니다.");
                }
            }
            finally
            {
                wgpuAdapterInfoFreeMembers(info);
            }
            var gray16 = (WGPUFeatureName)WGPUNativeFeature.TextureFormat16bitNorm;
            SupportsGray16 = wgpuAdapterHasFeature(Adapter, gray16);
            if (!SupportsGray16)
            {
                throw new NotSupportedException("이 GPU는 원본 Gray16 표시에 필요한 R16Unorm을 지원하지 않습니다.");
            }
            var limits = new WGPULimits();
            wgpuAdapterGetLimits(Adapter, &limits);
            Limits = limits;
            var descriptor = new WGPUDeviceDescriptor
            {
                requiredFeatureCount = 1,
                requiredFeatures = &gray16,
                requiredLimits = &limits,
                uncapturedErrorCallbackInfo = new()
                {
                    callback = &UncapturedError,
                    userdata1 = (void*)GCHandle.ToIntPtr(_callbackOwner)
                },
                deviceLostCallbackInfo = new()
                {
                    mode = WGPUCallbackMode.AllowSpontaneous,
                    callback = &DeviceLost,
                    userdata1 = (void*)GCHandle.ToIntPtr(_callbackOwner)
                }
            };
            wgpuAdapterRequestDevice(Adapter, &descriptor, new()
            {
                mode = WGPUCallbackMode.AllowSpontaneous,
                callback = &DeviceReady,
                userdata1 = (void*)GCHandle.ToIntPtr(_callbackOwner)
            });
            CheckErrors();
            if (Handle.IsNull)
            {
                throw new InvalidOperationException("wgpu 장치를 생성하지 못했습니다.");
            }
            Queue = wgpuDeviceGetQueue(Handle);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private static WgpuDevice Owner(void* value) => (WgpuDevice)GCHandle.FromIntPtr((nint)value).Target!;

    [UnmanagedCallersOnly]
    private static void AdapterReady(WGPURequestAdapterStatus status, WGPUAdapter adapter, WGPUStringView message, void* owner, void* unused)
    {
        if (status == WGPURequestAdapterStatus.Success)
        {
            Owner(owner).Adapter = adapter;
        }
        else
        {
            Owner(owner)._errors.Enqueue($"Adapter: {status} · {message}");
        }
    }

    [UnmanagedCallersOnly]
    private static void DeviceReady(WGPURequestDeviceStatus status, WGPUDevice device, WGPUStringView message, void* owner, void* unused)
    {
        if (status == WGPURequestDeviceStatus.Success)
        {
            Owner(owner).Handle = device;
        }
        else
        {
            Owner(owner)._errors.Enqueue($"Device: {status} · {message}");
        }
    }

    [UnmanagedCallersOnly]
    private static void UncapturedError(WGPUDevice* device, WGPUErrorType type, WGPUStringView message, void* owner, void* unused)
        => Owner(owner)._errors.Enqueue($"{type}: {message}");

    [UnmanagedCallersOnly]
    private static void DeviceLost(WGPUDevice* device, WGPUDeviceLostReason reason, WGPUStringView message, void* owner, void* unused)
    {
        if (reason != WGPUDeviceLostReason.Destroyed)
        {
            Owner(owner)._errors.Enqueue($"Device lost: {reason} · {message}");
        }
    }

    public void CheckErrors()
    {
        if (_errors.TryDequeue(out var message))
        {
            throw new InvalidOperationException(message);
        }
    }

    public void WaitIdle()
    {
        if (Handle.IsNotNull)
        {
            wgpuDevicePoll(Handle, true, null);
        }
    }

    public void Wait(ulong submission)
    {
        if (submission != 0)
        {
            wgpuDevicePoll(Handle, true, &submission);
            CheckErrors();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        WaitIdle();
        if (Surface.IsNotNull)
        {
            wgpuSurfaceRelease(Surface);
        }
        if (Queue.IsNotNull)
        {
            wgpuQueueRelease(Queue);
        }
        if (Handle.IsNotNull)
        {
            wgpuDeviceDestroy(Handle);
            wgpuDeviceRelease(Handle);
        }
        if (Adapter.IsNotNull)
        {
            wgpuAdapterRelease(Adapter);
        }
        if (Instance.IsNotNull)
        {
            wgpuInstanceRelease(Instance);
        }
        _callbackOwner.Free();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? name);
}
