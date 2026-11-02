using Vortice.Vulkan;
using static VisionCanvas.Vulkan.Rendering.VulkanDevice;

namespace VisionCanvas.Vulkan.Rendering;

internal sealed unsafe class VulkanBuffer : IDisposable
{
    private readonly VulkanDevice _gpu;
    private VkBuffer _buffer;
    private VkDeviceMemory _memory;
    private void* _pointer;
    private bool _disposed;

    public VkBuffer Handle => _buffer;
    public ulong Size { get; }
    public ulong AllocatedBytes { get; private set; }
    public nint Pointer => (nint)_pointer;

    public VulkanBuffer(VulkanDevice gpu, ulong size, VkBufferUsageFlags usage)
    {
        _gpu = gpu;
        Size = Math.Max(size, 16);
        try
        {
            var info = new VkBufferCreateInfo { size = Size, usage = usage, sharingMode = VkSharingMode.Exclusive };
            Check(gpu.Api.vkCreateBuffer(in info, out _buffer), "vkCreateBuffer");
            gpu.Api.vkGetBufferMemoryRequirements(_buffer, out var requirements);
            var allocation = new VkMemoryAllocateInfo
            {
                allocationSize = requirements.size,
                memoryTypeIndex = gpu.FindMemory(requirements.memoryTypeBits, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent)
            };
            VkDeviceMemory memory;
            Check(gpu.Api.vkAllocateMemory(&allocation, null, &memory), "vkAllocateMemory(buffer)");
            _memory = memory;
            AllocatedBytes = requirements.size;
            Check(gpu.Api.vkBindBufferMemory(_buffer, _memory, 0), "vkBindBufferMemory");
            void* pointer;
            Check(gpu.Api.vkMapMemory(_memory, 0, requirements.size, 0, &pointer), "vkMapMemory");
            _pointer = pointer;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_pointer != null)
        {
            _gpu.Api.vkUnmapMemory(_memory);
        }
        _gpu.Api.vkDestroyBuffer(_buffer);
        _gpu.Api.vkFreeMemory(_memory);
    }
}

/// <summary>프레임 fence 완료 후 Reset한다. 소규모 상수/정점/업로드가 페이지를 공유한다.</summary>
internal sealed class VulkanUploadArena(VulkanDevice gpu) : IDisposable
{
    private readonly List<VulkanBuffer> _pages = [];
    private int _page;
    private ulong _offset;

    public readonly record struct Slice(VulkanBuffer Buffer, ulong Offset, ulong Size)
    {
        public nint Pointer => Buffer.Pointer + checked((nint)Offset);
        public VkDescriptorBufferInfo Descriptor => new() { buffer = Buffer.Handle, offset = Offset, range = Size };
    }

    public void Reset()
    {
        _page = 0;
        _offset = 0;
    }

    public Slice Allocate(ulong bytes)
    {
        var alignment = Math.Max(256UL, Math.Max(gpu.Properties.limits.minUniformBufferOffsetAlignment, gpu.Properties.limits.minStorageBufferOffsetAlignment));
        var size = Math.Max(16, (bytes + alignment - 1) / alignment * alignment);
        while (_page < _pages.Count && _offset + size > _pages[_page].Size)
        {
            _page++;
            _offset = 0;
        }
        if (_page == _pages.Count)
        {
            _pages.Add(new(gpu, Math.Max(size, 4 * 1024 * 1024), VkBufferUsageFlags.TransferSrc |
                VkBufferUsageFlags.VertexBuffer | VkBufferUsageFlags.UniformBuffer | VkBufferUsageFlags.StorageBuffer));
        }
        var slice = new Slice(_pages[_page], _offset, Math.Max(bytes, 16));
        _offset += size;
        return slice;
    }

    public void Dispose()
    {
        foreach (var page in _pages)
        {
            page.Dispose();
        }
    }
}
