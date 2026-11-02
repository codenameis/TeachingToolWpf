using CpuMatching.Core;
using CpuMatching.Line2D;
using CpuMatching.SubNcc;

namespace CpuMatching.Tests;

public sealed class SmallRoiTests
{
    [Theory]
    [InlineData(0, 125)]
    [InlineData(0, 132)]
    [InlineData(1, 125)]
    [InlineData(1, 132)]
    public void FindsTargetsAtAllImageCornersWithOddAndEvenRowLengths(int algorithm, int width)
    {
        const int TemplateWidth = 33;
        const int TemplateHeight = 29;
        const int Height = 103;
        var pixels = new byte[TemplateWidth * TemplateHeight];
        for (var y = 4; y < TemplateHeight - 4; y++)
        {
            for (var x = 4; x < TemplateWidth - 4; x++)
            {
                if (x < 9 || y > 20 || (x > 22 && y < 9))
                {
                    pixels[(y * TemplateWidth) + x] = 225;
                }
            }
        }
        var template = new GrayImage(TemplateWidth, TemplateHeight, pixels);
        ITemplateMatcher matcher = algorithm switch
        {
            0 => new Line2DMatcher(template),
            _ => new SubNccMatcher(template)
        };
        var scene = new byte[width * Height];
        (int X, int Y)[] corners = [(0, 0), (width - TemplateWidth, 0), (0, Height - TemplateHeight), (width - TemplateWidth, Height - TemplateHeight)];
        foreach (var corner in corners)
        {
            for (var y = 0; y < TemplateHeight; y++)
            {
                pixels.AsSpan(y * TemplateWidth, TemplateWidth).CopyTo(scene.AsSpan(((corner.Y + y) * width) + corner.X));
            }
        }
        var options = new SearchOptions { MinimumAngle = -30, MaximumAngle = 30, SuppressionRadius = 16 };
        var result = matcher.Find(new GrayImage(width, Height, scene), options);
        Assert.Equal(4, result.Matches.Count);
        foreach (var corner in corners)
        {
            Assert.Contains(result.Matches, match => Math.Abs(match.X - corner.X - 16) < 1 &&
                Math.Abs(match.Y - corner.Y - 14) < 1 && Math.Abs(match.AngleDegrees) < 1);
        }
        Assert.False(result.CandidatesTruncated);
        // 동일 모델로 완전히 다른 영상을 검사할 때 이전 픽셀/결과가 남으면 안 된다.
        Assert.Empty(matcher.Find(new GrayImage(width, Height, new byte[scene.Length]), options).Matches);
    }
}
