using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;
using Vortice.DXGI;
using static Vortice.Direct3D12.D3D12;
using static Vortice.DXGI.DXGI;

namespace VisionCanvas.Rendering;

internal sealed class Dx12Device : IDisposable
{
    public IDXGIFactory4 Factory
    {
        get;
    }

    public ID3D12Device Device
    {
        get;
    }

    public ID3D12CommandQueue Queue
    {
        get;
    }

    public ID3D12Fence Fence
    {
        get;
    }

    public string AdapterName
    {
        get;
    } = "Unknown";

    public bool DebugEnabled
    {
        get;
    }

    public ulong DedicatedMemoryBytes
    {
        get;
    }

    public ulong LocalMemoryBudget
    {
        get;
    }

    public bool IsSoftware
    {
        get;
    }

    public long TileBudgetBytes => IsSoftware ? 256L * 1024 * 1024
        : (long)Math.Min(256UL * 1024 * 1024, Math.Max(16UL * 1024 * 1024, LocalMemoryBudget / 5));

    private readonly AutoResetEvent _fenceEvent = new(false);
    private ulong _signalValue;

    public IReadOnlyList<string> ReadDebugMessages()
    {
        if (!DebugEnabled)
        {
            return [];
        }

        using var queue = Device.QueryInterfaceOrNull<ID3D12InfoQueue>();
        if (queue is null)
        {
            return [];
        }

        var messages = new List<string>();
        for (ulong i = 0; i < queue.NumStoredMessagesAllowedByRetrievalFilter; i++)
        {
            var message = queue.GetMessage(i);
            if (message.Severity is MessageSeverity.Corruption or MessageSeverity.Error or MessageSeverity.Warning)
            {
                messages.Add($"{message.Severity}: {message.Description}");
            }
        }
        queue.ClearStoredMessages();
        return messages;
    }

    public Dx12Device(bool debug, RenderDevicePreference preference = RenderDevicePreference.Automatic)
        : this(debug, preference, EnumerateAdapters)
    {
    }

    // 열거 결과의 소유권을 받아 성공·실패 모두에서 adapter를 해제한다.
    internal Dx12Device(bool debug, RenderDevicePreference preference, Func<IDXGIFactory4, IReadOnlyList<IDXGIAdapter1>> enumerateAdapters)
    {
        try
        {
            if (!Enum.IsDefined(preference))
            {
                throw new ArgumentOutOfRangeException(nameof(preference));
            }
            if (debug && D3D12GetDebugInterface<ID3D12Debug>(out var debugInterface).Success)
            {
                using (debugInterface)
                {
                    debugInterface!.EnableDebugLayer();
                }
                DebugEnabled = true;
            }
            Factory = CreateDXGIFactory2<IDXGIFactory4>(DebugEnabled);
            if (preference == RenderDevicePreference.Automatic)
            {
                var adapters = enumerateAdapters(Factory);
                try
                {
                    foreach (var adapter in adapters.Where(a => (a.Description1.Flags & AdapterFlags.Software) == 0)
                                 .OrderByDescending(a => a.Description1.VendorId == 0x10DE)
                                 .ThenByDescending(a => a.Description1.DedicatedVideoMemory))
                    {
                        if (!D3D12CreateDevice(adapter, FeatureLevel.Level_11_0, out ID3D12Device? device).Success)
                        {
                            device?.Dispose();
                            continue;
                        }
                        Device = device!;
                        AdapterName = adapter.Description1.Description;
                        DedicatedMemoryBytes = (ulong)adapter.Description1.DedicatedVideoMemory;
                        using var memoryAdapter = adapter.QueryInterfaceOrNull<IDXGIAdapter3>();
                        LocalMemoryBudget = memoryAdapter?.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local).Budget ?? DedicatedMemoryBytes;
                        break;
                    }
                }
                finally
                {
                    foreach (var adapter in adapters)
                    {
                        adapter.Dispose();
                    }
                }
            }
            if (Device is null)
            {
                using var adapter = Factory.EnumWarpAdapter<IDXGIAdapter1>();
                var result = D3D12CreateDevice(adapter, FeatureLevel.Level_11_0, out ID3D12Device? device);
                Device = device!;
                if (result.Failure)
                {
                    throw new NotSupportedException($"Direct3D 12 소프트웨어 렌더러(WARP)를 시작하지 못했습니다. HRESULT: {result}");
                }
                AdapterName = adapter.Description1.Description;
                IsSoftware = true;
            }
            Queue = Device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Direct));
            Fence = Device.CreateFence(0);
        }
        catch
        {
            Fence?.Dispose();
            Queue?.Dispose();
            Device?.Dispose();
            Factory?.Dispose();
            _fenceEvent.Dispose();
            throw;
        }
    }

    private static IReadOnlyList<IDXGIAdapter1> EnumerateAdapters(IDXGIFactory4 factory)
    {
        var adapters = new List<IDXGIAdapter1>();
        for (uint index = 0; factory.EnumAdapters1(index, out var adapter).Success; index++)
        {
            adapters.Add(adapter);
        }
        return adapters;
    }

    public ulong Signal()
    {
        var value = ++_signalValue;
        Queue.Signal(Fence, value).CheckError();
        return value;
    }

    public void Wait(ulong value)
    {
        if (Fence.CompletedValue == ulong.MaxValue)
        {
            Device.DeviceRemovedReason.CheckError();
        }

        if (value == 0 || Fence.CompletedValue >= value)
        {
            return;
        }

        Fence.SetEventOnCompletion(value, _fenceEvent.SafeWaitHandle.DangerousGetHandle()).CheckError();
        if (!_fenceEvent.WaitOne(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("렌더 장치 응답 시간이 초과되었습니다.");
        }
    }

    public void Flush() => Wait(Signal());

    public void Dispose()
    {
        Fence.Dispose();
        Queue.Dispose();
        Device.Dispose();
        Factory.Dispose();
        _fenceEvent.Dispose();
    }
}
