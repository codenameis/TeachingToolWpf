using System.Numerics;
using VisionCanvas.Rendering;

namespace TeachingToolWpf.Imaging.Tests;

public class RoiStylesTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(.49f)]
    [InlineData(8.01f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void InvalidWidthCannotEnterAnImmutablePalette(float width)
        => Assert.Throws<ArgumentOutOfRangeException>(() => RoiStylePalette.Standard.Parent.WithThickness(width));

    [Fact]
    public void RoleWidthsAndFutureColorsRemainIndependent()
    {
        var standard = RoiStylePalette.Standard;
        var greenChild = new RoiStrokeStyle(new(0, 1, 0, 1), new(0, .6f, 0, 1), 3);
        var custom = new RoiStylePalette(standard.Default.WithThickness(2), greenChild, standard.Parent.WithThickness(4));
        Assert.Equal(2, custom.For(RoiRole.Default).StrokeThickness(false));
        Assert.Equal(3.5f, custom.For(RoiRole.Child).StrokeThickness(true));
        Assert.Equal(4, custom.For(RoiRole.Parent).ThicknessDip);
        Assert.Equal(new Vector4(0, 1, 0, 1), custom.Child.StrokeColor(false));
        Assert.Equal(standard.Parent.Color, custom.Parent.Color);
        Assert.Equal(1, standard.Default.ThicknessDip);
        Assert.NotEqual(standard.Default.Color, standard.Child.Color);
        Assert.NotEqual(standard.Default.Color, standard.Parent.Color);
        Assert.NotEqual(standard.Default.SelectedColor, standard.Default.MultiSelectedColor);
        Assert.Equal(standard.Default.MultiSelectedColor, standard.Default.WithThickness(3).MultiSelectedColor);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(-.1f)]
    [InlineData(1.1f)]
    [InlineData(float.PositiveInfinity)]
    public void NonNormalizedColorsAreRejected(float channel)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new RoiStrokeStyle(new(channel, 0, 0, 1), Vector4.One, 1));
}
