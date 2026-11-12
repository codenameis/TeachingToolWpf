// File: src/VisionAlgorithm/OpenSrc/CornerAlignment/SyntheticCornerTestGen/CornerAlignment8kTestSuite.cs
using System;
using System.Numerics;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.CornerAlignment.Domain;
using VisionAlgorithm.OpenSrc.CornerAlignment.Kinematics;
using VisionAlgorithm.OpenSrc.CornerAlignment.LineAndCorner;

namespace VisionAlgorithm.OpenSrc.CornerAlignment.SyntheticCornerTestGen;

/// <summary>
/// 모따기·라운딩·칩핑·버 내성 검증(Test 1), 8K 대면적 기판 2-Corner 및 4-Corner Diagonal 정합 검증(Test 2),
/// 기구적 회전 중심(CoR) 캘리브레이션 및 레버암 보정 1-Shot 정합 검증(Test 3),
/// UVW 병렬 스테이지 순기구학-역기구학 교차 검증(Test 4)을 일괄 수행하는 End-to-End 자동 검증 스위트.
/// </summary>
public static class CornerAlignment8kTestSuite
{
    public const float MaxAllowedSingleCornerPositionErrorPx = 0.02f;
    public const float MaxAllowedSingleCornerAngleErrorDeg = 0.015f;
    public const float MaxAllowed8kTwoCornerBaselineAngleErrorDeg = 0.001f;
    public const float MaxAllowed8kFourCornerExpansionCenterErrorPx = 0.015f;
    public const double MaxAllowedCorCalibrationErrorMm = 0.001;
    public const double MaxAllowedOneShotAlignmentResidualMm = 0.0001;
    public const double MaxAllowedUvwKinematicRoundTripErrorMm = 1e-9;

    private const float DegreesToRadians = MathF.PI / 180.0f;
    private const float RadiansToDegrees = 180.0f / MathF.PI;

    /// <summary>
    /// 4대 자동 검증 항목을 모두 실행하고 정밀도 기준을 통과했는지 검증한다.
    /// 기준 미달 시 상세 오차 메시지와 함께 <see cref="InvalidOperationException"/>을 발생시킨다.
    /// </summary>
    public static (
        float MaxPositionErrorPx,
        float MaxAngleErrorDeg,
        float Baseline8kAngleErrorDeg,
        float DiagonalCenterErrorPx,
        double CorCenterErrorMm,
        double OneShotResidualMm,
        double UvwRoundTripErrorMm) RunAllVerifications()
    {
        (float maxPosErr, float maxAngErr) = VerifyTest1_DefectiveVirtualCornerPrecision20Poses();
        (float baselineAngErr, float diagCenterErr) = VerifyTest2_8kLargePanelTwoAndFourCornerAlignment();
        (double corErrMm, double oneShotResMm) = VerifyTest3_CenterOfRotationAndLeverArmOneShotAlignment();
        double uvwErrMm = VerifyTest4_UvwStageForwardInverseKinematics();

        return (maxPosErr, maxAngErr, baselineAngErr, diagCenterErr, corErrMm, oneShotResMm, uvwErrMm);
    }

    /// <summary>
    /// Test 1: 모따기 8px + 라운딩 6px + 측면 칩핑(3.5px) + 측면 버(4.0px) 동시 존재 시
    /// 20개 난수 서브픽셀 포즈(dx, dy in [-0.48, +0.48] px, dTheta in [-3°, +3°])에서
    /// 위치 오차 &lt;= 0.02 px, 각도 오차 &lt;= 0.015° 달성 여부를 검증한다.
    /// </summary>
    public static (float MaxPositionErrorPx, float MaxAngleErrorDeg) VerifyTest1_DefectiveVirtualCornerPrecision20Poses()
    {
        const int patchSize = 256;
        const int poseCount = 20;
        byte[] buffer = new byte[patchSize * patchSize];

        CornerDefectProfile defect = CornerDefectProfile.SevereIndustrialDefect;
        CornerDetectionOptions options = new(
            GaussianSigma: 1.25f,
            MinimumContrast: 18.0f,
            RansacInlierThresholdPixels: 0.45f,
            TukeyIterations: 5,
            OrthogonalityToleranceDeg: 3.0f,
            MinimumInlierRatio: 0.55f);

        float maxPosError = 0.0f;
        float maxAngleErrorDeg = 0.0f;

        for (int i = 0; i < poseCount; i++)
        {
            // 결정론적 준난수(Halton/LCG) 서브픽셀 오프셋 및 미세 회전 생성
            float fracX = (((i * 73) % 97) / 96.0f) - 0.5f;
            float fracY = (((i * 53) % 89) / 88.0f) - 0.5f;
            float fracAngle = (((i * 37) % 83) / 82.0f) - 0.5f;

            float subpixelDx = fracX * 0.96f; // [-0.48, +0.48] px
            float subpixelDy = fracY * 0.96f; // [-0.48, +0.48] px
            float trueAngleDeg = fracAngle * 6.0f; // [-3.0°, +3.0°]
            float trueAngleRad = trueAngleDeg * DegreesToRadians;

            Vector2 nominalApex = new(44.0f, 44.0f);
            Vector2 trueApex = new(nominalApex.X + subpixelDx, nominalApex.Y + subpixelDy);

            Synthetic8kCornerRenderer.RenderCornerScene(
                buffer,
                patchSize,
                patchSize,
                patchSize,
                trueApex,
                trueAngleRad,
                defect,
                deterministicSeed: (uint)(1000 + i));

            var view = new ReadOnlyImageView2D<byte>(buffer, patchSize, patchSize, patchSize);

            // 예상 코너 꼭짓점(nominalApex) 기준으로 모따기/라운딩 영역(ApexIgnoreMargin = 20px)을 건너뛰는 2-Rake 설정
            var leg1Region = new RakeSearchRegion(
                StartPoint: nominalApex,
                EndPoint: new Vector2(nominalApex.X + 190.0f, nominalApex.Y),
                SearchLength: 28.0f,
                ProjectionWidth: 9.0f,
                CaliperCount: 30,
                ApexIgnoreMargin: 20.0f,
                Polarity: CaliperPolarity.DarkToLight,
                ExplicitScanDirection: new Vector2(0.0f, 1.0f));

            var leg2Region = new RakeSearchRegion(
                StartPoint: nominalApex,
                EndPoint: new Vector2(nominalApex.X, nominalApex.Y + 190.0f),
                SearchLength: 28.0f,
                ProjectionWidth: 9.0f,
                CaliperCount: 30,
                ApexIgnoreMargin: 20.0f,
                Polarity: CaliperPolarity.DarkToLight,
                ExplicitScanDirection: new Vector2(1.0f, 0.0f));

            VirtualCornerResult result = VirtualCornerAligner.DetectVirtualCorner(view, leg1Region, leg2Region, options);
            if (!result.IsValid)
            {
                throw new InvalidOperationException($"Test 1 실패: 포즈 #{i}에서 가상 코너 검출에 실패했습니다.");
            }

            float posErr = Vector2.Distance(result.CornerPoint, trueApex);
            float angErrDeg = MathF.Abs(NormalizeAngleDiffRad(result.OrientationRad, trueAngleRad)) * RadiansToDegrees;

            if (posErr > maxPosError)
            {
                maxPosError = posErr;
            }

            if (angErrDeg > maxAngleErrorDeg)
            {
                maxAngleErrorDeg = angErrDeg;
            }
        }

        if (maxPosError > MaxAllowedSingleCornerPositionErrorPx)
        {
            throw new InvalidOperationException(
                $"Test 1 위치 정밀도 미달: 최대 오차 {maxPosError:F4} px > 허용치 {MaxAllowedSingleCornerPositionErrorPx:F4} px");
        }

        if (maxAngleErrorDeg > MaxAllowedSingleCornerAngleErrorDeg)
        {
            throw new InvalidOperationException(
                $"Test 1 각도 정밀도 미달: 최대 오차 {maxAngleErrorDeg:F5}° > 허용치 {MaxAllowedSingleCornerAngleErrorDeg:F5}°");
        }

        return (maxPosError, maxAngleErrorDeg);
    }

    /// <summary>
    /// Test 2: 8192×8192 대면적 기판(기저선 6000 px)의 코너 로컬 ROI를 추출하여
    /// 2-Corner 정합 시 각도 오차 &lt;= 0.001° 달성 및 제조 공차 +0.4 px 팽창 시 4-Corner Diagonal 정중앙 상쇄를 검증한다.
    /// </summary>
    public static (float BaselineAngleErrorDeg, float DiagonalCenterErrorPx) VerifyTest2_8kLargePanelTwoAndFourCornerAlignment()
    {
        const int winSize = 256;
        byte[] winBuffer = new byte[winSize * winSize];

        Vector2 truePanelCenter = new(4096.32f, 4095.74f);
        const float nominalPanelSpan = 6000.0f;
        const float manufacturingExpansionPx = 0.4f; // 가로·세로 +0.4 px 팽창 (양측 각 +0.2 px)
        float expandedSpan = nominalPanelSpan + manufacturingExpansionPx;
        float trueAngleDeg = 0.42f;
        float trueAngleRad = trueAngleDeg * DegreesToRadians;

        CornerDefectProfile panelDefect = new(
            ChamferLength: 6.0f,
            CornerRadius: 4.5f,
            ChippingDepth: 3.0f,
            BurrHeight: 3.5f,
            NoiseSigma: 0.8f);

        CornerDetectionOptions options = new(
            GaussianSigma: 1.25f,
            MinimumContrast: 18.0f,
            RansacInlierThresholdPixels: 0.45f,
            TukeyIterations: 5,
            OrthogonalityToleranceDeg: 2.5f,
            MinimumInlierRatio: 0.55f);

        // 4개 코너의 기준(Reference, 공칭 6000x6000, 각도 0) 좌표
        float halfNom = nominalPanelSpan * 0.5f;
        Vector2 nominalCenter = new(4096.0f, 4096.0f);
        Span<Vector2> referenceCorners =
        [
            new Vector2(nominalCenter.X - halfNom, nominalCenter.Y - halfNom), // Top-Left
            new Vector2(nominalCenter.X + halfNom, nominalCenter.Y - halfNom), // Top-Right
            new Vector2(nominalCenter.X + halfNom, nominalCenter.Y + halfNom), // Bottom-Right
            new Vector2(nominalCenter.X - halfNom, nominalCenter.Y + halfNom)  // Bottom-Left
        ];

        // 각 코너에서 안쪽을 향하는 두 변의 단위 방향 (Top-Left, Top-Right, Bottom-Right, Bottom-Left)
        ReadOnlySpan<(Vector2 Leg1Dir, Vector2 Scan1Dir, Vector2 Leg2Dir, Vector2 Scan2Dir)> cornerDirs =
        [
            (new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(1f, 0f)),
            (new Vector2(-1f, 0f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(-1f, 0f)),
            (new Vector2(-1f, 0f), new Vector2(0f, -1f), new Vector2(0f, -1f), new Vector2(-1f, 0f)),
            (new Vector2(1f, 0f), new Vector2(0f, -1f), new Vector2(0f, -1f), new Vector2(1f, 0f))
        ];

        Span<VirtualCornerResult> detectedCorners = stackalloc VirtualCornerResult[4];
        Span<Vector2> measuredCornerPoints = stackalloc Vector2[4];

        float cosA = MathF.Cos(trueAngleRad);
        float sinA = MathF.Sin(trueAngleRad);
        float halfExp = expandedSpan * 0.5f;
        ReadOnlySpan<(float Sx, float Sy)> signs = [(-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f)];

        for (int c = 0; c < 4; c++)
        {
            float lx = signs[c].Sx * halfExp;
            float ly = signs[c].Sy * halfExp;
            Vector2 approxWorldCorner = new(
                truePanelCenter.X + (lx * cosA) - (ly * sinA),
                truePanelCenter.Y + (lx * sinA) + (ly * cosA));

            var dirs = cornerDirs[c];
            int originX = (int)MathF.Floor(approxWorldCorner.X) - ((dirs.Leg1Dir.X > 0 ? 48 : winSize - 48));
            int originY = (int)MathF.Floor(approxWorldCorner.Y) - ((dirs.Leg2Dir.Y > 0 ? 48 : winSize - 48));

            Synthetic8kCornerRenderer.Render8kPanelCornerWindow(
                winBuffer,
                winSize,
                winSize,
                winSize,
                originX,
                originY,
                truePanelCenter,
                expandedSpan,
                expandedSpan,
                trueAngleRad,
                panelDefect,
                seed: (uint)(500 + c));

            var view = new ReadOnlyImageView2D<byte>(winBuffer, winSize, winSize, winSize);
            Vector2 localApex = new(approxWorldCorner.X - originX, approxWorldCorner.Y - originY);

            var leg1 = new RakeSearchRegion(
                StartPoint: localApex,
                EndPoint: localApex + (dirs.Leg1Dir * 185.0f),
                SearchLength: 24.0f,
                ProjectionWidth: 9.0f,
                CaliperCount: 30,
                ApexIgnoreMargin: 20.0f,
                Polarity: CaliperPolarity.DarkToLight,
                ExplicitScanDirection: dirs.Scan1Dir);

            var leg2 = new RakeSearchRegion(
                StartPoint: localApex,
                EndPoint: localApex + (dirs.Leg2Dir * 185.0f),
                SearchLength: 24.0f,
                ProjectionWidth: 9.0f,
                CaliperCount: 30,
                ApexIgnoreMargin: 20.0f,
                Polarity: CaliperPolarity.DarkToLight,
                ExplicitScanDirection: dirs.Scan2Dir);

            VirtualCornerResult localRes = VirtualCornerAligner.DetectVirtualCorner(view, leg1, leg2, options);
            if (!localRes.IsValid)
            {
                throw new InvalidOperationException($"Test 2 실패: 8K 코너 #{c} 검출에 실패했습니다.");
            }

            Vector2 worldCorner = new(localRes.CornerX + originX, localRes.CornerY + originY);
            detectedCorners[c] = localRes with { CornerX = worldCorner.X, CornerY = worldCorner.Y };
            measuredCornerPoints[c] = worldCorner;
        }

        // 1) 상단 좌·우 2-Corner (6000 px 기저선) 정합 검증
        var (_, twoCornerAngleRad, twoValid) = VirtualCornerAligner.AlignTwoCorners(detectedCorners[0], detectedCorners[1]);
        if (!twoValid)
        {
            throw new InvalidOperationException("Test 2 실패: 2-Corner 기저선 정합이 무효합니다.");
        }

        float baselineAngleErrorDeg = MathF.Abs(NormalizeAngleDiffRad(twoCornerAngleRad, trueAngleRad)) * RadiansToDegrees;
        if (baselineAngleErrorDeg > MaxAllowed8kTwoCornerBaselineAngleErrorDeg)
        {
            throw new InvalidOperationException(
                $"Test 2 6000px 기저선 각도 오차 초과: {baselineAngleErrorDeg:F6}° > {MaxAllowed8kTwoCornerBaselineAngleErrorDeg:F6}°");
        }

        // 2) 4-Corner Diagonal 정합 (+0.4 px 제조 팽창 공차 양분 상쇄 검증)
        var (measuredCentroid, kabschAngleRad, _, fourValid) = VirtualCornerAligner.AlignDiagonalCorners(referenceCorners, measuredCornerPoints);
        if (!fourValid)
        {
            throw new InvalidOperationException("Test 2 실패: 4-Corner Diagonal 정합이 무효합니다.");
        }

        float centerErrorPx = Vector2.Distance(measuredCentroid, truePanelCenter);
        float kabschAngleErrorDeg = MathF.Abs(NormalizeAngleDiffRad(kabschAngleRad, trueAngleRad)) * RadiansToDegrees;

        if (centerErrorPx > MaxAllowed8kFourCornerExpansionCenterErrorPx || kabschAngleErrorDeg > MaxAllowed8kTwoCornerBaselineAngleErrorDeg)
        {
            throw new InvalidOperationException(
                $"Test 2 4-Corner 정합 오차 초과: 중심 오차={centerErrorPx:F4} px, 각도 오차={kabschAngleErrorDeg:F6}°");
        }

        return (baselineAngleErrorDeg, centerErrorPx);
    }

    /// <summary>
    /// Test 3: 숨겨진 기구 회전 중심(X_cor = 125.4 mm, Y_cor = -84.2 mm)을 기준으로
    /// -5°, -2.5°, 0°, +2.5°, +5° 회전된 5개 코너 좌표로부터 회전 중심을 오차 &lt;= 0.001 mm 이내로 역산하고,
    /// 레버암 보정 1-Shot 이동 후 잔차 오차 &lt;= 0.0001 mm 이내를 검증한다.
    /// </summary>
    public static (double CorCenterErrorMm, double OneShotResidualMm) VerifyTest3_CenterOfRotationAndLeverArmOneShotAlignment()
    {
        const double trueCorX = 125.4;
        const double trueCorY = -84.2;
        const double masterCornerX = 210.6;
        const double masterCornerY = 45.8;
        const double masterThetaRad = 0.0;

        ReadOnlySpan<double> calibrationAnglesDeg = [-5.0, -2.5, 0.0, 2.5, 5.0];
        Span<(double X_Mm, double Y_Mm)> trajectory = stackalloc (double X_Mm, double Y_Mm)[calibrationAnglesDeg.Length];

        for (int i = 0; i < calibrationAnglesDeg.Length; i++)
        {
            double rad = calibrationAnglesDeg[i] * (Math.PI / 180.0);
            var (rx, ry, _) = RigidBodyAlignSolver.SimulateMotionAroundCor(
                masterCornerX,
                masterCornerY,
                masterThetaRad,
                trueCorX,
                trueCorY,
                deltaX_Mm: 0.0,
                deltaY_Mm: 0.0,
                deltaThetaRad: rad);
            trajectory[i] = (rx, ry);
        }

        var (estCorX, estCorY, _, _, isValid) = CenterOfRotationCalibrator.CalibrateCenterOfRotation(trajectory);
        if (!isValid)
        {
            throw new InvalidOperationException("Test 3 실패: 회전 중심(CoR) 원 피팅에 실패했습니다.");
        }

        double corErrorMm = Math.Sqrt(((estCorX - trueCorX) * (estCorX - trueCorX)) + ((estCorY - trueCorY) * (estCorY - trueCorY)));
        if (corErrorMm > MaxAllowedCorCalibrationErrorMm)
        {
            throw new InvalidOperationException(
                $"Test 3 회전 중심 역산 오차 초과: {corErrorMm:E4} mm > {MaxAllowedCorCalibrationErrorMm:E4} mm");
        }

        // 임의 틀어진 현재 부품 상태 (병진 오프셋 + 회전 오프셋 -3.75°가 결합된 상태)에서 1-Shot 보정 검증
        double misalignedThetaRad = -3.75 * (Math.PI / 180.0);
        var (currentX, currentY, currentTheta) = RigidBodyAlignSolver.SimulateMotionAroundCor(
            masterCornerX,
            masterCornerY,
            masterThetaRad,
            trueCorX,
            trueCorY,
            deltaX_Mm: -4.25,
            deltaY_Mm: 6.80,
            deltaThetaRad: misalignedThetaRad);

        var (cmdDeltaX, cmdDeltaY, cmdDeltaTheta) = RigidBodyAlignSolver.ComputeOneShotAlignment(
            masterCornerX,
            masterCornerY,
            masterThetaRad,
            currentX,
            currentY,
            currentTheta,
            estCorX,
            estCorY);

        var (finalX, finalY, finalTheta) = RigidBodyAlignSolver.SimulateMotionAroundCor(
            currentX,
            currentY,
            currentTheta,
            trueCorX,
            trueCorY,
            cmdDeltaX,
            cmdDeltaY,
            cmdDeltaTheta);

        double oneShotResidualMm = Math.Sqrt(((finalX - masterCornerX) * (finalX - masterCornerX)) + ((finalY - masterCornerY) * (finalY - masterCornerY)));
        double angleResidualRad = Math.Abs(finalTheta - masterThetaRad);

        if (oneShotResidualMm > MaxAllowedOneShotAlignmentResidualMm || angleResidualRad > 1e-9)
        {
            throw new InvalidOperationException(
                $"Test 3 1-Shot 정합 잔차 초과: 위치 잔차={oneShotResidualMm:E4} mm, 각도 잔차={angleResidualRad:E4} rad");
        }

        return (corErrorMm, oneShotResidualMm);
    }

    /// <summary>
    /// Test 4: UVW 병렬 스테이지 역기구학이 산출한 (DeltaU, DeltaV, DeltaW) 스트로크를
    /// 비선형 순기구학에 대입했을 때 목표 강체 변환 (DeltaX, DeltaY, DeltaTheta)를 정확히 복원하는지 교차 검증한다.
    /// </summary>
    public static double VerifyTest4_UvwStageForwardInverseKinematics()
    {
        UvwStageInverseKinematics stage = UvwStageInverseKinematics.CreateStandardStage(stageRadiusMm: 160.0);

        const double targetDeltaX = 3.45;
        const double targetDeltaY = -2.18;
        double targetDeltaThetaRad = 1.85 * (Math.PI / 180.0);

        MotionCorrectionCommand command = stage.ComputeActuatorStrokes(targetDeltaX, targetDeltaY, targetDeltaThetaRad);
        var (recoveredDx, recoveredDy, recoveredDTheta, converged) = stage.SolveForwardKinematics(
            command.DeltaU_Mm,
            command.DeltaV_Mm,
            command.DeltaW_Mm);

        if (!converged)
        {
            throw new InvalidOperationException("Test 4 실패: UVW 순기구학 Newton-Raphson 해가 수렴하지 않았습니다.");
        }

        double errX = recoveredDx - targetDeltaX;
        double errY = recoveredDy - targetDeltaY;
        double errThetaMmEquiv = (recoveredDTheta - targetDeltaThetaRad) * 160.0;
        double totalRoundTripErr = Math.Sqrt((errX * errX) + (errY * errY) + (errThetaMmEquiv * errThetaMmEquiv));

        if (totalRoundTripErr > MaxAllowedUvwKinematicRoundTripErrorMm)
        {
            throw new InvalidOperationException(
                $"Test 4 UVW 순·역기구학 교차 오차 초과: {totalRoundTripErr:E4} mm > {MaxAllowedUvwKinematicRoundTripErrorMm:E4} mm");
        }

        return totalRoundTripErr;
    }

    private static float NormalizeAngleDiffRad(float a, float b)
    {
        float diff = (a - b + MathF.PI) % (2.0f * MathF.PI);
        if (diff < 0.0f)
        {
            diff += 2.0f * MathF.PI;
        }

        return diff - MathF.PI;
    }
}
