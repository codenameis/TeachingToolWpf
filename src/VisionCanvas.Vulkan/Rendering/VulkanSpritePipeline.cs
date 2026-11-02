using System.Runtime.InteropServices;
using VisionCanvas.Rendering;
using VisionImaging.Abstractions;
using Vortice.Vulkan;
using static VisionCanvas.Vulkan.Rendering.VulkanDevice;

namespace VisionCanvas.Vulkan.Rendering;

internal sealed unsafe class VulkanSpritePipeline : ITexturePipeline, IDisposable
{
    private readonly VulkanDevice _gpu;
    private readonly VulkanFrameResources[] _frames;
    private readonly VulkanPipeline _pipeline;
    private VkSampler _linear;
    private VkSampler _point;

    public VulkanSpritePipeline(VulkanDevice gpu, VulkanSurface surface, VulkanFrameResources[] frames)
    {
        _gpu = gpu;
        _frames = frames;
        _pipeline = new(gpu, surface, "Viewport.hlsl", true);
        try
        {
            _linear = CreateSampler(VkFilter.Linear);
            _point = CreateSampler(VkFilter.Nearest);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private VkSampler CreateSampler(VkFilter filter)
    {
        var info = new VkSamplerCreateInfo
        {
            magFilter = filter,
            minFilter = filter,
            mipmapMode = VkSamplerMipmapMode.Nearest,
            addressModeU = VkSamplerAddressMode.ClampToEdge,
            addressModeV = VkSamplerAddressMode.ClampToEdge,
            addressModeW = VkSamplerAddressMode.ClampToEdge,
            maxLod = 0
        };
        Check(_gpu.Api.vkCreateSampler(in info, out var sampler), "vkCreateSampler");
        return sampler;
    }

    public IRenderTexture CreateTexture(int width, int height, PixelKind kind) => new VulkanTexture(_gpu, width, height, kind);

    public IRenderTexture Upload(PixelBuffer pixels, IRenderSurface surface)
    {
        var texture = new VulkanTexture(_gpu, pixels.Width, pixels.Height, pixels.Kind);
        try
        {
            Update(texture, pixels, surface);
            texture.IsOpaque = TextureTransparency.PixelsAreOpaque(pixels);
            return texture;
        }
        catch
        {
            surface.Retire(texture);
            throw;
        }
    }

    public void Update(IRenderTexture target, PixelBuffer pixels, IRenderSurface targetSurface)
    {
        var texture = (VulkanTexture)target;
        var surface = (VulkanSurface)targetSurface;
        if (texture.Width != pixels.Width || texture.Height != pixels.Height || texture.Kind != pixels.Kind)
        {
            throw new ArgumentException("텍스처와 프레임 형식이 다릅니다.");
        }
        var upload = _frames[surface.FrameIndex].Uploads.Allocate((ulong)pixels.Length);
        pixels.Span.CopyTo(new Span<byte>((void*)upload.Pointer, pixels.Length));
        VulkanTexture.Barrier(_gpu, surface.Commands, texture.Image,
            texture.HasPixels ? VkImageLayout.ShaderReadOnlyOptimal : VkImageLayout.Undefined, VkImageLayout.TransferDstOptimal,
            texture.HasPixels ? VkPipelineStageFlags.FragmentShader : VkPipelineStageFlags.TopOfPipe, VkPipelineStageFlags.Transfer,
            texture.HasPixels ? VkAccessFlags.ShaderRead : 0, VkAccessFlags.TransferWrite);
        var region = new VkBufferImageCopy
        {
            bufferOffset = upload.Offset,
            imageSubresource = new(VkImageAspectFlags.Color, 0, 0, 1),
            imageExtent = new((uint)pixels.Width, (uint)pixels.Height, 1)
        };
        _gpu.Api.vkCmdCopyBufferToImage(surface.Commands, upload.Buffer.Handle, texture.Image, VkImageLayout.TransferDstOptimal, 1, &region);
        VulkanTexture.Barrier(_gpu, surface.Commands, texture.Image, VkImageLayout.TransferDstOptimal, VkImageLayout.ShaderReadOnlyOptimal,
            VkPipelineStageFlags.Transfer, VkPipelineStageFlags.FragmentShader, VkAccessFlags.TransferWrite, VkAccessFlags.ShaderRead);
        texture.HasPixels = true;
        texture.IsOpaque = pixels.Kind is PixelKind.Gray8 or PixelKind.Gray16;
    }

    public void Draw(SpriteMesh mesh, VulkanSurface surface, int middleBatch, Action middle)
    {
        var frame = _frames[surface.FrameIndex];
        var vertices = frame.Write<SpriteVertex>(CollectionsMarshal.AsSpan(mesh.Vertices));
        Bind();
        for (var index = 0; index < mesh.Batches.Count; index++)
        {
            if (index == middleBatch)
            {
                middle();
                Bind();
            }
            var batch = mesh.Batches[index];
            BindTexture(frame, surface, (VulkanTexture)batch.Texture, batch.PointSampling);
            _gpu.Api.vkCmdDraw(surface.Commands, (uint)batch.Count, 1, (uint)batch.Start, 0);
        }
        if (middleBatch == mesh.Batches.Count)
        {
            middle();
        }

        void Bind()
        {
            _pipeline.Bind(surface);
            var buffer = vertices.Buffer.Handle;
            var offset = vertices.Offset;
            _gpu.Api.vkCmdBindVertexBuffers(surface.Commands, 0, 1, &buffer, &offset);
        }
    }

    private void BindTexture(VulkanFrameResources frame, VulkanSurface surface, VulkanTexture texture, bool pointSampling)
    {
        var constants = new SpriteOptions(texture.Kind is PixelKind.Gray8 or PixelKind.Gray16 ? 1u : 0, pointSampling ? 1u : 0);
        var uniform = frame.Write(MemoryMarshal.CreateReadOnlySpan(in constants, 1)).Descriptor;
        var image = new VkDescriptorImageInfo { imageView = texture.View, imageLayout = VkImageLayout.ShaderReadOnlyOptimal };
        var linear = new VkDescriptorImageInfo { sampler = _linear };
        var point = new VkDescriptorImageInfo { sampler = _point };
        var set = frame.Allocate(_pipeline.SetLayout);
        var writes = stackalloc VkWriteDescriptorSet[4];
        writes[0] = new()
        {
            dstSet = set,
            dstBinding = 0,
            descriptorCount = 1,
            descriptorType = VkDescriptorType.SampledImage,
            pImageInfo = &image
        };
        writes[1] = new()
        {
            dstSet = set,
            dstBinding = 2,
            descriptorCount = 1,
            descriptorType = VkDescriptorType.UniformBuffer,
            pBufferInfo = &uniform
        };
        writes[2] = new()
        {
            dstSet = set,
            dstBinding = 3,
            descriptorCount = 1,
            descriptorType = VkDescriptorType.Sampler,
            pImageInfo = &linear
        };
        writes[3] = new()
        {
            dstSet = set,
            dstBinding = 4,
            descriptorCount = 1,
            descriptorType = VkDescriptorType.Sampler,
            pImageInfo = &point
        };
        _gpu.Api.vkUpdateDescriptorSets(4, writes, 0, null);
        _gpu.Api.vkCmdBindDescriptorSets(surface.Commands, VkPipelineBindPoint.Graphics, _pipeline.Layout, 0, set);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct SpriteOptions(uint Grayscale, uint PointSampling);

    public void Dispose()
    {
        _gpu.Api.vkDestroySampler(_point);
        _gpu.Api.vkDestroySampler(_linear);
        _pipeline.Dispose();
    }
}
