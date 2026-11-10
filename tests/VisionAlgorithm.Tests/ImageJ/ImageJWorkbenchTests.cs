// File: tests/VisionAlgorithm.Tests/ImageJ/ImageJWorkbenchTests.cs
using System;
using System.Collections.Generic;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.ImageJ;
using Xunit;

namespace VisionAlgorithm.Tests.ImageJ;

public sealed class ImageJWorkbenchTests
{
    [Fact]
    public void ValueObjectSpecs_DefaultStruct_IsNotInitialized_And_ThrowsWhenApplied()
    {
        ImageJContrastSpec defaultContrast = default;
        ImageJThresholdSpec defaultThreshold = default;
        ImageJParticleFilterSpec defaultParticleFilter = default;

        Assert.False(defaultContrast.IsInitialized);
        Assert.False(defaultThreshold.IsInitialized);
        Assert.False(defaultParticleFilter.IsInitialized);

        var pixels = new byte[16 * 16];
        var dst = new byte[16 * 16];

        Assert.Throws<ArgumentException>(() =>
        {
            var view = new ReadOnlyImageView2D<byte>(pixels, 16, 16, 16);
            ImageJWorkbenchEngine.ApplyBrightnessContrastToRoi(view, dst, 16, 0, 0, 16, 16, defaultContrast);
        });

        Assert.Throws<ArgumentException>(() =>
        {
            using var engine = new ImageJWorkbenchEngine();
            var view = new ReadOnlyImageView2D<byte>(pixels, 16, 16, 16);
            _ = engine.MeasureRoi(view, 0, 0, 16, 16, 1, "Uninit", defaultThreshold);
        });
    }

    [Fact]
    public void ProbePixel_And_MeasureRoi_ComputeAccurateStatisticsAndCenterOfMass()
    {
        using var engine = new ImageJWorkbenchEngine();
        const int width = 32;
        const int height = 32;
        const int stride = 32;
        var buffer = new byte[height * stride];

        // Background = 20, bright square [8..15, 8..15] = 220
        buffer.AsSpan().Fill(20);
        for (int y = 8; y < 16; y++)
        {
            buffer.AsSpan((y * stride) + 8, 8).Fill(220);
        }

        var view = new ReadOnlyImageView2D<byte>(buffer, width, height, stride);

        ImageJPixelProbeResult insideProbe = engine.ProbePixel(view, 12, 12);
        Assert.True(insideProbe.IsInsideImage);
        Assert.Equal((byte)220, insideProbe.GrayValue);
        Assert.Equal(220.0f, insideProbe.Mean3x3, precision: 2);
        Assert.Equal(0.0f, insideProbe.SobelGradient, precision: 2);

        ImageJPixelProbeResult edgeProbe = engine.ProbePixel(view, 8, 12);
        Assert.True(edgeProbe.SobelGradient > 100.0f);

        ImageJThresholdSpec thresh = ImageJThresholdSpec.Create(
            ImageJAutoThresholdMethod.Manual,
            minThreshold: 100,
            maxThreshold: byte.MaxValue,
            isDarkBackground: true);

        ImageJMeasurementRecord record = engine.MeasureRoi(
            view,
            roiX: 0,
            roiY: 0,
            roiWidth: width,
            roiHeight: height,
            measurementNumber: 1,
            label: "TestSlice:ROI_1",
            thresholdSpec: thresh);

        Assert.Equal(width * height, record.AreaPixels);
        Assert.Equal((byte)20, record.MinGray);
        Assert.Equal((byte)220, record.MaxGray);
        // 64 bright pixels out of 1024 = 6.25%
        Assert.Equal(6.25f, record.ThresholdedAreaPercent, precision: 2);
        // Intensity-weighted Center of Mass should be pulled toward the [8..15, 8..15] bright square (center ~ 11.5)
        Assert.True(record.CenterOfMassX < record.CentroidX);
        Assert.True(record.CenterOfMassY < record.CentroidY);
    }

    [Fact]
    public void AnalyzeParticles_FiltersByAreaAndEdgeContact_And_MeasuresParticleMetrics()
    {
        using var engine = new ImageJWorkbenchEngine();
        const int width = 64;
        const int height = 64;
        const int stride = 64;
        var buffer = new byte[height * stride];
        buffer.AsSpan().Fill(15);

        // Particle 1: Interior 8x8 square at (20, 20) -> Area = 64 px
        for (int y = 20; y < 28; y++)
        {
            buffer.AsSpan((y * stride) + 20, 8).Fill(230);
        }

        // Particle 2: Small 2x2 noise dot at (45, 45) -> Area = 4 px
        for (int y = 45; y < 47; y++)
        {
            buffer.AsSpan((y * stride) + 45, 2).Fill(230);
        }

        // Particle 3: Border-touching 6x6 square at (0, 10) -> Area = 36 px
        for (int y = 10; y < 16; y++)
        {
            buffer.AsSpan(y * stride, 6).Fill(230);
        }

        var view = new ReadOnlyImageView2D<byte>(buffer, width, height, stride);
        var thresh = ImageJThresholdSpec.Create(ImageJAutoThresholdMethod.Manual, 120, 255, isDarkBackground: true);
        var filterExcludeEdges = ImageJParticleFilterSpec.Create(
            minAreaPixels: 10,
            maxAreaPixels: 500,
            minCircularity: 0.1f,
            maxCircularity: 1.0f,
            shouldExcludeEdgeParticles: true);

        IReadOnlyList<ImageJParticleRecord> particles = engine.AnalyzeParticles(
            view,
            0,
            0,
            width,
            height,
            thresh,
            filterExcludeEdges);

        Assert.Single(particles);
        Assert.Equal(64, particles[0].AreaPixels);
        Assert.Equal(24.0f, particles[0].CentroidX, precision: 1);
        Assert.Equal(24.0f, particles[0].CentroidY, precision: 1);
    }

    [Fact]
    public void ApplyFilters_And_RoiPixelEdits_ModifyTargetRegionCleanly()
    {
        const int width = 48;
        const int height = 48;
        const int stride = 48;
        var src = new byte[height * stride];
        var dst = new byte[height * stride];

        ImageJWorkbenchEngine.RenderBuiltInSample(src, width, height, stride, ImageJSampleKind.UnevenShadingAndSaltPepper);
        var view = new ReadOnlyImageView2D<byte>(src, width, height, stride);

        ImageJWorkbenchEngine.ApplyFilterToRoi(view, dst, stride, 4, 4, 40, 40, ImageJFilterKind.Median3x3);
        ImageJWorkbenchEngine.ApplyFilterToRoi(view, dst, stride, 4, 4, 40, 40, ImageJFilterKind.RollingBallTopHat);
        ImageJWorkbenchEngine.ApplyFilterToRoi(view, dst, stride, 4, 4, 40, 40, ImageJFilterKind.HistogramEqualize);

        ImageJWorkbenchEngine.FillRoi(dst, width, height, stride, 10, 10, 8, 8, fillValue: 180);
        Assert.Equal((byte)180, dst[(12 * stride) + 12]);

        ImageJWorkbenchEngine.ClearOutsideRoi(dst, width, height, stride, 10, 10, 8, 8, backgroundValue: 0);
        Assert.Equal((byte)0, dst[(2 * stride) + 2]);
        Assert.Equal((byte)180, dst[(12 * stride) + 12]);
    }
}
