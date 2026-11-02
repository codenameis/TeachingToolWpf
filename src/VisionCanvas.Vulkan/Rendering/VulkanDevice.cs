using System.Runtime.InteropServices;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace VisionCanvas.Vulkan.Rendering;

/// <summary>한 렌더 스레드의 Vulkan instance/device/queue 소유자. 다른 Canvas와 dispatch table을 공유하지 않는다.</summary>
internal sealed unsafe class VulkanDevice : IDisposable
{
    private static readonly object s_loaderGate = new();
    private VkInstance _instance;
    private VkDevice _device;
    private VkSurfaceKHR _surface;
    private bool _disposed;

    public VkInstanceApi InstanceApi { get; private set; } = null!;
    public VkDeviceApi Api { get; private set; } = null!;
    public VkPhysicalDevice PhysicalDevice { get; private set; }
    public VkDevice Handle => _device;
    public VkSurfaceKHR Surface => _surface;
    public VkQueue Queue { get; private set; }
    public uint QueueFamily { get; private set; }
    public VkPhysicalDeviceProperties Properties { get; private set; }
    public VkPhysicalDeviceMemoryProperties Memory { get; private set; }
    public string Name { get; private set; } = "";
    public long TileBudgetBytes { get; private set; }
    public ulong? LocalMemoryBudget { get; private set; }

    public VulkanDevice(nint hwnd)
    {
        try
        {
            lock (s_loaderGate)
            {
                Check(vkInitialize(), "Vulkan loader");
            }
            using var surfaceName = new Utf8Name("VK_KHR_surface");
            using var win32Name = new Utf8Name("VK_KHR_win32_surface");
            var extensions = stackalloc byte*[2] { surfaceName.Pointer, win32Name.Pointer };
            var application = new VkApplicationInfo { apiVersion = VK_API_VERSION_1_1 };
            var instanceInfo = new VkInstanceCreateInfo
            {
                pApplicationInfo = &application,
                enabledExtensionCount = 2,
                ppEnabledExtensionNames = extensions
            };
            Check(vkCreateInstance(in instanceInfo, out _instance), "vkCreateInstance");
            InstanceApi = new(_instance);
            var surfaceInfo = new VkWin32SurfaceCreateInfoKHR
            {
                hwnd = hwnd,
                hinstance = Marshal.GetHINSTANCE(typeof(VulkanDevice).Module)
            };
            Check(InstanceApi.vkCreateWin32SurfaceKHR(in surfaceInfo, out _surface), "vkCreateWin32SurfaceKHR");
            SelectDevice();
            var priority = 1f;
            var queueInfo = new VkDeviceQueueCreateInfo
            {
                queueFamilyIndex = QueueFamily,
                queueCount = 1,
                pQueuePriorities = &priority
            };
            using var swapchainName = new Utf8Name("VK_KHR_swapchain");
            var extension = swapchainName.Pointer;
            var deviceInfo = new VkDeviceCreateInfo
            {
                queueCreateInfoCount = 1,
                pQueueCreateInfos = &queueInfo,
                enabledExtensionCount = 1,
                ppEnabledExtensionNames = &extension
            };
            Check(InstanceApi.vkCreateDevice(PhysicalDevice, in deviceInfo, out _device), "vkCreateDevice");
            Api = new(InstanceApi, _device);
            Api.vkGetDeviceQueue(QueueFamily, 0, out var queue);
            Queue = queue;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void SelectDevice()
    {
        Check(InstanceApi.vkEnumeratePhysicalDevices(out var count), "vkEnumeratePhysicalDevices");
        var devices = new VkPhysicalDevice[count];
        Check(InstanceApi.vkEnumeratePhysicalDevices(devices), "vkEnumeratePhysicalDevices");
        var bestScore = -1;
        foreach (var device in devices)
        {
            var properties = InstanceApi.vkGetPhysicalDeviceProperties(device);
            if (properties.apiVersion < VK_API_VERSION_1_1 || properties.deviceType == VkPhysicalDeviceType.Cpu ||
                !Extensions(device).Contains("VK_KHR_swapchain"))
            {
                continue;
            }
            InstanceApi.vkGetPhysicalDeviceQueueFamilyProperties(device, out var queueCount);
            var families = new VkQueueFamilyProperties[queueCount];
            InstanceApi.vkGetPhysicalDeviceQueueFamilyProperties(device, families);
            for (var index = 0u; index < families.Length; index++)
            {
                Check(InstanceApi.vkGetPhysicalDeviceSurfaceSupportKHR(device, index, _surface, out var present), "surface support");
                if ((families[index].queueFlags & VkQueueFlags.Graphics) == 0 || !present)
                {
                    continue;
                }
                var score = properties.deviceType == VkPhysicalDeviceType.DiscreteGpu ? 2 : 1;
                if (score <= bestScore)
                {
                    continue;
                }
                bestScore = score;
                PhysicalDevice = device;
                QueueFamily = index;
                Properties = properties;
                Name = Marshal.PtrToStringUTF8((nint)properties.deviceName) ?? "Vulkan GPU";
            }
        }
        if (bestScore < 0)
        {
            throw new NotSupportedException("Vulkan 1.1 graphics/present GPU를 찾지 못했습니다.");
        }
        InstanceApi.vkGetPhysicalDeviceMemoryProperties(PhysicalDevice, out var memory);
        Memory = memory;
        var budget = new VkPhysicalDeviceMemoryBudgetPropertiesEXT();
        var hasBudget = Extensions(PhysicalDevice).Contains("VK_EXT_memory_budget");
        if (hasBudget)
        {
            var query = new VkPhysicalDeviceMemoryProperties2 { pNext = &budget };
            InstanceApi.vkGetPhysicalDeviceMemoryProperties2(PhysicalDevice, &query);
        }
        ulong localBytes = 0;
        ulong localBudget = 0;
        for (var index = 0; index < memory.memoryHeapCount; index++)
        {
            if ((memory.memoryHeaps[index].flags & VkMemoryHeapFlags.DeviceLocal) != 0)
            {
                localBytes = Math.Max(localBytes, memory.memoryHeaps[index].size);
                localBudget = Math.Max(localBudget, budget.heapBudget[index]);
            }
        }
        LocalMemoryBudget = hasBudget && localBudget > 0 ? localBudget : null;
        // 확장이 없는 장치는 heap 크기를 보수적 대체값으로 사용하되 가용 예산이라고 표시하지 않는다.
        TileBudgetBytes = Math.Clamp((long)((LocalMemoryBudget ?? localBytes) / 5), 16L * 1024 * 1024, 256L * 1024 * 1024);
    }

    private HashSet<string> Extensions(VkPhysicalDevice device)
    {
        Check(InstanceApi.vkEnumerateDeviceExtensionProperties(device, out var count), "device extensions");
        var extensions = new VkExtensionProperties[count];
        Check(InstanceApi.vkEnumerateDeviceExtensionProperties(device, extensions), "device extensions");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var extension in extensions)
        {
            names.Add(Marshal.PtrToStringUTF8((nint)extension.extensionName)!);
        }
        return names;
    }

    public uint FindMemory(uint allowed, VkMemoryPropertyFlags flags)
    {
        var memory = Memory;
        for (var index = 0u; index < memory.memoryTypeCount; index++)
        {
            if ((allowed & (1u << (int)index)) != 0 && (memory.memoryTypes[(int)index].propertyFlags & flags) == flags)
            {
                return index;
            }
        }
        throw new NotSupportedException($"Vulkan 메모리 형식을 찾지 못했습니다: {flags}.");
    }

    public void Wait(VkFence fence)
    {
        if (fence != VkFence.Null)
        {
            Check(Api.vkWaitForFences(fence, true, ulong.MaxValue), "vkWaitForFences");
        }
    }

    public void WaitIdle() => Check(Api.vkDeviceWaitIdle(), "vkDeviceWaitIdle");

    public static void Check(VkResult result, string operation)
    {
        if (result != VkResult.Success)
        {
            throw new InvalidOperationException($"{operation}: {result}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_device != VkDevice.Null)
        {
            Api.vkDeviceWaitIdle();
            Api.vkDestroyDevice();
        }
        if (_surface != VkSurfaceKHR.Null)
        {
            InstanceApi.vkDestroySurfaceKHR(_surface);
        }
        if (_instance != VkInstance.Null)
        {
            InstanceApi.vkDestroyInstance();
        }
    }
}

internal sealed unsafe class Utf8Name(string value) : IDisposable
{
    private readonly nint _value = Marshal.StringToCoTaskMemUTF8(value);
    public byte* Pointer => (byte*)_value;
    public void Dispose() => Marshal.FreeCoTaskMem(_value);
}
