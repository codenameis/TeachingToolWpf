using System.IO;
using System.Runtime.InteropServices;
using Vortice.ShaderCompiler;
using Vortice.Vulkan;
using static VisionCanvas.Vulkan.Rendering.VulkanDevice;

namespace VisionCanvas.Vulkan.Rendering;

/// <summary>공통 HLSL을 명시적인 Vulkan descriptor 배치로 컴파일한다.</summary>
internal sealed unsafe class VulkanPipeline : IDisposable
{
    private readonly VulkanDevice _gpu;
    private VkPipeline _pipeline;
    private VkPipelineLayout _layout;
    private VkDescriptorSetLayout _setLayout;
    public VkPipelineLayout Layout => _layout;
    public VkDescriptorSetLayout SetLayout => _setLayout;

    public VulkanPipeline(VulkanDevice gpu, VulkanSurface surface, string shader, bool sprites)
    {
        _gpu = gpu;
        VkShaderModule vertex = default;
        VkShaderModule fragment = default;
        try
        {
            vertex = Compile(shader, ShaderKind.VertexShader, "VSMain");
            fragment = Compile(shader, ShaderKind.FragmentShader, "PSMain");
            var bindings = stackalloc VkDescriptorSetLayoutBinding[sprites ? 5 : 3];
            bindings[0] = new()
            {
                binding = 0,
                descriptorType = sprites ? VkDescriptorType.SampledImage : VkDescriptorType.StorageBuffer,
                descriptorCount = 1,
                stageFlags = VkShaderStageFlags.AllGraphics
            };
            bindings[1] = new()
            {
                binding = 1,
                descriptorType = VkDescriptorType.StorageBuffer,
                descriptorCount = 1,
                stageFlags = VkShaderStageFlags.AllGraphics
            };
            bindings[2] = new()
            {
                binding = 2,
                descriptorType = VkDescriptorType.UniformBuffer,
                descriptorCount = 1,
                stageFlags = VkShaderStageFlags.AllGraphics
            };
            if (sprites)
            {
                bindings[3] = new()
                {
                    binding = 3,
                    descriptorType = VkDescriptorType.Sampler,
                    descriptorCount = 1,
                    stageFlags = VkShaderStageFlags.Fragment
                };
                bindings[4] = new()
                {
                    binding = 4,
                    descriptorType = VkDescriptorType.Sampler,
                    descriptorCount = 1,
                    stageFlags = VkShaderStageFlags.Fragment
                };
            }
            var descriptorInfo = new VkDescriptorSetLayoutCreateInfo { bindingCount = sprites ? 5u : 3u, pBindings = bindings };
            Check(gpu.Api.vkCreateDescriptorSetLayout(in descriptorInfo, out _setLayout), "vkCreateDescriptorSetLayout");
            var setLayout = _setLayout;
            var layoutInfo = new VkPipelineLayoutCreateInfo { setLayoutCount = 1, pSetLayouts = &setLayout };
            Check(gpu.Api.vkCreatePipelineLayout(in layoutInfo, out _layout), "vkCreatePipelineLayout");
            using var vertexEntry = new Utf8Name("VSMain");
            using var fragmentEntry = new Utf8Name("PSMain");
            var stages = stackalloc VkPipelineShaderStageCreateInfo[2];
            stages[0] = new()
            {
                stage = VkShaderStageFlags.Vertex,
                module = vertex,
                pName = vertexEntry.Pointer
            };
            stages[1] = new()
            {
                stage = VkShaderStageFlags.Fragment,
                module = fragment,
                pName = fragmentEntry.Pointer
            };
            var binding = new VkVertexInputBindingDescription { binding = 0, stride = 32, inputRate = VkVertexInputRate.Vertex };
            var attributes = stackalloc VkVertexInputAttributeDescription[3]
            {
                new() { location = 0, binding = 0, format = VkFormat.R32G32Sfloat, offset = 0 },
                new() { location = 1, binding = 0, format = VkFormat.R32G32Sfloat, offset = 8 },
                new() { location = 2, binding = 0, format = VkFormat.R32G32B32A32Sfloat, offset = 16 }
            };
            var vertexInput = new VkPipelineVertexInputStateCreateInfo
            {
                vertexBindingDescriptionCount = sprites ? 1u : 0,
                pVertexBindingDescriptions = &binding,
                vertexAttributeDescriptionCount = sprites ? 3u : 0,
                pVertexAttributeDescriptions = attributes
            };
            var assembly = new VkPipelineInputAssemblyStateCreateInfo { topology = VkPrimitiveTopology.TriangleList };
            var viewport = new VkPipelineViewportStateCreateInfo { viewportCount = 1, scissorCount = 1 };
            var raster = new VkPipelineRasterizationStateCreateInfo
            {
                polygonMode = VkPolygonMode.Fill,
                cullMode = VkCullModeFlags.None,
                frontFace = VkFrontFace.Clockwise,
                lineWidth = 1
            };
            var multisample = new VkPipelineMultisampleStateCreateInfo { rasterizationSamples = VkSampleCountFlags.Count1 };
            var blendAttachment = new VkPipelineColorBlendAttachmentState
            {
                blendEnable = true,
                srcColorBlendFactor = VkBlendFactor.SrcAlpha,
                dstColorBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
                colorBlendOp = VkBlendOp.Add,
                srcAlphaBlendFactor = VkBlendFactor.One,
                dstAlphaBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
                alphaBlendOp = VkBlendOp.Add,
                colorWriteMask = VkColorComponentFlags.All
            };
            var blend = new VkPipelineColorBlendStateCreateInfo { attachmentCount = 1, pAttachments = &blendAttachment };
            var dynamicStates = stackalloc VkDynamicState[2] { VkDynamicState.Viewport, VkDynamicState.Scissor };
            var dynamicState = new VkPipelineDynamicStateCreateInfo { dynamicStateCount = 2, pDynamicStates = dynamicStates };
            var info = new VkGraphicsPipelineCreateInfo
            {
                stageCount = 2,
                pStages = stages,
                pVertexInputState = &vertexInput,
                pInputAssemblyState = &assembly,
                pViewportState = &viewport,
                pRasterizationState = &raster,
                pMultisampleState = &multisample,
                pColorBlendState = &blend,
                pDynamicState = &dynamicState,
                layout = _layout,
                renderPass = surface.RenderPass
            };
            VkPipeline pipeline;
            Check(gpu.Api.vkCreateGraphicsPipelines(VkPipelineCache.Null, 1, &info, &pipeline), "vkCreateGraphicsPipelines");
            _pipeline = pipeline;
        }
        catch
        {
            Dispose();
            throw;
        }
        finally
        {
            gpu.Api.vkDestroyShaderModule(vertex);
            gpu.Api.vkDestroyShaderModule(fragment);
        }
    }

    private VkShaderModule Compile(string file, ShaderKind stage, string entry)
    {
        var bytecode = VulkanShaderCompiler.Compile(Path.Combine(AppContext.BaseDirectory, "Shaders", file), stage, entry);
        Check(_gpu.Api.vkCreateShaderModule(bytecode, null, out var module), "vkCreateShaderModule");
        return module;
    }

    public void Bind(VulkanSurface surface) => _gpu.Api.vkCmdBindPipeline(surface.Commands, VkPipelineBindPoint.Graphics, _pipeline);

    public void Dispose()
    {
        _gpu.Api.vkDestroyPipeline(_pipeline);
        _gpu.Api.vkDestroyPipelineLayout(_layout);
        _gpu.Api.vkDestroyDescriptorSetLayout(_setLayout);
    }
}

/// <summary>프레임 fence 이후에만 descriptor pool과 업로드 페이지를 재사용한다.</summary>
internal sealed unsafe class VulkanFrameResources : IDisposable
{
    private const uint SetsPerPool = 2048;
    private readonly VulkanDevice _gpu;
    private readonly List<VkDescriptorPool> _pools = [];
    private int _poolIndex;
    private uint _setCount;
    public VulkanUploadArena Uploads { get; }

    public VulkanFrameResources(VulkanDevice gpu)
    {
        _gpu = gpu;
        Uploads = new(gpu);
    }

    public void Reset()
    {
        foreach (var pool in _pools)
        {
            Check(_gpu.Api.vkResetDescriptorPool(pool, 0), "vkResetDescriptorPool");
        }
        _poolIndex = 0;
        _setCount = 0;
        Uploads.Reset();
    }

    public VkDescriptorSet Allocate(VkDescriptorSetLayout layout)
    {
        if (_setCount == SetsPerPool)
        {
            _poolIndex++;
            _setCount = 0;
        }
        if (_poolIndex == _pools.Count)
        {
            var sizes = stackalloc VkDescriptorPoolSize[4]
            {
                new(VkDescriptorType.UniformBuffer, SetsPerPool),
                new(VkDescriptorType.StorageBuffer, SetsPerPool * 2),
                new(VkDescriptorType.SampledImage, SetsPerPool),
                new(VkDescriptorType.Sampler, SetsPerPool * 2)
            };
            var poolInfo = new VkDescriptorPoolCreateInfo { maxSets = SetsPerPool, poolSizeCount = 4, pPoolSizes = sizes };
            Check(_gpu.Api.vkCreateDescriptorPool(in poolInfo, out var pool), "vkCreateDescriptorPool");
            _pools.Add(pool);
        }
        var info = new VkDescriptorSetAllocateInfo { descriptorPool = _pools[_poolIndex], descriptorSetCount = 1, pSetLayouts = &layout };
        Check(_gpu.Api.vkAllocateDescriptorSets(in info, out var set), "vkAllocateDescriptorSets");
        _setCount++;
        return set;
    }

    public VulkanUploadArena.Slice Write<T>(ReadOnlySpan<T> values) where T : unmanaged
    {
        var bytes = MemoryMarshal.AsBytes(values);
        var slice = Uploads.Allocate((ulong)bytes.Length);
        bytes.CopyTo(new Span<byte>((void*)slice.Pointer, bytes.Length));
        return slice;
    }

    public void BindBuffers<T>(VulkanPipeline pipeline, VulkanSurface surface, in T constants,
        VkDescriptorBufferInfo first, VkDescriptorBufferInfo second) where T : unmanaged
    {
        var uniform = Write(MemoryMarshal.CreateReadOnlySpan(in constants, 1)).Descriptor;
        var set = Allocate(pipeline.SetLayout);
        var writes = stackalloc VkWriteDescriptorSet[3];
        writes[0] = new()
        {
            dstSet = set,
            dstBinding = 0,
            descriptorCount = 1,
            descriptorType = VkDescriptorType.StorageBuffer,
            pBufferInfo = &first
        };
        writes[1] = new()
        {
            dstSet = set,
            dstBinding = 1,
            descriptorCount = 1,
            descriptorType = VkDescriptorType.StorageBuffer,
            pBufferInfo = &second
        };
        writes[2] = new()
        {
            dstSet = set,
            dstBinding = 2,
            descriptorCount = 1,
            descriptorType = VkDescriptorType.UniformBuffer,
            pBufferInfo = &uniform
        };
        _gpu.Api.vkUpdateDescriptorSets(3, writes, 0, null);
        _gpu.Api.vkCmdBindDescriptorSets(surface.Commands, VkPipelineBindPoint.Graphics, pipeline.Layout, 0, set);
    }

    public void Dispose()
    {
        foreach (var pool in _pools)
        {
            _gpu.Api.vkDestroyDescriptorPool(pool);
        }
        Uploads.Dispose();
    }
}
