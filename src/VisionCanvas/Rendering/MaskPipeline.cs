using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using VisionGeometry;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace VisionCanvas.Rendering;

/// <summary>단일 선택 ROI의 마스크 합집합을 그린다. 원본 영상과 ROI 윤곽은 변경하지 않는다.</summary>
internal sealed class MaskPipeline : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct DrawConstants
    {
        public Vector2 Center;
        public Vector2 AxisX;
        public Vector2 AxisY;
        public uint MaskCount;
        public uint VertexCount;
        public uint IsCircle;
        public uint SelectedIndex;
        public float Dpi;
        public uint Padding;
    }

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
        if (!scene.ShowMasks || scene.Document.Selection.Count != 1)
        {
            return;
        }
        var roi = scene.Document.Rois[scene.Document.Selection.Single()];
        if (scene.Document.IsGroupingRoi(roi.Id))
        {
            return;
        }
        var draft = scene.Overlay.MaskDraft;
        var count = roi.Masks.Length + (draft is null ? 0 : 1);
        if (count == 0)
        {
            return;
        }
        var geometry = scene.Transform?.Geometry(roi) ?? roi.Geometry;
        var arena = _uploads[surface.FrameIndex];
        arena.Reset();
        // float4 경계 + float4 도형 종류. 프레임 fence가 완료된 영역만 재사용한다.
        var masks = arena.Allocate((ulong)count * 32);
        var maskData = new Span<Vector4>((void*)masks.Pointer, count * 2);
        for (var index = 0; index < count; index++)
        {
            var mask = index < roi.Masks.Length ? roi.Masks[index] : draft!;
            maskData[index * 2] = new((float)mask.Bounds.Left, (float)mask.Bounds.Top, (float)mask.Bounds.Right, (float)mask.Bounds.Bottom);
            maskData[(index * 2) + 1] = new(mask.Shape == MaskShape.Ellipse ? 1 : 0, 0, 0, 0);
        }
        var vertices = geometry.Shape.Vertices;
        var outline = arena.Allocate((ulong)vertices.Length * 8);
        var points = new Span<Vector2>((void*)outline.Pointer, vertices.Length);
        for (var index = 0; index < vertices.Length; index++)
        {
            points[index] = new((float)vertices[index].X, (float)vertices[index].Y);
        }
        var center = scene.Document.Viewport.ImageToScreen(geometry.Center);
        var scale = scene.Document.Viewport.Scale;
        var (sin, cos) = Math.SinCos(geometry.Angle * Math.PI / 180);
        var constants = new DrawConstants
        {
            Center = new((float)((center.X * 2 / scene.Width) - 1), (float)(1 - (center.Y * 2 / scene.Height))),
            AxisX = new((float)(cos * geometry.Width * scale / scene.Width), (float)(-sin * geometry.Width * scale / scene.Height)),
            AxisY = new((float)(-sin * geometry.Height * scale / scene.Width), (float)(-cos * geometry.Height * scale / scene.Height)),
            MaskCount = (uint)count,
            VertexCount = (uint)vertices.Length,
            IsCircle = geometry.Shape.Kind == RoiShapeKind.Circle ? 1u : 0u,
            SelectedIndex = draft is null ? unchecked((uint)scene.SelectedMaskIndex) : (uint)roi.Masks.Length,
            Dpi = (float)scene.Dpi
        };
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
