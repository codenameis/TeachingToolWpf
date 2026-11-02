using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice.Vulkan;

namespace VisionCanvas.Vulkan.Rendering;

/// <summary>명시적 캡처만 readback한다. Save는 제출된 GPU 작업 완료 후 호출한다.</summary>
internal sealed unsafe class VulkanReadback : IDisposable
{
    private readonly VulkanBuffer _buffer;
    private readonly int _width;
    private readonly int _height;

    public VulkanReadback(VulkanDevice gpu, VulkanSurface surface)
    {
        _width = surface.Width;
        _height = surface.Height;
        _buffer = new(gpu, checked((ulong)_width * (ulong)_height * 4), VkBufferUsageFlags.TransferDst);
        VulkanTexture.Barrier(gpu, surface.Commands, surface.CurrentImage, VkImageLayout.PresentSrcKHR, VkImageLayout.TransferSrcOptimal,
            VkPipelineStageFlags.ColorAttachmentOutput, VkPipelineStageFlags.Transfer, VkAccessFlags.ColorAttachmentWrite, VkAccessFlags.TransferRead);
        var copy = new VkBufferImageCopy
        {
            imageSubresource = new(VkImageAspectFlags.Color, 0, 0, 1),
            imageExtent = new((uint)_width, (uint)_height, 1)
        };
        gpu.Api.vkCmdCopyImageToBuffer(surface.Commands, surface.CurrentImage, VkImageLayout.TransferSrcOptimal, _buffer.Handle, 1, &copy);
        var hostRead = new VkMemoryBarrier
        {
            srcAccessMask = VkAccessFlags.TransferWrite,
            dstAccessMask = VkAccessFlags.HostRead
        };
        gpu.Api.vkCmdPipelineBarrier(surface.Commands, VkPipelineStageFlags.Transfer, VkPipelineStageFlags.Host, 0, 1, &hostRead, 0, null, 0, null);
        VulkanTexture.Barrier(gpu, surface.Commands, surface.CurrentImage, VkImageLayout.TransferSrcOptimal, VkImageLayout.PresentSrcKHR,
            VkPipelineStageFlags.Transfer, VkPipelineStageFlags.BottomOfPipe, VkAccessFlags.TransferRead, 0);
    }

    public void Save(string path)
    {
        var pixels = new ReadOnlySpan<byte>((void*)_buffer.Pointer, checked(_width * _height * 4)).ToArray();
        var bitmap = BitmapSource.Create(_width, _height, 96, 96, PixelFormats.Bgra32, null, pixels, _width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    public void Dispose() => _buffer.Dispose();
}
