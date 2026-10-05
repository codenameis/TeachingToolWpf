using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using VisionCanvas.Core.Editing;
using VisionCanvas.Rendering;
using VisionGeometry;

namespace TeachingToolWpf.Imaging.Tests;

public class RoiGpuLayoutTests
{
    [Fact]
    public void DrawPayloadMatchesEveryHlslConstantOffsetAndContainsNoManagedReferences()
    {
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<RoiDrawConstants>());
        Assert.Equal(128, Marshal.SizeOf<RoiDrawConstants>());
        Assert.Equal(RoiDrawConstants.SizeInBytes, Unsafe.SizeOf<RoiDrawConstants>());
        Assert.Equal(32, RoiDrawConstants.RootConstantCount);

        var offsets = new (string Field, int ByteOffset)[]
        {
            (nameof(RoiDrawConstants.ClipScale), 0),
            (nameof(RoiDrawConstants.ImageScale), 8),
            (nameof(RoiDrawConstants.ReservedView), 12),
            (nameof(RoiDrawConstants.ScreenOrigin), 16),
            (nameof(RoiDrawConstants.ShadowPass), 24),
            (nameof(RoiDrawConstants.SummaryOpacity), 28),
            (nameof(RoiDrawConstants.ShadowColor), 32),
            (nameof(RoiDrawConstants.Rotation), 48),
            (nameof(RoiDrawConstants.ReservedRotation), 56),
            (nameof(RoiDrawConstants.DefaultColor), 64),
            (nameof(RoiDrawConstants.ChildColor), 80),
            (nameof(RoiDrawConstants.ParentColor), 96),
            (nameof(RoiDrawConstants.StrokeWidths), 112)
        };
        foreach (var (field, byteOffset) in offsets)
        {
            Assert.Equal(byteOffset, Marshal.OffsetOf<RoiDrawConstants>(field).ToInt32());
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NamedPayloadPreservesThePriorGpuBytesForEveryDrawPass(bool selected, bool shadow)
    {
        var palette = new RoiStylePalette(
            new(new(.1f, .2f, .3f, .4f), new(.2f, .3f, .4f, .5f), 2),
            new(new(.3f, .4f, .5f, .6f), new(.4f, .5f, .6f, .7f), 3),
            new(new(.5f, .6f, .7f, .8f), new(.6f, .7f, .8f, .9f), 4));
        var transform = new RigidTransform2D(new(1e12, 1e12), 37, new(17, -13));
        var blockOrigin = new PointD(1e12 + 200, 1e12 + 100);
        var state = DocumentState.Empty with
        {
            Viewport = new(.25, new(-2.5e11, -2.5e11))
        };
        var scene = new RenderScene(state, null, 0, 800, 600, 1.5, new(), Styles: palette);
        var named = new RoiDrawConstants(scene, blockOrigin, transform, selected, shadow ? RoiDrawPass.Shadow : RoiDrawPass.Stroke);

        // 이전 float[32] 계약을 독립적인 기준으로 보존한다. 예약 슬롯도 정확히 0이어야 한다.
        var screenOrigin = state.Viewport.ImageToScreen(transform.Apply(blockOrigin));
        var expected = new float[32]
        {
            (float)(2 / scene.Width), (float)(-2 / scene.Height), (float)state.Viewport.Scale, 0,
            (float)screenOrigin.X, (float)screenOrigin.Y, shadow ? 1 : 0, selected ? 1 : .5f,
            0, 0, 0, .22f,
            (float)transform.Cosine, (float)transform.Sine, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0
        };
        for (var role = 0;
role < 3;
role++)
        {
            var style = palette.For((RoiRole)role);
            var color = style.StrokeColor(selected);
            var offset = 16 + (role * 4);
            expected[offset] = color.X;
            expected[offset + 1] = color.Y;
            expected[offset + 2] = color.Z;
            expected[offset + 3] = color.W;
            expected[28 + role] = style.StrokeThickness(selected) + (shadow ? selected ? 1.5f : 1 : 0);
        }

        Assert.Equal(MemoryMarshal.AsBytes(expected.AsSpan()).ToArray(), MemoryMarshal.AsBytes(new[] { named }.AsSpan()).ToArray());
    }

    [Fact]
    public void InstanceStrideAndRoleStridePreserveTheTwoGpuViews()
    {
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<RoiInstance>());
        Assert.Equal(32, Marshal.SizeOf<RoiInstance>());
        Assert.Equal(RoiInstance.SizeInBytes, Unsafe.SizeOf<RoiInstance>());
        Assert.Equal(sizeof(uint), RoiInstance.RoleSizeInBytes);
        var instance = new RoiInstance(new(1, 2), new(3, 4), new(5, 6), 7, 8);
        var bytes = MemoryMarshal.AsBytes(new[] { instance }.AsSpan());
        Assert.Equal(new float[] { 1, 2, 3, 4, 5, 6 }, MemoryMarshal.Cast<byte, float>(bytes[..24]).ToArray());
        Assert.Equal(new uint[] { 7, 8 }, MemoryMarshal.Cast<byte, uint>(bytes[24..]).ToArray());
    }
}
