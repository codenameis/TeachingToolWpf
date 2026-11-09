using System.Numerics;
using VisionAlgorithm.OpenSrc.Calibration;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.Metrology;
using VisionAlgorithm.OpenSrc.Synthetic8kTestGen;

namespace VisionAlgorithm.Tests;

public sealed class InspectionSuite8kTests
{
    [Fact]
    public void SimdAffineWarper_WithIdentityPoseAtPixelCenter_MatchesSourcePixelsExactlyWithZeroBlur()
    {
        const int size = 64;
        byte[] src = new byte[size * size];
        for (int i = 0; i < src.Length; i++)
        {
            src[i] = (byte)((i * 73) & 0xFF);
        }

        float[] dst = new float[size * size];
        FixturePose2D identityPose = new(centerX: 32.0f, centerY: 32.0f, angleRad: 0f, scale: 1.0f);

        SimdAffineWarper.WarpRectifiedRoi(
            src,
            sourceWidth: size,
            sourceHeight: size,
            sourceStride: size,
            fixturePose: identityPose,
            roiWidth: size,
            roiHeight: size,
            destination: dst);

        for (int i = 0; i < src.Length; i++)
        {
            Assert.Equal(src[i], dst[i], precision: 5);
        }
    }

    [Fact]
    public void InspectionSuite8kTestRunner_ExecutesAllSevenAlgorithmsOnProcedural8kSceneWithinSubpixelGroundTruthBounds()
    {
        InspectionSuite8kVerificationReport report = InspectionSuite8kTestRunner.RunAllVerifications();

        // 1. Subpixel Metrology: 1D Caliper, RANSAC + Tukey IRLS Line, Taubin Circle
        Assert.True(report.CaliperWidthErrorPixels < 0.08f, $"Expected caliper width error < 0.08 px, got {report.CaliperWidthErrorPixels:F4} px.");
        Assert.True(report.LineAngleErrorDegrees < 0.08f, $"Expected RANSAC+IRLS line angle error < 0.08 deg, got {report.LineAngleErrorDegrees:F4} deg.");
        Assert.True(report.CircleRadiusErrorPixels < 0.08f, $"Expected Taubin circle radius error < 0.08 px, got {report.CircleRadiusErrorPixels:F4} px.");
        Assert.True(report.CircleCenterErrorPixels < 0.08f, $"Expected Taubin circle center error < 0.08 px, got {report.CircleCenterErrorPixels:F4} px.");

        // 2. Statistical Golden Variation Model
        Assert.Equal(0, report.GoldenNormalFalseDefectPixels);
        Assert.True(report.GoldenDefectiveDetectedPixels >= 40, $"Expected >= 40 defective pixels on defective die, got {report.GoldenDefectiveDetectedPixels}.");

        // 3. RLE Blob & Interval Morphology
        Assert.Equal(1L, report.RleCleanedBlobCount);
        Assert.True(report.RleBlobCentroidErrorPixels < 0.05f, $"Expected RLE blob centroid error < 0.05 px, got {report.RleBlobCentroidErrorPixels:F4} px.");

        // 4. Steger Hessian Curvilinear Ridge & Bead Continuity
        Assert.True(report.StegerCenterlineRmsErrorPixels < 0.02f, $"Expected Steger centerline RMS error < 0.02 px, got {report.StegerCenterlineRmsErrorPixels:F4} px.");
        Assert.True(report.ContinuousBeadPassed);
        Assert.True(report.GappedBeadDetectedGapCount >= 2, $"Expected >= 2 gap stations on broken bead, got {report.GappedBeadDetectedGapCount}.");

        // 5. Edge Few-Shot Classifier (SimpleShot + Ledoit-Wolf Mahalanobis + OOD Rejection)
        Assert.True(report.FewShotClassifiedKnownClassesCorrectly);
        Assert.True(report.FewShotRejectedOodAnomaly);

        // 6. PaDiM Patch Anomaly Heatmap Detector
        Assert.True(report.PadimAcceptedNormalDie);
        Assert.True(report.PadimDetectedDefectiveDie);
        Assert.True(report.PadimLocalizationErrorPixels <= 10.0f, $"Expected PaDiM peak localization error <= 10 px, got {report.PadimLocalizationErrorPixels:F2} px.");

        // 7. Hartley 9-Point Homography & Brown-Conrady Lens Distortion
        Assert.True(report.HomographyRmsErrorMm < 1e-4, $"Expected homography RMS error < 1e-4 mm, got {report.HomographyRmsErrorMm:E3} mm.");
        Assert.True(report.LensUndistortRoundTripErrorPixels < 1e-3f, $"Expected Brown-Conrady round-trip error < 1e-3 px, got {report.LensUndistortRoundTripErrorPixels:E3} px.");
    }

    [Fact]
    public void SubpixelCaliper1D_TryMeasureSingleEdge_IgnoresUncomputedBoundaryPaddingAtKernelRadius()
    {
        const int width = 64;
        const int height = 32;
        byte[] image = new byte[width * height];

        // Create a true edge at x = 32, plus a high-contrast step right at the scan start boundary (x = 12..17)
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (x >= 32)
                {
                    image[(y * width) + x] = 220;
                }
                else if (x >= 16)
                {
                    image[(y * width) + x] = 30;
                }
                else
                {
                    image[(y * width) + x] = 250;
                }
            }
        }

        using InspectionWorkspace workspace = new();
        CaliperMeasurementSpec spec = new(gaussianSigma: 1.5f, minimumContrast: 10f, projectionStripCount: 5);
        FixturePose2D pose = new(centerX: 32f, centerY: 16f, angleRad: 0f, scale: 1f);

        bool found = SubpixelCaliper1D.TryMeasureSingleEdge(
            image,
            width,
            height,
            width,
            pose,
            scanLengthPixels: 40f,
            projectionWidthPixels: 5f,
            desiredPolarity: CaliperEdgePolarity.DarkToLight,
            spec: spec,
            workspace: workspace,
            out CaliperEdgeHit hit);

        Assert.True(found);
        Assert.True(MathF.Abs(hit.WorldPoint.X - 31.5f) < 0.6f, $"Expected edge near x=31.5, got {hit.WorldPoint.X:F3}");
    }

    [Fact]
    public void RobustMestimatorAndRansacLineFitter_PreserveSubpixelInliersWhenOverHalfPointsAreExact()
    {
        Span<float> evenResiduals = [0f, 0f, 0.2f, 0.4f];
        float sigma = RobustMestimator.EstimateMadSigmaInPlace(evenResiduals, minimumFloor: 1e-3f);
        Assert.Equal(0.1f * RobustMestimator.MadToGaussianSigmaScale, sigma, precision: 5);

        using InspectionWorkspace workspace = new();
        Span<Vector2> points =
        [
            new Vector2(0f, 10.00f),
            new Vector2(10f, 10.00f),
            new Vector2(20f, 10.00f),
            new Vector2(30f, 10.00f),
            new Vector2(40f, 10.00f),
            new Vector2(50f, 10.08f),
            new Vector2(60f, 9.92f),
            new Vector2(70f, 10.06f)
        ];

        FittedLine2D line = RansacIrlsLineFitter.FitLine(points, inlierThresholdPixels: 0.5f, workspace);
        Assert.True(line.IsValid);
        Assert.Equal(8, line.InlierCount);
    }

    [Fact]
    public void SubpixelCaliper1D_MeasureEdgePairWidth_SelectsOptimalOrderedPairEvenWhenStrongerEdgeFollowsSecondEdge()
    {
        const int width = 96;
        const int height = 24;
        byte[] image = new byte[width * height];

        // Bead: DarkToLight at x = 24 (30 -> 170, delta = +140), LightToDark at x = 44 (170 -> 20, delta = -150).
        // Stronger reflection DarkToLight at x = 68 (20 -> 250, delta = +230) with no subsequent LightToDark edge.
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte val = x switch
                {
                    < 24 => 30,
                    < 44 => 170,
                    < 68 => 20,
                    _ => 250
                };
                image[(y * width) + x] = val;
            }
        }

        using InspectionWorkspace workspace = new();
        ReadOnlyImageView2D<byte> view = new(image, width, height, width);
        FixturePose2D pose = new(centerX: 48f, centerY: 12f, angleRad: 0f, scale: 1f);
        CaliperMeasurementSpec spec = new(gaussianSigma: 1.1f, minimumContrast: 12f, projectionStripCount: 5);

        CaliperPairResult pair = SubpixelCaliper1D.MeasureEdgePairWidth(
            view,
            pose,
            scanLengthPixels: 80f,
            projectionWidthPixels: 5f,
            firstPolarity: CaliperEdgePolarity.DarkToLight,
            secondPolarity: CaliperEdgePolarity.LightToDark,
            spec: spec,
            workspace: workspace);

        Assert.True(pair.IsFound);
        Assert.True(MathF.Abs(pair.SubpixelWidth - 20.0f) < 0.15f, $"Expected bead width ~20.0 px, got {pair.SubpixelWidth:F3} px.");
    }

    [Fact]
    public void TaubinCircleFitter_FitCircle_Reconstructs120And180DegreePartialArcsWithSubHundredthPixelAccuracy()
    {
        Vector2 trueCenter = new(314.25f, 271.75f);
        const float trueRadius = 85.5f;
        const int sampleCount = 36;

        foreach (float arcDegrees in new[] { 120f, 180f })
        {
            Vector2[] points = new Vector2[sampleCount];
            float arcRad = arcDegrees * (MathF.PI / 180f);
            float startAngle = -0.35f;

            for (int i = 0; i < sampleCount; i++)
            {
                float theta = startAngle + (arcRad * i / (sampleCount - 1));
                (float sinT, float cosT) = MathF.SinCos(theta);
                points[i] = new Vector2(
                    trueCenter.X + (trueRadius * cosT),
                    trueCenter.Y + (trueRadius * sinT));
            }

            FittedCircle2D fitted = TaubinCircleFitter.FitCircle(points);

            Assert.True(fitted.IsValid);
            float centerError = Vector2.Distance(fitted.Center, trueCenter);
            float radiusError = MathF.Abs(fitted.Radius - trueRadius);
            Assert.True(centerError < 0.01f, $"[{arcDegrees} deg arc] Expected center error < 0.01 px, got {centerError:E4} px.");
            Assert.True(radiusError < 0.01f, $"[{arcDegrees} deg arc] Expected radius error < 0.01 px, got {radiusError:E4} px.");
        }
    }
}
