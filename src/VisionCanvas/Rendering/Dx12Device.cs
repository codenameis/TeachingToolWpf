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

    public long TileBudgetBytes => (long)Math.Min(256UL * 1024 * 1024, Math.Max(16UL * 1024 * 1024, LocalMemoryBudget / 5));

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
        for (ulong i = 0;
i < queue.NumStoredMessagesAllowedByRetrievalFilter;
i++)
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

    public Dx12Device(bool debug)
    {
        if (debug && D3D12GetDebugInterface<ID3D12Debug>(out var debugInterface).Success)
        {
            using (debugInterface)
            {
                debugInterface!.EnableDebugLayer();
            }

            DebugEnabled = true;
        }
        Factory = CreateDXGIFactory2<IDXGIFactory4>(DebugEnabled);
        var adapters = new List<IDXGIAdapter1>();
        for (uint i = 0;
Factory.EnumAdapters1(i, out var adapter).Success;
i++)
        {
            if ((adapter.Description1.Flags & AdapterFlags.Software) != 0)
            {
                adapter.Dispose();
            }
            else
            {
                adapters.Add(adapter);
            }
        }
        try
        {
            foreach (var adapter in adapters.OrderByDescending(a => a.Description1.VendorId == 0x10DE)
                         .ThenByDescending(a => a.Description1.DedicatedVideoMemory))
            {
                if (!D3D12CreateDevice(adapter, FeatureLevel.Level_11_0, out ID3D12Device? device).Success)
                {
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
        if (Device is null)
        {
            Factory.Dispose();
            throw new NotSupportedException("Direct3D 12 하드웨어를 찾지 못했습니다.");
        }
        Queue = Device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Direct));
        Fence = Device.CreateFence(0);
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
            throw new TimeoutException("GPU 응답 시간이 초과되었습니다.");
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
