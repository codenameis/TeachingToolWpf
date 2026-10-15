using CpuMatching.Core;
using CpuMatching.DirectionalChamfer;

namespace CpuMatching.Tests;

public sealed class ChamferClutterTests
{
    [Fact]
    public void DenseUnrelatedEdgesMustNotImpersonateTemplate()
    {
        var pixels = new byte[64 * 48];
        Array.Fill(pixels, (byte)30);
        for (var y = 8; y < 40; y++)
        {
            for (var x = 10; x < 54; x++)
            {
                if (x < 18 || y > 31 || (x > 40 && y < 16))
                {
                    pixels[(y * 64) + x] = 220;
                }
            }
        }
        var template = new GrayImage(64, 48, pixels);
        var clutter = new byte[128 * 96];
        for (var y = 0; y < 96; y++)
        {
            for (var x = 0; x < 128; x++)
            {
                clutter[(y * 128) + x] = (byte)((((x / 8) + (y / 8)) % 2 == 0) ? 30 : 220);
            }
        }
        var matcher = new DirectionalChamferMatcher(template);
        var options = new SearchOptions { MinimumAngle = -15, MaximumAngle = 15, MinimumScore = .75 };
        Assert.Empty(matcher.Find(new GrayImage(128, 96, clutter), options).Matches);
        var positive = new byte[128 * 96];
        Array.Fill(positive, (byte)30);
        for (var y = 0; y < 48; y++)
        {
            pixels.AsSpan(y * 64, 64).CopyTo(positive.AsSpan(((y + 24) * 128) + 32));
        }
        Assert.Single(matcher.Find(new GrayImage(128, 96, positive), options).Matches);
    }
}
