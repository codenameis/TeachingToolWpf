using System.IO;
using System.Text;
using WebGPU;
using static WebGPU.WebGPU;

namespace VisionCanvas.Wgpu.Rendering;

internal sealed unsafe class WgpuPipeline : IDisposable
{
    private WGPURenderPipeline _pipeline;

    public WGPUBindGroupLayout SetLayout { get; private set; }

    public WgpuPipeline(WgpuDevice gpu, WgpuSurface surface, string shader, bool sprites)
    {
        WGPUShaderModule module = default;
        WGPUPipelineLayout layout = default;
        try
        {
            var source = Encoding.UTF8.GetBytes(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", shader)));
            fixed (byte* code = source)
            {
                var wgsl = new WGPUShaderSourceWGSL
                {
                    chain = new() { sType = WGPUSType.ShaderSourceWGSL },
                    code = new(code, source.Length)
                };
                var descriptor = new WGPUShaderModuleDescriptor { nextInChain = (WGPUChainedStruct*)&wgsl };
                module = wgpuDeviceCreateShaderModule(gpu.Handle, &descriptor);
            }
            gpu.CheckErrors();
            var entries = stackalloc WGPUBindGroupLayoutEntry[5];
            entries[0] = sprites ? new()
            {
                binding = 0,
                visibility = WGPUShaderStage.Fragment,
                texture = new()
                {
                    sampleType = WGPUTextureSampleType.Float,
                    viewDimension = WGPUTextureViewDimension._2D
                }
            } : Storage(0);
            entries[1] = Storage(1);
            entries[2] = new()
            {
                binding = 2,
                visibility = WGPUShaderStage.Vertex | WGPUShaderStage.Fragment,
                buffer = new()
                {
                    type = WGPUBufferBindingType.Uniform
                }
            };
            entries[3] = new()
            {
                binding = 3,
                visibility = WGPUShaderStage.Fragment,
                sampler = new()
                {
                    type = WGPUSamplerBindingType.Filtering
                }
            };
            entries[4] = new()
            {
                binding = 4,
                visibility = WGPUShaderStage.Fragment,
                sampler = new()
                {
                    type = WGPUSamplerBindingType.NonFiltering
                }
            };
            var bindLayout = new WGPUBindGroupLayoutDescriptor { entryCount = sprites ? 5u : 3u, entries = entries };
            SetLayout = wgpuDeviceCreateBindGroupLayout(gpu.Handle, &bindLayout);
            var setLayout = SetLayout;
            var layoutDescriptor = new WGPUPipelineLayoutDescriptor { bindGroupLayoutCount = 1, bindGroupLayouts = &setLayout };
            layout = wgpuDeviceCreatePipelineLayout(gpu.Handle, &layoutDescriptor);
            var attributes = stackalloc WGPUVertexAttribute[3]
            {
                new() { format = WGPUVertexFormat.Float32x2, offset = 0, shaderLocation = 0 },
                new() { format = WGPUVertexFormat.Float32x2, offset = 8, shaderLocation = 1 },
                new() { format = WGPUVertexFormat.Float32x4, offset = 16, shaderLocation = 2 }
            };
            var vertices = new WGPUVertexBufferLayout { arrayStride = 32, stepMode = WGPUVertexStepMode.Vertex, attributeCount = 3, attributes = attributes };
            var blend = new WGPUBlendState
            {
                color = new() { operation = WGPUBlendOperation.Add, srcFactor = WGPUBlendFactor.SrcAlpha, dstFactor = WGPUBlendFactor.OneMinusSrcAlpha },
                alpha = new() { operation = WGPUBlendOperation.Add, srcFactor = WGPUBlendFactor.One, dstFactor = WGPUBlendFactor.OneMinusSrcAlpha }
            };
            var target = new WGPUColorTargetState { format = surface.Format, blend = &blend, writeMask = WGPUColorWriteMask.All };
            fixed (byte* vertexName = "vs_main"u8, fragmentName = "fs_main"u8)
            {
                var fragment = new WGPUFragmentState { module = module, entryPoint = new(fragmentName, 7), targetCount = 1, targets = &target };
                var pipeline = new WGPURenderPipelineDescriptor
                {
                    layout = layout,
                    vertex = new() { module = module, entryPoint = new(vertexName, 7), bufferCount = sprites ? 1u : 0u, buffers = sprites ? &vertices : null },
                    primitive = new() { topology = WGPUPrimitiveTopology.TriangleList, frontFace = WGPUFrontFace.CCW, cullMode = WGPUCullMode.None },
                    multisample = new() { count = 1, mask = uint.MaxValue },
                    fragment = &fragment
                };
                _pipeline = wgpuDeviceCreateRenderPipeline(gpu.Handle, &pipeline);
            }
            gpu.CheckErrors();
        }
        catch
        {
            Dispose();
            throw;
        }
        finally
        {
            if (layout.IsNotNull)
            {
                wgpuPipelineLayoutRelease(layout);
            }
            if (module.IsNotNull)
            {
                wgpuShaderModuleRelease(module);
            }
        }
    }

    private static WGPUBindGroupLayoutEntry Storage(uint binding) => new()
    {
        binding = binding,
        visibility = WGPUShaderStage.Vertex | WGPUShaderStage.Fragment,
        buffer = new()
        {
            type = WGPUBufferBindingType.ReadOnlyStorage
        }
    };

    public void Bind(WgpuSurface surface) => wgpuRenderPassEncoderSetPipeline(surface.Pass, _pipeline);

    public void Dispose()
    {
        if (_pipeline.IsNotNull)
        {
            wgpuRenderPipelineRelease(_pipeline);
            _pipeline = default;
        }
        if (SetLayout.IsNotNull)
        {
            wgpuBindGroupLayoutRelease(SetLayout);
            SetLayout = default;
        }
    }
}
