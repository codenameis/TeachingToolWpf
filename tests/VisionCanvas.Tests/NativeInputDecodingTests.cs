using TeachingToolWpf.Controls;

namespace TeachingToolWpf.Imaging.Tests;

public class NativeInputDecodingTests
{
    [Theory]
    [InlineData(0x00000000L, 0, 0)]
    [InlineData(0xFFFEFFFFL, -1, -2)]
    [InlineData(0x7FFF8000L, -32768, 32767)]
    [InlineData(0x80007FFFL, 32767, -32768)]
    [InlineData(0x7654321080007FFFL, 32767, -32768)]
    [InlineData(-1L, -1, -1)]
    public void PointUsesSignedLowWordsAndIgnoresUpperPointerBits(long packed, int expectedX, int expectedY)
    {
        var point = NativeMethods.DecodePoint(unchecked((nint)packed));

        Assert.Equal(expectedX, point.X);
        Assert.Equal(expectedY, point.Y);
    }

    [Theory]
    [InlineData(0x00000000L, 0)]
    [InlineData(0x003C0000L, 0.5)]
    [InlineData(0xFFC40000L, -0.5)]
    [InlineData(0x00780000L, 1)]
    [InlineData(0xFF880000L, -1)]
    [InlineData(0x76543210003CFFFFL, 0.5)]
    [InlineData(0x76543210FFC4FFFFL, -0.5)]
    public void WheelUsesSignedDeltaAndPreservesPartialNotches(long packed, double expectedNotches)
    {
        var notches = NativeMethods.DecodeWheelNotches(unchecked((nint)packed));

        Assert.Equal(expectedNotches, notches);
    }
}
