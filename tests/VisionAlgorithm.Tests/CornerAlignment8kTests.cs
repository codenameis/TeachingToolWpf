// File: tests/VisionAlgorithm.Tests/CornerAlignment8kTests.cs
using System;
using System.IO;
using System.Numerics;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.CornerAlignment.Domain;
using VisionAlgorithm.OpenSrc.CornerAlignment.Kinematics;
using VisionAlgorithm.OpenSrc.CornerAlignment.SyntheticCornerTestGen;
using Xunit;

namespace VisionAlgorithm.Tests;

public sealed class CornerAlignment8kTests
{
    [Fact]
    public void Test1_DefectiveVirtualCorner_20RandomSubpixelPoses_MeetsPositionAndAngleSpec()
    {
        var (maxPosErrorPx, maxAngleErrorDeg) =
            CornerAlignment8kTestSuite.VerifyTest1_DefectiveVirtualCornerPrecision20Poses();

        Assert.True(
            maxPosErrorPx <= CornerAlignment8kTestSuite.MaxAllowedSingleCornerPositionErrorPx,
            $"Max position error {maxPosErrorPx:F4} px exceeded {CornerAlignment8kTestSuite.MaxAllowedSingleCornerPositionErrorPx:F4} px");
        Assert.True(
            maxAngleErrorDeg <= CornerAlignment8kTestSuite.MaxAllowedSingleCornerAngleErrorDeg,
            $"Max angle error {maxAngleErrorDeg:F5}° exceeded {CornerAlignment8kTestSuite.MaxAllowedSingleCornerAngleErrorDeg:F5}°");
    }

    [Fact]
    public void Test2_8kLargePanel_TwoCornerBaselineAndFourCornerDiagonalExpansion_MeetsSpec()
    {
        var (baselineAngleErrorDeg, diagonalCenterErrorPx) =
            CornerAlignment8kTestSuite.VerifyTest2_8kLargePanelTwoAndFourCornerAlignment();

        Assert.True(
            baselineAngleErrorDeg <= CornerAlignment8kTestSuite.MaxAllowed8kTwoCornerBaselineAngleErrorDeg,
            $"8K 6000px baseline angle error {baselineAngleErrorDeg:F6}° exceeded {CornerAlignment8kTestSuite.MaxAllowed8kTwoCornerBaselineAngleErrorDeg:F6}°");
        Assert.True(
            diagonalCenterErrorPx <= CornerAlignment8kTestSuite.MaxAllowed8kFourCornerExpansionCenterErrorPx,
            $"8K 4-corner diagonal center error {diagonalCenterErrorPx:F4} px exceeded {CornerAlignment8kTestSuite.MaxAllowed8kFourCornerExpansionCenterErrorPx:F4} px");
    }

    [Fact]
    public void Test3_CenterOfRotationAndLeverArmOneShotAlignment_MeetsSpec()
    {
        var (corCenterErrorMm, oneShotResidualMm) =
            CornerAlignment8kTestSuite.VerifyTest3_CenterOfRotationAndLeverArmOneShotAlignment();

        Assert.True(
            corCenterErrorMm <= CornerAlignment8kTestSuite.MaxAllowedCorCalibrationErrorMm,
            $"Center of Rotation error {corCenterErrorMm:E4} mm exceeded {CornerAlignment8kTestSuite.MaxAllowedCorCalibrationErrorMm:E4} mm");
        Assert.True(
            oneShotResidualMm <= CornerAlignment8kTestSuite.MaxAllowedOneShotAlignmentResidualMm,
            $"1-Shot alignment residual {oneShotResidualMm:E4} mm exceeded {CornerAlignment8kTestSuite.MaxAllowedOneShotAlignmentResidualMm:E4} mm");
    }

    [Fact]
    public void Test4_UvwStageForwardAndInverseKinematics_RoundTripExact()
    {
        double roundTripErrMm = CornerAlignment8kTestSuite.VerifyTest4_UvwStageForwardInverseKinematics();
        Assert.True(
            roundTripErrMm <= CornerAlignment8kTestSuite.MaxAllowedUvwKinematicRoundTripErrorMm,
            $"UVW round-trip error {roundTripErrMm:E4} mm exceeded {CornerAlignment8kTestSuite.MaxAllowedUvwKinematicRoundTripErrorMm:E4} mm");
    }

    [Fact]
    public void NPointHomographyAndBmpPgmExport_RoundTripSuccessfully()
    {
        Span<Vector2> pixelPts = stackalloc Vector2[9];
        Span<Vector2> worldMmPts = stackalloc Vector2[9];

        int idx = 0;
        for (int r = -1; r <= 1; r++)
        {
            for (int c = -1; c <= 1; c++)
            {
                float wx = c * 15.0f;
                float wy = r * 15.0f;
                worldMmPts[idx] = new Vector2(wx, wy);
                pixelPts[idx] = new Vector2(512.0f + (wx * 20.0f) - (wy * 0.5f), 512.0f + (wx * 0.5f) + (wy * 20.0f));
                idx++;
            }
        }

        NPointHomographyMapper mapper = NPointHomographyMapper.Calibrate(pixelPts, worldMmPts);
        Assert.True(mapper.RmsReprojectionErrorMm < 1e-5);

        byte[] patch = new byte[128 * 128];
        Synthetic8kCornerRenderer.RenderCornerScene(
            patch,
            128,
            128,
            128,
            new Vector2(32.5f, 32.5f),
            0.05f,
            CornerDefectProfile.SevereIndustrialDefect);

        var view = new ReadOnlyImageView2D<byte>(patch, 128, 128, 128);
        string tempDir = Path.Combine(Path.GetTempPath(), $"CornerAlignTest_{Guid.NewGuid():N}");
        try
        {
            string pgmPath = Path.Combine(tempDir, "corner.pgm");
            string bmpPath = Path.Combine(tempDir, "corner.bmp");

            Synthetic8kCornerRenderer.SaveRoiAsPgm(view, pgmPath);
            Synthetic8kCornerRenderer.SaveRoiAsBmp(view, bmpPath);

            Assert.True(File.Exists(pgmPath) && new FileInfo(pgmPath).Length > 128 * 128);
            Assert.True(File.Exists(bmpPath) && new FileInfo(bmpPath).Length > 128 * 128);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
