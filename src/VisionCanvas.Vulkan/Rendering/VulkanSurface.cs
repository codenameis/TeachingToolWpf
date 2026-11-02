using VisionCanvas.Rendering;
using Vortice.Vulkan;
using static VisionCanvas.Vulkan.Rendering.VulkanDevice;

namespace VisionCanvas.Vulkan.Rendering;

/// <summary>두 프레임 슬롯과 swapchain 이미지별 present semaphore를 소유한다.</summary>
internal sealed unsafe class VulkanSurface : IRenderSurface, IDisposable
{
    private readonly VulkanDevice _gpu;
    private readonly Frame[] _frames = [new(), new()];
    private VkSwapchainKHR _swapchain;
    private VkImage[] _images = [];
    private VkImageView[] _views = [];
    private VkFramebuffer[] _targets = [];
    private VkSemaphore[] _presentReady = [];
    private uint _imageIndex;
    private bool _recreate;
    private bool _disposed;

    private sealed class Frame
    {
        public VkCommandPool Pool;
        public VkCommandBuffer Commands;
        public VkFence Fence;
        public VkSemaphore Acquired;
        public List<IDisposable> Retired { get; } = [];
    }

    public int FrameIndex { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public VkFormat Format { get; private set; }
    public VkRenderPass RenderPass { get; private set; }
    public VkCommandBuffer Commands => _frames[FrameIndex].Commands;
    public VkImage CurrentImage => _images[_imageIndex];

    public VulkanSurface(VulkanDevice gpu, int width, int height)
    {
        _gpu = gpu;
        try
        {
            foreach (var frame in _frames)
            {
                var poolInfo = new VkCommandPoolCreateInfo { queueFamilyIndex = gpu.QueueFamily, flags = VkCommandPoolCreateFlags.ResetCommandBuffer };
                Check(gpu.Api.vkCreateCommandPool(in poolInfo, out frame.Pool), "vkCreateCommandPool");
                var allocation = new VkCommandBufferAllocateInfo { commandPool = frame.Pool, level = VkCommandBufferLevel.Primary, commandBufferCount = 1 };
                VkCommandBuffer commands;
                Check(gpu.Api.vkAllocateCommandBuffers(&allocation, &commands), "vkAllocateCommandBuffers");
                frame.Commands = commands;
                var fenceInfo = new VkFenceCreateInfo { flags = VkFenceCreateFlags.Signaled };
                Check(gpu.Api.vkCreateFence(in fenceInfo, out frame.Fence), "vkCreateFence");
                var semaphoreInfo = new VkSemaphoreCreateInfo();
                Check(gpu.Api.vkCreateSemaphore(in semaphoreInfo, out frame.Acquired), "vkCreateSemaphore");
            }
            CreateSwapchain(Math.Max(1, width), Math.Max(1, height));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void CreateSwapchain(int width, int height)
    {
        var instance = _gpu.InstanceApi;
        Check(instance.vkGetPhysicalDeviceSurfaceCapabilitiesKHR(_gpu.PhysicalDevice, _gpu.Surface, out var capabilities), "surface capabilities");
        Check(instance.vkGetPhysicalDeviceSurfaceFormatsKHR(_gpu.PhysicalDevice, _gpu.Surface, out var formatCount), "surface formats");
        var formats = new VkSurfaceFormatKHR[formatCount];
        Check(instance.vkGetPhysicalDeviceSurfaceFormatsKHR(_gpu.PhysicalDevice, _gpu.Surface, formats), "surface formats");
        var selected = formats.FirstOrDefault(format => format.format == VkFormat.B8G8R8A8Unorm && format.colorSpace == VkColorSpaceKHR.SrgbNonLinear);
        if (selected.format != VkFormat.B8G8R8A8Unorm)
        {
            throw new NotSupportedException("Vulkan surface의 BGRA8 UNORM 형식이 필요합니다.");
        }
        Format = selected.format;
        var extent = capabilities.currentExtent.width == uint.MaxValue
            ? new VkExtent2D(Math.Clamp((uint)width, capabilities.minImageExtent.width, capabilities.maxImageExtent.width),
                Math.Clamp((uint)height, capabilities.minImageExtent.height, capabilities.maxImageExtent.height))
            : capabilities.currentExtent;
        var count = Math.Max(2u, capabilities.minImageCount);
        if (capabilities.maxImageCount > 0)
        {
            count = Math.Min(count, capabilities.maxImageCount);
        }
        var usage = VkImageUsageFlags.ColorAttachment | VkImageUsageFlags.TransferSrc;
        if ((capabilities.supportedUsageFlags & usage) != usage)
        {
            throw new NotSupportedException("Vulkan surface가 표시/캡처 용도를 지원하지 않습니다.");
        }
        var swapchainInfo = new VkSwapchainCreateInfoKHR
        {
            surface = _gpu.Surface,
            minImageCount = count,
            imageFormat = Format,
            imageColorSpace = selected.colorSpace,
            imageExtent = extent,
            imageArrayLayers = 1,
            imageUsage = usage,
            imageSharingMode = VkSharingMode.Exclusive,
            preTransform = capabilities.currentTransform,
            compositeAlpha = VkCompositeAlphaFlagsKHR.Opaque,
            presentMode = VkPresentModeKHR.Fifo,
            clipped = true
        };
        Check(_gpu.Api.vkCreateSwapchainKHR(in swapchainInfo, out _swapchain), "vkCreateSwapchainKHR");
        Width = (int)extent.width;
        Height = (int)extent.height;
        Check(_gpu.Api.vkGetSwapchainImagesKHR(_swapchain, out var imageCount), "vkGetSwapchainImagesKHR");
        _images = new VkImage[imageCount];
        Check(_gpu.Api.vkGetSwapchainImagesKHR(_swapchain, _images), "vkGetSwapchainImagesKHR");
        _views = new VkImageView[imageCount];
        _targets = new VkFramebuffer[imageCount];
        _presentReady = new VkSemaphore[imageCount];
        if (RenderPass == VkRenderPass.Null)
        {
            CreateRenderPass();
        }
        for (var index = 0; index < imageCount; index++)
        {
            var viewInfo = new VkImageViewCreateInfo
            {
                image = _images[index],
                viewType = VkImageViewType.Image2D,
                format = Format,
                subresourceRange = new(VkImageAspectFlags.Color, 0, 1, 0, 1)
            };
            Check(_gpu.Api.vkCreateImageView(in viewInfo, out _views[index]), "vkCreateImageView");
            var view = _views[index];
            var targetInfo = new VkFramebufferCreateInfo
            {
                renderPass = RenderPass,
                attachmentCount = 1,
                pAttachments = &view,
                width = extent.width,
                height = extent.height,
                layers = 1
            };
            Check(_gpu.Api.vkCreateFramebuffer(in targetInfo, out _targets[index]), "vkCreateFramebuffer");
            var semaphoreInfo = new VkSemaphoreCreateInfo();
            Check(_gpu.Api.vkCreateSemaphore(in semaphoreInfo, out _presentReady[index]), "vkCreateSemaphore");
        }
    }

    private void CreateRenderPass()
    {
        var attachment = new VkAttachmentDescription
        {
            format = Format,
            samples = VkSampleCountFlags.Count1,
            loadOp = VkAttachmentLoadOp.Clear,
            storeOp = VkAttachmentStoreOp.Store,
            stencilLoadOp = VkAttachmentLoadOp.DontCare,
            stencilStoreOp = VkAttachmentStoreOp.DontCare,
            initialLayout = VkImageLayout.Undefined,
            finalLayout = VkImageLayout.PresentSrcKHR
        };
        var color = new VkAttachmentReference(0, VkImageLayout.ColorAttachmentOptimal);
        var subpass = new VkSubpassDescription { pipelineBindPoint = VkPipelineBindPoint.Graphics, colorAttachmentCount = 1, pColorAttachments = &color };
        var dependency = new VkSubpassDependency
        {
            srcSubpass = uint.MaxValue,
            dstSubpass = 0,
            srcStageMask = VkPipelineStageFlags.ColorAttachmentOutput,
            dstStageMask = VkPipelineStageFlags.ColorAttachmentOutput,
            dstAccessMask = VkAccessFlags.ColorAttachmentWrite
        };
        var info = new VkRenderPassCreateInfo
        {
            attachmentCount = 1,
            pAttachments = &attachment,
            subpassCount = 1,
            pSubpasses = &subpass,
            dependencyCount = 1,
            pDependencies = &dependency
        };
        Check(_gpu.Api.vkCreateRenderPass(in info, out var pass), "vkCreateRenderPass");
        RenderPass = pass;
    }

    public bool Begin(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return false;
        }
        if (_recreate || width != Width || height != Height)
        {
            _gpu.WaitIdle();
            DestroySwapchain();
            CreateSwapchain(width, height);
            _recreate = false;
        }
        var frame = _frames[FrameIndex];
        // 유한 대기로 종료 신호를 주기적으로 확인한다. UI 스레드는 이 fence를 기다리지 않는다.
        var wait = _gpu.Api.vkWaitForFences(frame.Fence, true, 100_000_000);
        if (wait == VkResult.Timeout)
        {
            return false;
        }
        Check(wait, "vkWaitForFences");
        var acquire = _gpu.Api.vkAcquireNextImageKHR(_swapchain, 100_000_000, frame.Acquired, VkFence.Null, out _imageIndex);
        if (acquire == VkResult.ErrorOutOfDateKHR)
        {
            _recreate = true;
            return false;
        }
        if (acquire is VkResult.Timeout or VkResult.NotReady)
        {
            return false;
        }
        if (acquire == VkResult.SuboptimalKHR)
        {
            _recreate = true;
        }
        else
        {
            Check(acquire, "vkAcquireNextImageKHR");
        }
        foreach (var resource in frame.Retired)
        {
            resource.Dispose();
        }
        frame.Retired.Clear();
        Check(_gpu.Api.vkResetCommandPool(frame.Pool, 0), "vkResetCommandPool");
        var begin = new VkCommandBufferBeginInfo { flags = VkCommandBufferUsageFlags.OneTimeSubmit };
        Check(_gpu.Api.vkBeginCommandBuffer(Commands, &begin), "vkBeginCommandBuffer");
        return true;
    }

    public void BeginDrawing()
    {
        var clear = new VkClearValue(new VkClearColorValue(.045f, .06f, .085f, 1));
        var begin = new VkRenderPassBeginInfo
        {
            renderPass = RenderPass,
            framebuffer = _targets[_imageIndex],
            renderArea = new VkRect2D(0, 0, (uint)Width, (uint)Height),
            clearValueCount = 1,
            pClearValues = &clear
        };
        _gpu.Api.vkCmdBeginRenderPass(Commands, &begin, VkSubpassContents.Inline);
        var viewport = new VkViewport(0, 0, Width, Height, 0, 1);
        _gpu.Api.vkCmdSetViewport(Commands, 0, 1, &viewport);
        var scissor = new VkRect2D(0, 0, (uint)Width, (uint)Height);
        _gpu.Api.vkCmdSetScissor(Commands, 0, 1, &scissor);
    }

    public void EndDrawing() => _gpu.Api.vkCmdEndRenderPass(Commands);

    public VkFence Present()
    {
        var frame = _frames[FrameIndex];
        Check(_gpu.Api.vkEndCommandBuffer(Commands), "vkEndCommandBuffer");
        Check(_gpu.Api.vkResetFences(frame.Fence), "vkResetFences");
        var acquired = frame.Acquired;
        var ready = _presentReady[_imageIndex];
        var commands = Commands;
        var stage = VkPipelineStageFlags.ColorAttachmentOutput;
        var submit = new VkSubmitInfo
        {
            waitSemaphoreCount = 1,
            pWaitSemaphores = &acquired,
            pWaitDstStageMask = &stage,
            commandBufferCount = 1,
            pCommandBuffers = &commands,
            signalSemaphoreCount = 1,
            pSignalSemaphores = &ready
        };
        Check(_gpu.Api.vkQueueSubmit(_gpu.Queue, 1, &submit, frame.Fence), "vkQueueSubmit");
        var chain = _swapchain;
        var index = _imageIndex;
        var present = new VkPresentInfoKHR
        {
            waitSemaphoreCount = 1,
            pWaitSemaphores = &ready,
            swapchainCount = 1,
            pSwapchains = &chain,
            pImageIndices = &index
        };
        var result = _gpu.Api.vkQueuePresentKHR(_gpu.Queue, &present);
        if (result is VkResult.ErrorOutOfDateKHR or VkResult.SuboptimalKHR)
        {
            _recreate = true;
        }
        else
        {
            Check(result, "vkQueuePresentKHR");
        }
        FrameIndex = (FrameIndex + 1) % _frames.Length;
        return frame.Fence;
    }

    public void Retire(IDisposable resource) => _frames[FrameIndex].Retired.Add(resource);

    private void DestroySwapchain()
    {
        foreach (var target in _targets)
        {
            _gpu.Api.vkDestroyFramebuffer(target);
        }
        foreach (var view in _views)
        {
            _gpu.Api.vkDestroyImageView(view);
        }
        foreach (var semaphore in _presentReady)
        {
            _gpu.Api.vkDestroySemaphore(semaphore);
        }
        _targets = [];
        _views = [];
        _presentReady = [];
        _gpu.Api.vkDestroySwapchainKHR(_swapchain);
        _swapchain = VkSwapchainKHR.Null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _gpu.Api.vkDeviceWaitIdle();
        DestroySwapchain();
        _gpu.Api.vkDestroyRenderPass(RenderPass);
        foreach (var frame in _frames)
        {
            foreach (var resource in frame.Retired)
            {
                resource.Dispose();
            }
            _gpu.Api.vkDestroySemaphore(frame.Acquired);
            _gpu.Api.vkDestroyFence(frame.Fence);
            _gpu.Api.vkDestroyCommandPool(frame.Pool);
        }
    }
}
