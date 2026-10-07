using System.Runtime.InteropServices;
using VisionCanvas.Rendering;

namespace VisionCanvas.Tests;

public sealed class MaskGpuLayoutTests
{
    [Fact]
    public void ShaderConstantsMatchThreeSixteenByteRegisters()
    {
        Assert.Equal(48, Marshal.SizeOf<MaskPipeline.DrawConstants>());
        Assert.Equal(24, Marshal.OffsetOf<MaskPipeline.DrawConstants>(nameof(MaskPipeline.DrawConstants.MaskCount)).ToInt32());
        Assert.Equal(32, Marshal.OffsetOf<MaskPipeline.DrawConstants>(nameof(MaskPipeline.DrawConstants.IsCircle)).ToInt32());
        Assert.Equal(40, Marshal.OffsetOf<MaskPipeline.DrawConstants>(nameof(MaskPipeline.DrawConstants.Dpi)).ToInt32());
    }
}
