using System.Runtime.InteropServices;
using VisionCanvas.Rendering;
using VisionCanvas.Rendering.Abstractions;

namespace VisionCanvas.Tests;

public sealed class MaskGpuLayoutTests
{
    [Fact]
    public void ShaderConstantsMatchThreeSixteenByteRegisters()
    {
        Assert.Equal(48, Marshal.SizeOf<MaskDrawConstants>());
        Assert.Equal(24, Marshal.OffsetOf<MaskDrawConstants>(nameof(MaskDrawConstants.MaskCount)).ToInt32());
        Assert.Equal(32, Marshal.OffsetOf<MaskDrawConstants>(nameof(MaskDrawConstants.IsCircle)).ToInt32());
        Assert.Equal(40, Marshal.OffsetOf<MaskDrawConstants>(nameof(MaskDrawConstants.Dpi)).ToInt32());
    }
}
