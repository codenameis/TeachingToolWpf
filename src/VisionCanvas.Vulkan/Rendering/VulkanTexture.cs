using VisionCanvas.Rendering.Abstractions;
using VisionImaging.Abstractions;
using Vortice.Vulkan;
using static VisionCanvas.Vulkan.Rendering.VulkanDevice;

namespace VisionCanvas.Vulkan.Rendering;

internal sealed unsafe class VulkanTexture : IRenderTexture
{
    private readonly VulkanDevice _gpu;
    private VkImage _image;
    private VkImageView _view;
    private VkDeviceMemory _memory;
    private bool _disposed;

    public VkImage Image => _image;
    public VkImageView View => _view;
    public PixelKind Kind { get; }
    public int Width { get; }
    public int Height { get; }
    public bool HasPixels
    {
        get; set;
    }
    public bool IsOpaque
    {
        get; set;
    }
    public long Bytes => (long)Width * Height * Kind switch
    {
        PixelKind.Gray8 => 1,
        PixelKind.Gray16 => 2,
        _ => 4
    };
    public long AllocatedBytes { get; private set; }

    public VulkanTexture(VulkanDevice gpu, int width, int height, PixelKind kind)
    {
        _gpu = gpu;
        Width = width;
        Height = height;
        Kind = kind;
        if (width <= 0 || height <= 0 || width > gpu.Properties.limits.maxImageDimension2D || height > gpu.Properties.limits.maxImageDimension2D)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Vulkan 텍스처 크기 제한을 넘었습니다.");
        }
        var format = kind switch
        {
            PixelKind.Gray8 => VkFormat.R8Unorm,
            PixelKind.Gray16 => VkFormat.R16Unorm,
            PixelKind.Bgra32 => VkFormat.B8G8R8A8Unorm,
            PixelKind.Rgba32 => VkFormat.R8G8B8A8Unorm,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        try
        {
            var info = new VkImageCreateInfo
            {
                imageType = VkImageType.Image2D,
                format = format,
                extent = new((uint)width, (uint)height, 1),
                mipLevels = 1,
                arrayLayers = 1,
                samples = VkSampleCountFlags.Count1,
                tiling = VkImageTiling.Optimal,
                usage = VkImageUsageFlags.TransferDst | VkImageUsageFlags.Sampled,
                sharingMode = VkSharingMode.Exclusive
            };
            Check(gpu.Api.vkCreateImage(in info, out _image), "vkCreateImage");
            gpu.Api.vkGetImageMemoryRequirements(_image, out var requirements);
            var allocation = new VkMemoryAllocateInfo
            {
                allocationSize = requirements.size,
                memoryTypeIndex = gpu.FindMemory(requirements.memoryTypeBits, VkMemoryPropertyFlags.DeviceLocal)
            };
            VkDeviceMemory memory;
            Check(gpu.Api.vkAllocateMemory(&allocation, null, &memory), "vkAllocateMemory(image)");
            _memory = memory;
            AllocatedBytes = checked((long)requirements.size);
            Check(gpu.Api.vkBindImageMemory(_image, _memory, 0), "vkBindImageMemory");
            var viewInfo = new VkImageViewCreateInfo
            {
                image = _image,
                viewType = VkImageViewType.Image2D,
                format = format,
                subresourceRange = new(VkImageAspectFlags.Color, 0, 1, 0, 1)
            };
            Check(gpu.Api.vkCreateImageView(in viewInfo, out _view), "vkCreateImageView(texture)");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public static void Barrier(VulkanDevice gpu, VkCommandBuffer commands, VkImage image,
        VkImageLayout before, VkImageLayout after, VkPipelineStageFlags sourceStage, VkPipelineStageFlags targetStage,
        VkAccessFlags sourceAccess, VkAccessFlags targetAccess)
    {
        var barrier = new VkImageMemoryBarrier
        {
            image = image,
            oldLayout = before,
            newLayout = after,
            srcQueueFamilyIndex = uint.MaxValue,
            dstQueueFamilyIndex = uint.MaxValue,
            srcAccessMask = sourceAccess,
            dstAccessMask = targetAccess,
            subresourceRange = new(VkImageAspectFlags.Color, 0, 1, 0, 1)
        };
        gpu.Api.vkCmdPipelineBarrier(commands, sourceStage, targetStage, 0, 0, null, 0, null, 1, &barrier);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _gpu.Api.vkDestroyImageView(_view);
        _gpu.Api.vkDestroyImage(_image);
        _gpu.Api.vkFreeMemory(_memory);
    }
}
