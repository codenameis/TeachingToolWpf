// File: tests/VisionAlgorithm.Tests/PatMaxStep1Tests.cs
using VisionAlgorithm.OpenSrc.PatMax;

namespace VisionAlgorithm.Tests;

public sealed class PatMaxStep1Tests
{
    private const int MicroWidth = 48;
    private const int MicroHeight = 32;
    private const int PaddedStride = 64;
    private const byte LowIntensity = 20;
    private const byte HighIntensity = 220;

    [Theory]
    [InlineData(16.25f, 0.0f, true)]
    [InlineData(20.75f, 0.0f, false)]
    [InlineData(18.50f, 90.0f, true)]
    public void SubPixelEdgeExtractor_PaddedMicroBuffer_RecoversSubPixelEdgeAndUnitNormals(
        float expectedBoundaryCenter,
        float orientationDegrees,
        bool includeOrthogonalCrossBar)
    {
        var buffer = new byte[MicroHeight * PaddedStride];
        RenderSubPixelEdgePattern(buffer, MicroWidth, MicroHeight, PaddedStride, expectedBoundaryCenter, orientationDegrees, includeOrthogonalCrossBar);

        using var dipoles = EdgeDipoleSoA.Rent(MicroWidth * MicroHeight);
        SubPixelEdgeExtractor.Extract(buffer, MicroWidth, MicroHeight, PaddedStride, threshold: 12.0f, dipoles);

        Assert.False(dipoles.IsEmpty);

        var primaryDipoles = Enumerable.Range(0, dipoles.Count)
            .Select(i => dipoles[i])
            .Where(d => orientationDegrees == 0.0f ? d.CosTheta > 0.95f : d.SinTheta > 0.95f)
            .ToArray();

        Assert.NotEmpty(primaryDipoles);

        var meanCoordinate = primaryDipoles.Average(d => orientationDegrees == 0.0f ? d.X : d.Y);
        Assert.InRange(MathF.Abs(meanCoordinate - expectedBoundaryCenter), 0.0f, 0.2f);

        foreach (var d in primaryDipoles)
        {
            var normLength = MathF.Sqrt((d.CosTheta * d.CosTheta) + (d.SinTheta * d.SinTheta));
            Assert.InRange(normLength, 0.999f, 1.001f);
        }

        var trainParams = PatMaxTrainParams.Default;
        var stability = StabilityMatrixAnalyzer.Evaluate(dipoles, in trainParams);
        Assert.False(stability.HasInsufficientFeatures);
        Assert.Equal(!includeOrthogonalCrossBar, stability.HasApertureWarning);
        Assert.Equal(includeOrthogonalCrossBar, stability.IsStable);
    }

    [Fact]
    public void GranularityPyramid_PaddedMicroBuffer_BuildsDownsampledOctavesWithoutStrideBleed()
    {
        var buffer = new byte[MicroHeight * PaddedStride];
        for (var y = 0; y < MicroHeight; y++)
        {
            var rowStart = y * PaddedStride;
            for (var x = 0; x < MicroWidth; x++)
            {
                buffer[rowStart + x] = HighIntensity;
            }

            for (var x = MicroWidth; x < PaddedStride; x++)
            {
                buffer[rowStart + x] = 0;
            }
        }

        using var pyramid = new GranularityPyramid();
        pyramid.Build(buffer, MicroWidth, MicroHeight, PaddedStride, fineGranularity: 1, coarseGranularity: 4);

        Assert.Equal(3, pyramid.LevelCount);
        var coarseLevel = pyramid.GetLevel(2);
        Assert.Equal(MicroWidth / 4, coarseLevel.Width);
        Assert.Equal(MicroHeight / 4, coarseLevel.Height);
        Assert.Equal(4.0f, coarseLevel.ScaleFactor);
        Assert.All(coarseLevel.Pixels.ToArray(), pixel => Assert.Equal(HighIntensity, pixel));
    }

    private static void RenderSubPixelEdgePattern(
        Span<byte> buffer,
        int width,
        int height,
        int stride,
        float boundaryCoord,
        float orientationDegrees,
        bool includeOrthogonalCrossBar)
    {
        for (var y = 0; y < height; y++)
        {
            var rowOffset = y * stride;
            for (var x = 0; x < width; x++)
            {
                var coord = orientationDegrees == 0.0f ? x + 0.5f : y + 0.5f;
                var delta = coord - boundaryCoord;
                var ramp = Math.Clamp(delta + 0.5f, 0.0f, 1.0f);
                var value = LowIntensity + ((HighIntensity - LowIntensity) * ramp);

                if (includeOrthogonalCrossBar)
                {
                    var orthoCoord = orientationDegrees == 0.0f ? y + 0.5f : x + 0.5f;
                    if (orthoCoord > height * 0.5f && coord > boundaryCoord + 4.0f)
                    {
                        value = LowIntensity;
                    }
                }

                buffer[rowOffset + x] = (byte)MathF.Round(value);
            }
        }
    }
}
