using System.IO;
using System.Numerics;
using VisionCanvas.Rendering.Abstractions;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace VisionCanvas.Rendering;

/// <summary>단일 선택 ROI의 마스크 합집합을 그린다. 원본 영상과 ROI 윤곽은 변경하지 않는다.</summary>
internal sealed class MaskPipeline : IDisposable
{
    private const uint ConstantCount = 12;
    private readonly ID3D12RootSignature _root;
    private readonly ID3D12PipelineState _pipeline;
    private readonly FrameUploadArena[] _uploads;

    public MaskPipeline(Dx12Device gpu)
    {
        _uploads = [new(gpu), new(gpu)];
        _root = gpu.Device.CreateRootSignature(new RootSignatureDescription1(RootSignatureFlags.None,
            [new(new RootConstants(0, 0, ConstantCount), ShaderVisibility.All),
             new(RootParameterType.ShaderResourceView, new RootDescriptor1(0, 0), ShaderVisibility.Pixel),
             new(RootParameterType.ShaderResourceView, new RootDescriptor1(1, 0), ShaderVisibility.Pixel)]));
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Shaders", "Mask.hlsl");
            _pipeline = gpu.Device.CreateGraphicsPipelineState<ID3D12PipelineState>(new GraphicsPipelineStateDescription
            {
                RootSignature = _root,
                VertexShader = ShaderCompiler.CompileFile(path, "VSMain", "vs_5_1"),
                PixelShader = ShaderCompiler.CompileFile(path, "PSMain", "ps_5_1"),
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                RasterizerState = RasterizerDescription.CullNone,
                BlendState = BlendDescription.NonPremultiplied,
                DepthStencilState = DepthStencilDescription.None,
                RenderTargetFormats = [Format.B8G8R8A8_UNorm],
                SampleDescription = new(1, 0)
            });
        }
        catch
        {
            _root.Dispose();
            throw;
        }
    }

    public unsafe void Draw(RenderScene scene, Dx12Surface surface)
    {
        if (MaskDrawConstants.TryResolveActiveTarget(scene) is not { } target)
        {
            return;
        }

        var arena = _uploads[surface.FrameIndex];
        arena.Reset();
        var masks = arena.Allocate((ulong)target.MaskCount * 32);
        var maskData = new Span<Vector4>((void*)masks.Pointer, target.MaskCount * 2);
        var outline = arena.Allocate((ulong)target.VertexCount * 8);
        var points = new Span<Vector2>((void*)outline.Pointer, target.VertexCount);
        var constants = MaskDrawConstants.Pack(scene, in target, maskData, points);

        var commands = surface.Commands;
        commands.SetGraphicsRootSignature(_root);
        commands.SetPipelineState(_pipeline);
        commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        commands.SetGraphicsRoot32BitConstants(0, ConstantCount, &constants, 0);
        commands.SetGraphicsRootShaderResourceView(1, masks.Buffer.GPUVirtualAddress + masks.Offset);
        commands.SetGraphicsRootShaderResourceView(2, outline.Buffer.GPUVirtualAddress + outline.Offset);
        commands.DrawInstanced(6, 1, 0, 0);
    }

    public void Dispose()
    {
        foreach (var arena in _uploads)
        {
            arena.Dispose();
        }
        _pipeline.Dispose();
        _root.Dispose();
    }
}
