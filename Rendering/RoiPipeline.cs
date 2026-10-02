using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using TeachingToolWpf.Core.Geometry;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.D3DCompiler;
using Vortice.DXGI;

namespace TeachingToolWpf.Rendering;

/// <summary>Retained geometry with independently updated role slots. Palette/navigation change draw constants.</summary>
internal sealed class RoiPipeline : IDisposable
{
    private sealed class KeyComparer : IEqualityComparer<(object Identity, int Level)>
    {
        public bool Equals((object Identity, int Level) a, (object Identity, int Level) b) => ReferenceEquals(a.Identity, b.Identity) && a.Level == b.Level;
        public int GetHashCode((object Identity, int Level) key) => HashCode.Combine(RuntimeHelpers.GetHashCode(key.Identity), key.Level);
    }
    private sealed record Block(ID3D12Resource Buffer, PointD Origin, int Count, int SourceCount, long AllocationBytes, RoiRoleCache Roles) : IDisposable
    {
        public long LastUsed { get; set; }
        public ulong RoleOffset => (ulong)Count * 32;
        public long CacheCost => AllocationBytes + (long)SourceCount * 256 + (long)Count * 4;
        public void Dispose() => Buffer.Dispose();
    }
    private readonly Dx12Device gpu;
    private readonly ID3D12RootSignature root;
    private readonly ID3D12PipelineState pipeline;
    private readonly FrameUploadArena[] uploads;
    private readonly Dictionary<(object Identity, int Level), Block> blocks = new(new KeyComparer());
    private long frame, bytes;
    public long UploadedBytes { get; private set; }
    public long InstanceCount { get; private set; }
    public long CacheBytes => bytes;
    public RoiPipeline(Dx12Device gpu)
    {
        this.gpu = gpu; uploads = [new(gpu), new(gpu)];
        root = gpu.Device.CreateRootSignature(new RootSignatureDescription1(RootSignatureFlags.None,
            [new(RootParameterType.ShaderResourceView, new RootDescriptor1(0, 0), ShaderVisibility.Vertex),
             new(new RootConstants(0, 0, 32), ShaderVisibility.All),
             new(RootParameterType.ShaderResourceView, new RootDescriptor1(1, 0), ShaderVisibility.Vertex)]));
        var shader = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "Roi.hlsl"));
        pipeline = gpu.Device.CreateGraphicsPipelineState<ID3D12PipelineState>(new GraphicsPipelineStateDescription
        {
            RootSignature = root,
            VertexShader = Compiler.Compile(shader, "VSMain", "Roi.hlsl", "vs_5_1"),
            PixelShader = Compiler.Compile(shader, "PSMain", "Roi.hlsl", "ps_5_1"),
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, RasterizerState = RasterizerDescription.CullNone,
            BlendState = BlendDescription.NonPremultiplied, DepthStencilState = DepthStencilDescription.None,
            RenderTargetFormats = [Format.B8G8R8A8_UNorm], SampleDescription = new(1, 0)
        });
    }
    public void Draw(RenderScene scene, Dx12Surface surface)
    {
        frame++; UploadedBytes = InstanceCount = 0; uploads[surface.FrameIndex].Reset();
        var view = scene.Document.Viewport;
        var a = view.ScreenToImage(new(-8, -8)); var b = view.ScreenToImage(new(scene.Width + 8, scene.Height + 8));
        var visible = new BoundsD(a.X, a.Y, b.X, b.Y);
        var level = (int)Math.Floor(Math.Log2(6 / view.Scale));
        surface.Commands.SetGraphicsRootSignature(root); surface.Commands.SetPipelineState(pipeline);
        surface.Commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        if (scene.Transform is { } preview)
        {
            DrawIndex(preview.Selection.Stationary, false, default);
            if (preview.Selection.Affected.Count != scene.Document.Selection.Count) DrawIndex(preview.Selection.Moving, false, preview.Transform);
            DrawIndex(scene.Document.SelectedSpatial, true, preview.Transform);
        }
        else
        {
            if (scene.Document.Selection.Count != scene.Document.Rois.Count) DrawIndex(scene.Document.Snapshot.Spatial, false, default);
            if (scene.Document.Selection.Count > 0) DrawIndex(scene.Document.SelectedSpatial, true, default);
        }
        // Keep recent geometry for pan/zoom/undo, but bound retired and inactive cache growth.
        if (bytes > 128L * 1024 * 1024)
            foreach (var pair in blocks.Where(p => p.Value.LastUsed != frame).OrderBy(p => p.Value.LastUsed).ToArray())
            {
                if (bytes <= 128L * 1024 * 1024) break;
                blocks.Remove(pair.Key); bytes -= pair.Value.CacheCost; surface.Retire(pair.Value);
            }
        void DrawIndex(RoiSpatialIndex index, bool selected, RigidTransform2D transform)
        {
            var query = transform.InverseTransformBounds(visible);
            foreach (var region in index.Blocks(query))
            {
                var key = (region.Identity, level);
                if (!blocks.TryGetValue(key, out var block))
                {
                    var instances = RoiInstance.Pack(region, Math.Pow(2, level));
                    var roles = new RoiRoleCache(); roles.Refresh(region, Math.Pow(2, level), scene.Document, instances.Length);
                    block = Upload(region, instances, roles, surface); blocks.Add(key, block); bytes += block.CacheCost;
                }
                else if (block.Roles.Refresh(region, Math.Pow(2, level), scene.Document, block.Count)) UploadRoles(block, surface);
                block.LastUsed = frame; InstanceCount += block.Count;
                DrawBlock(block, scene, surface, true, selected, transform);
                DrawBlock(block, scene, surface, false, selected, transform);
            }
        }
    }
    private unsafe Block Upload(RoiSpatialBlock region, RoiInstance[] instances, RoiRoleCache roles, Dx12Surface surface)
    {
        var size = checked((ulong)instances.Length * 32);
        var roleSize = checked((ulong)roles.Values.Length * 4);
        var description = ResourceDescription.Buffer(Math.Max(size + roleSize, 32));
        var allocation = gpu.Device.GetResourceAllocationInfo(0, description).SizeInBytes;
        var buffer = gpu.Device.CreateCommittedResource(HeapType.Default, description, ResourceStates.Common);
        try
        {
            surface.Commands.ResourceBarrierTransition(buffer, ResourceStates.Common, ResourceStates.CopyDest);
            if (size > 0)
            {
                var upload = uploads[surface.FrameIndex].Allocate(size);
                instances.AsSpan().CopyTo(new Span<RoiInstance>((void*)upload.Pointer, instances.Length));
                surface.Commands.CopyBufferRegion(buffer, 0, upload.Buffer, upload.Offset, size);
                var roleUpload = uploads[surface.FrameIndex].Allocate(roleSize);
                roles.Values.AsSpan().CopyTo(new Span<uint>((void*)roleUpload.Pointer, roles.Values.Length));
                surface.Commands.CopyBufferRegion(buffer, size, roleUpload.Buffer, roleUpload.Offset, roleSize);
            }
            surface.Commands.ResourceBarrierTransition(buffer, ResourceStates.CopyDest, ResourceStates.NonPixelShaderResource);
            UploadedBytes += (long)(size + roleSize);
            return new(buffer, region.Bounds.Center, instances.Length, region.Count, checked((long)allocation), roles);
        }
        catch { buffer.Dispose(); throw; }
    }
    private unsafe void UploadRoles(Block block, Dx12Surface surface)
    {
        var size = checked((ulong)block.Roles.Values.Length * 4);
        if (size == 0) return;
        var upload = uploads[surface.FrameIndex].Allocate(size);
        block.Roles.Values.AsSpan().CopyTo(new Span<uint>((void*)upload.Pointer, block.Roles.Values.Length));
        surface.Commands.ResourceBarrierTransition(block.Buffer, ResourceStates.NonPixelShaderResource, ResourceStates.CopyDest);
        surface.Commands.CopyBufferRegion(block.Buffer, block.RoleOffset, upload.Buffer, upload.Offset, size);
        surface.Commands.ResourceBarrierTransition(block.Buffer, ResourceStates.CopyDest, ResourceStates.NonPixelShaderResource);
        UploadedBytes += (long)size;
    }
    private unsafe void DrawBlock(Block block, RenderScene scene, Dx12Surface surface, bool shadow, bool selected, RigidTransform2D transform)
    {
        // Keep the large image origin in double. Only block-relative vectors reach the float shader.
        var origin = scene.Document.Viewport.ImageToScreen(transform.Apply(block.Origin));
        Span<float> constants = stackalloc float[32];
        constants.Clear();
        constants[0] = (float)(2 / scene.Width); constants[1] = (float)(-2 / scene.Height); constants[2] = (float)scene.Document.Viewport.Scale;
        constants[4] = (float)origin.X; constants[5] = (float)origin.Y; constants[6] = shadow ? 1 : 0; constants[7] = selected ? 1 : .5f;
        constants[11] = .22f; constants[12] = (float)transform.Cosine; constants[13] = (float)transform.Sine;
        WriteRoleStyles(constants[16..], scene.RoiStyles, selected, shadow);
        fixed (float* pointer = constants) surface.Commands.SetGraphicsRoot32BitConstants(1, 32, pointer, 0);
        surface.Commands.SetGraphicsRootShaderResourceView(0, block.Buffer.GPUVirtualAddress);
        surface.Commands.SetGraphicsRootShaderResourceView(2, block.Buffer.GPUVirtualAddress + block.RoleOffset);
        surface.Commands.DrawInstanced(24, (uint)block.Count, 0, 0);
    }
    internal static void WriteRoleStyles(Span<float> constants, RoiStylePalette palette, bool selected, bool shadow)
    {
        for (var role = 0; role < 3; role++)
        {
            var style = palette.For((RoiRole)role); var color = style.StrokeColor(selected); var offset = role * 4;
            constants[offset] = color.X; constants[offset + 1] = color.Y; constants[offset + 2] = color.Z; constants[offset + 3] = color.W;
            constants[12 + role] = style.StrokeThickness(selected) + (shadow ? selected ? 1.5f : 1 : 0);
        }
        constants[15] = 0;
    }
    public void Dispose()
    {
        foreach (var block in blocks.Values) block.Dispose();
        foreach (var upload in uploads) upload.Dispose();
        pipeline.Dispose(); root.Dispose();
    }
}
