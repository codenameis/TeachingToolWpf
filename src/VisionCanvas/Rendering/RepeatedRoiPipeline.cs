using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using VisionCanvas.Core.Editing;
using VisionGeometry;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace VisionCanvas.Rendering;

/// <summary>반복 격자를 화면 픽셀당 계산한다. ROI 수만큼 instance를 생성하거나 업로드하지 않는다.</summary>
internal sealed class RepeatedRoiPipeline : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct DrawConstants
    {
        public Vector2 Center;
        public Vector2 AxisX;
        public Vector2 AxisY;
        public uint Columns;
        public uint Rows;
        public float PitchPixels;
        public float LinePixels;
        public uint SummaryStride;
        public uint OverrideCount;
        public Vector4 Color;
    }

    private const uint ConstantCount = 16;
    private readonly ID3D12RootSignature _root;
    private readonly ID3D12PipelineState _pipeline;
    private readonly FrameUploadArena[] _uploads;
    private readonly ConditionalWeakTable<object, uint[]> _ids = new();
    private readonly ConditionalWeakTable<RepeatedRoiPart, Dictionary<uint, uint[]>> _blocks = new();

    public RepeatedRoiPipeline(Dx12Device gpu)
    {
        _uploads = [new(gpu), new(gpu)];
        _root = gpu.Device.CreateRootSignature(new RootSignatureDescription1(RootSignatureFlags.None,
            [new(new RootConstants(0, 0, ConstantCount), ShaderVisibility.All),
             new(RootParameterType.ShaderResourceView, new RootDescriptor1(0, 0), ShaderVisibility.Pixel),
             new(RootParameterType.ShaderResourceView, new RootDescriptor1(1, 0), ShaderVisibility.Vertex)]));
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Shaders", "RepeatedRoi.hlsl");
            _pipeline = gpu.Device.CreateGraphicsPipelineState<ID3D12PipelineState>(new GraphicsPipelineStateDescription
            {
                RootSignature = _root,
                VertexShader = ShaderCompiler.CompileFile(path, "VSMain", "vs_5_1"),
                PixelShader = ShaderCompiler.CompileFile(path, gpu.IsSoftware ? "PSMainCpu" : "PSMain", "ps_5_1"),
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

    internal static uint SummaryStride(RepeatedRoiGrid grid, double scale)
    {
        if (grid.Pitch * scale >= 3)
        {
            return 1;
        }
        var stride = 1u;
        while (grid.Pitch * scale * stride < 6 && stride < Math.Max(grid.Columns, grid.Rows))
        {
            stride *= 2;
        }
        return stride;
    }

    internal static uint[] DrawingBlocks(RepeatedRoiPart part, uint stride)
    {
        var blocks = part.Blocks;
        IEnumerable<RepeatedRoiPart.CellBlock> drawing = blocks;
        if (stride > 1 && blocks.Length > 1)
        {
            // At summary zoom, thousands of subpixel stair steps would repeatedly shade the same
            // pixels. Merge occupied summary cells; exact membership stays in the document/hit test.
            var step = (int)stride;
            var rows = new SortedDictionary<int, List<RoiCellSet.Range>>();
            foreach (var block in blocks)
            {
                for (var row = block.Top / step; row <= block.Bottom / step; row++)
                {
                    if (!rows.TryGetValue(row, out var ranges))
                    {
                        rows.Add(row, ranges = []);
                    }
                    ranges.Add(new(block.Left / step, (block.Right / step) + 1));
                }
            }
            var left = blocks.Min(block => block.Left);
            var right = blocks.Max(block => block.Right);
            var top = blocks.Min(block => block.Top);
            var bottom = blocks.Max(block => block.Bottom);
            var summaries = new List<RepeatedRoiPart.CellBlock>();
            foreach (var (row, ranges) in rows)
            {
                foreach (var range in RoiCellSet.Create(ranges).Ranges)
                {
                    summaries.Add(new(Math.Max(left, range.Start * step), Math.Max(top, row * step),
                        Math.Min(right, (range.End * step) - 1), Math.Min(bottom, ((row + 1) * step) - 1)));
                }
            }
            if (summaries.Count < blocks.Length)
            {
                drawing = summaries;
            }
        }
        return drawing.SelectMany(block => new[] { (uint)block.Left, (uint)block.Top, (uint)(block.Right + 1), (uint)(block.Bottom + 1) }).ToArray();
    }

    public unsafe void Draw(RenderScene scene, Dx12Surface surface)
    {
        var arena = _uploads[surface.FrameIndex];
        arena.Reset();
        if (scene.RepeatedGrid is { } legacy)
        {
            DrawGrid(scene, surface, legacy, null, scene.RepeatedSelection is null ? scene.RoiStyles.Default.MultiSelectedColor : scene.RoiStyles.Default.Color, scene.RepeatedSelection is null);
        }

        if (scene.Document.RepeatedRois.IsEmpty)
        {
            return;
        }

        var selection = scene.Transform?.Selection ?? scene.Document.TransformSelection;
        foreach (var batch in selection.StationaryRepeated)
        {
            foreach (var part in batch.Parts)
            {
                DrawGrid(scene, surface, part.Grid, part, scene.RoiStyles.Default.Color, false);
            }
        }

        foreach (var batch in selection.Repeated)
        {
            foreach (var part in batch.Parts)
            {
                DrawGrid(scene, surface, part.Grid.Transform(scene.Transform?.Transform ?? default), part,
                    scene.Document.SelectionCount > 1 ? scene.RoiStyles.Default.MultiSelectedColor : scene.RoiStyles.Default.SelectedColor, true);
            }
        }
    }

    private unsafe void DrawGrid(RenderScene scene, Dx12Surface surface, RepeatedRoiGrid grid, RepeatedRoiPart? part, Vector4 color, bool selected)
    {
        // The vertex quads cover occupied blocks only. Partial selections no longer shade the entire
        // grid twice or binary-search 10,001 row intervals at every covered pixel on WARP.
        var overrideIds = part is null ? _ids.GetValue(grid.Overrides, _ => grid.Overrides.Keys.Order().Select(index => (uint)index).ToArray()) : [];
        var stride = SummaryStride(grid, scene.Document.Viewport.Scale);
        uint[] blocks;
        if (part is null)
        {
            blocks = [0u, 0u, (uint)grid.Columns, (uint)grid.Rows];
        }
        else
        {
            var levels = _blocks.GetValue(part, _ => new());
            if (!levels.TryGetValue(stride, out blocks!))
            {
                levels.Add(stride, blocks = DrawingBlocks(part, stride));
            }
        }
        if (blocks.Length == 0)
        {
            return;
        }

        var arena = _uploads[surface.FrameIndex];
        var buffer = arena.Allocate((ulong)Math.Max(1, overrideIds.Length) * sizeof(uint));
        overrideIds.CopyTo(new Span<uint>((void*)buffer.Pointer, overrideIds.Length));
        var regions = arena.Allocate((ulong)blocks.Length * sizeof(uint));
        blocks.CopyTo(new Span<uint>((void*)regions.Pointer, blocks.Length));
        var view = scene.Document.Viewport;
        var center = view.ImageToScreen(grid.Center);
        var (sin, cos) = Math.SinCos(grid.Angle * Math.PI / 180);
        var width = grid.Columns * grid.Pitch * view.Scale;
        var height = grid.Rows * grid.Pitch * view.Scale;
        var constants = new DrawConstants
        {
            Center = new((float)((center.X * 2 / scene.Width) - 1), (float)(1 - (center.Y * 2 / scene.Height))),
            AxisX = new((float)(cos * width / scene.Width), (float)(-sin * width / scene.Height)),
            AxisY = new((float)(-sin * height / scene.Width), (float)(-cos * height / scene.Height)),
            Columns = (uint)grid.Columns,
            Rows = (uint)grid.Rows,
            PitchPixels = (float)(grid.Pitch * view.Scale * scene.Dpi),
            LinePixels = scene.RoiStyles.Default.StrokeThickness(selected) * (float)scene.Dpi,
            SummaryStride = stride,
            OverrideCount = (uint)overrideIds.Length,
            Color = color
        };
        var commands = surface.Commands;
        commands.SetGraphicsRootSignature(_root);
        commands.SetPipelineState(_pipeline);
        commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        commands.SetGraphicsRoot32BitConstants(0, ConstantCount, &constants, 0);
        commands.SetGraphicsRootShaderResourceView(1, buffer.Buffer.GPUVirtualAddress + buffer.Offset);
        commands.SetGraphicsRootShaderResourceView(2, regions.Buffer.GPUVirtualAddress + regions.Offset);
        commands.DrawInstanced(6, (uint)(blocks.Length / 4), 0, 0);
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
