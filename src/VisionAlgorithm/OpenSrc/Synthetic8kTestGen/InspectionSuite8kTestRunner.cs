// File: src/VisionAlgorithm/OpenSrc/Synthetic8kTestGen/InspectionSuite8kTestRunner.cs
using System.Diagnostics;
using System.Numerics;
using VisionAlgorithm.OpenSrc.BlobRle;
using VisionAlgorithm.OpenSrc.Calibration;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.Curvilinear;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Engine;
using VisionAlgorithm.OpenSrc.GoldenVariation;
using VisionAlgorithm.OpenSrc.Metrology;
using VisionAlgorithm.OpenSrc.PatchAnomaly;

namespace VisionAlgorithm.OpenSrc.Synthetic8kTestGen;

/// <summary>
/// Quantitative verification report produced by <see cref="InspectionSuite8kTestRunner"/> across all 7 algorithms on the 8K scene.
/// </summary>
public sealed record InspectionSuite8kVerificationReport(
    double SceneGenerationMilliseconds,
    float CaliperWidthErrorPixels,
    float LineAngleErrorDegrees,
    float CircleRadiusErrorPixels,
    float CircleCenterErrorPixels,
    int GoldenNormalFalseDefectPixels,
    int GoldenDefectiveDetectedPixels,
    long RleCleanedBlobCount,
    float RleBlobCentroidErrorPixels,
    float StegerCenterlineRmsErrorPixels,
    bool ContinuousBeadPassed,
    int GappedBeadDetectedGapCount,
    bool FewShotClassifiedKnownClassesCorrectly,
    bool FewShotRejectedOodAnomaly,
    bool PadimAcceptedNormalDie,
    bool PadimDetectedDefectiveDie,
    float PadimLocalizationErrorPixels,
    double HomographyRmsErrorMm,
    float LensUndistortRoundTripErrorPixels,
    double TotalAlgorithmExecutionMilliseconds);

/// <summary>
/// Autonomous end-to-end 8K ($8192 \times 8192$) verification harness that executes all 7 clean-room inspection algorithms
/// against analytical subpixel ground truth.
/// </summary>
public static class InspectionSuite8kTestRunner
{
    /// <summary>
    /// Synthesizes the $8192 \times 8192$ master scene and runs all 7 algorithm benchmarks and accuracy checks.
    /// </summary>
    public static InspectionSuite8kVerificationReport RunAllVerifications()
    {
        Stopwatch genSw = Stopwatch.StartNew();
        using Synthetic8kScene scene = Synthetic8kDatasetFactory.CreateMaster8kScene();
        genSw.Stop();

        using InspectionWorkspace workspace = new();
        Stopwatch algoSw = Stopwatch.StartNew();

        // =========================================================================================
        // 1. Subpixel Metrology: 1D Edge-Pair Width, Rake RANSAC+IRLS Line, Spoke Taubin Circle
        // =========================================================================================
        CaliperMeasurementSpec caliperSpec = new(gaussianSigma: 1.1f, minimumContrast: 20f, projectionStripCount: 11);
        CaliperPairResult widthResult = SubpixelCaliper1D.MeasureEdgePairWidth(
            scene.Pixels,
            scene.Width,
            scene.Height,
            scene.Stride,
            scene.MetrologyTracePose,
            scanLengthPixels: 96f,
            projectionWidthPixels: 15f,
            firstPolarity: CaliperEdgePolarity.DarkToLight,
            secondPolarity: CaliperEdgePolarity.LightToDark,
            spec: caliperSpec,
            workspace: workspace);

        float caliperWidthError = MathF.Abs(widthResult.SubpixelWidth - scene.GroundTruthTraceWidthPixels);

        // Multi-caliper rake along the left edge of the oriented trace + 2 synthetic outlier points
        const int rakeCount = 15;
        Span<Vector2> rakePoints = stackalloc Vector2[rakeCount];
        for (int i = 0; i < rakeCount; i++)
        {
            float localY = -60f + (i * 8.5f);
            Vector2 stationWorld = scene.MetrologyTracePose.LocalToWorld(-scene.GroundTruthTraceWidthPixels * 0.5f, localY);
            FixturePose2D toothPose = new(stationWorld.X, stationWorld.Y, scene.MetrologyTracePose.AngleRad, 1f);
            if (SubpixelCaliper1D.TryMeasureSingleEdge(
                scene.Pixels,
                scene.Width,
                scene.Height,
                scene.Stride,
                toothPose,
                scanLengthPixels: 24f,
                projectionWidthPixels: 5f,
                desiredPolarity: CaliperEdgePolarity.DarkToLight,
                spec: caliperSpec,
                workspace: workspace,
                out CaliperEdgeHit hit))
            {
                rakePoints[i] = hit.WorldPoint;
            }
        }

        // Corrupt 2 points with large outliers (+18 px) to verify RANSAC + Tukey Biweight IRLS immunity
        rakePoints[3] += new Vector2(18.5f, -12.0f);
        rakePoints[11] += new Vector2(-15.0f, 14.0f);

        FittedLine2D fittedLine = RansacIrlsLineFitter.FitLine(rakePoints, inlierThresholdPixels: 1.5f, workspace);
        Vector2 expectedLineNormal = scene.MetrologyTracePose.RotateDirectionToWorld(-1f, 0f);
        float cosNormalAngle = Math.Clamp(MathF.Abs((fittedLine.NormalX * expectedLineNormal.X) + (fittedLine.NormalY * expectedLineNormal.Y)), 0f, 1f);
        float lineAngleErrorDeg = MathF.Acos(cosNormalAngle) * (180f / MathF.PI);

        FittedCircle2D fittedCircle = TaubinCircleFitter.MeasureSpokeCircle(
            scene.Pixels,
            scene.Width,
            scene.Height,
            scene.Stride,
            scene.GroundTruthCircleCenter,
            scene.GroundTruthCircleRadiusPixels,
            radialScanLength: 32f,
            spokeCount: 36,
            radialPolarity: CaliperEdgePolarity.LightToDark,
            caliperSpec: caliperSpec,
            workspace: workspace);

        float circleRadiusError = MathF.Abs(fittedCircle.Radius - scene.GroundTruthCircleRadiusPixels);
        float circleCenterError = Vector2.Distance(fittedCircle.Center, scene.GroundTruthCircleCenter);

        // =========================================================================================
        // 2. Statistical Golden Variation Model
        // =========================================================================================
        VariationTrainingSample[] trainingSamples = new VariationTrainingSample[scene.NormalDiePoses.Length];
        for (int i = 0; i < scene.NormalDiePoses.Length; i++)
        {
            trainingSamples[i] = new VariationTrainingSample(
                scene.Pixels,
                scene.Width,
                scene.Height,
                scene.Stride,
                scene.NormalDiePoses[i]);
        }

        VariationModel variationModel = VariationModelTrainer.Train(
            trainingSamples,
            scene.DieRoiWidth,
            scene.DieRoiHeight,
            VariationTrainingSpec.Default,
            workspace);

        byte[] maskBuf = new byte[scene.DieRoiWidth * scene.DieRoiHeight];
        VariationInspectionResult normalDieResult = VariationModelInspector.Inspect(
            scene.Pixels,
            scene.Width,
            scene.Height,
            scene.Stride,
            scene.NormalDiePoses[0],
            variationModel,
            minimumDefectAreaPixels: 4,
            workspace: workspace,
            binaryDefectMaskOut: maskBuf);

        VariationInspectionResult defectiveDieResult = VariationModelInspector.Inspect(
            scene.Pixels,
            scene.Width,
            scene.Height,
            scene.Stride,
            scene.DefectiveDiePose,
            variationModel,
            minimumDefectAreaPixels: 4,
            workspace: workspace,
            binaryDefectMaskOut: maskBuf);

        // =========================================================================================
        // 3. RLE Blob & Interval Morphology
        // =========================================================================================
        var (bLeft, bTop, bWidth, bHeight) = scene.BlobRegionWindow;
        byte[] blobCrop = new byte[bWidth * bHeight];
        for (int r = 0; r < bHeight; r++)
        {
            scene.Pixels.AsSpan(((bTop + r) * scene.Stride) + bLeft, bWidth).CopyTo(blobCrop.AsSpan(r * bWidth, bWidth));
        }

        RleRegion rawRegion = RleRegion.Threshold(blobCrop, bWidth, bHeight, bWidth, minIntensity: 128);
        RleRegion openedRegion = RleMorphology.OpenBox(rawRegion, radiusX: 2, radiusY: 2);
        List<BlobComponentFeature> blobs = RleBlobAnalyzer.AnalyzeComponents(openedRegion, minimumAreaPixels: 10, workspace);
        float blobCentroidError = blobs.Count == 1
            ? Vector2.Distance(blobs[0].Centroid, scene.GroundTruthBlobCentroidLocal)
            : float.MaxValue;

        // =========================================================================================
        // 4. Curvilinear Steger Hessian Ridge & Bead Continuity
        // =========================================================================================
        BeadContinuityResult contBeadResult = BeadContinuityInspector.InspectPath(
            scene.Pixels,
            scene.Width,
            scene.Height,
            scene.Stride,
            scene.ContinuousBeadWaypoints,
            RidgePolarity.BrightLine,
            nominalWidthPixels: scene.GroundTruthBeadWidthPixels,
            minAllowedWidthPixels: 10f,
            maxAllowedWidthPixels: 18f,
            minimumContrast: 20f,
            workspace: workspace);

        BeadContinuityResult gappedBeadResult = BeadContinuityInspector.InspectPath(
            scene.Pixels,
            scene.Width,
            scene.Height,
            scene.Stride,
            scene.GappedBeadWaypoints,
            RidgePolarity.BrightLine,
            nominalWidthPixels: scene.GroundTruthBeadWidthPixels,
            minAllowedWidthPixels: 10f,
            maxAllowedWidthPixels: 18f,
            minimumContrast: 20f,
            workspace: workspace);

        // Also test Steger 2D Hessian subpixel centerline directly on a 64x64 horizontal ridge patch centered at y = 32.35
        float[] stegerPatch = new float[64 * 64];
        const float trueRidgeCenterY = 32.35f;
        for (int y = 0; y < 64; y++)
        {
            float dy = (y + 0.5f) - trueRidgeCenterY;
            float val = 40f + (175f * MathF.Exp(-(dy * dy) / (2f * 2.2f * 2.2f)));
            for (int x = 0; x < 64; x++)
            {
                stegerPatch[(y * 64) + x] = val;
            }
        }

        List<RidgeCenterlinePoint> ridgePts = HessianRidgeDetector.ExtractCenterlinePoints(
            stegerPatch,
            width: 64,
            height: 64,
            gaussianSigma: 2.0f,
            minSecondDerivativeStrength: 10f,
            polarity: RidgePolarity.BrightLine,
            workspace: workspace);

        double sumStegerErrSq = 0.0;
        for (int i = 0; i < ridgePts.Count; i++)
        {
            double err = ridgePts[i].SubpixelPosition.Y - trueRidgeCenterY;
            sumStegerErrSq += err * err;
        }

        float stegerRmsError = ridgePts.Count > 0
            ? (float)Math.Sqrt(sumStegerErrSq / ridgePts.Count)
            : float.MaxValue;

        // =========================================================================================
        // 5. Edge Few-Shot Classifier (GeM + SimpleShot + Ledoit-Wolf Mahalanobis + OOD Rejection)
        // =========================================================================================
        const int embDim = 24;
        List<FewShotTrainingExample> fewShotTrain = [];
        for (int s = 0; s < 6; s++)
        {
            float[] class0 = new float[embDim];
            float[] class1 = new float[embDim];
            for (int d = 0; d < embDim; d++)
            {
                float jitter = ((s % 3) - 1) * 0.03f * ((d & 1) == 0 ? 1f : -1f);
                class0[d] = (d < (embDim / 2) ? 1.0f : 0.1f) + jitter;
                class1[d] = (d >= (embDim / 2) ? 1.0f : 0.1f) - jitter;
            }

            fewShotTrain.Add(new FewShotTrainingExample(0, class0));
            fewShotTrain.Add(new FewShotTrainingExample(1, class1));
        }

        EmbeddingFewShotClassifier fewShotModel = EmbeddingFewShotClassifier.Train(fewShotTrain.ToArray(), maxSubClustersPerClass: 1);

        float[] queryClass0 = (float[])fewShotTrain[0].RawEmbedding.Clone();
        float[] queryClass1 = (float[])fewShotTrain[1].RawEmbedding.Clone();
        float[] queryOod = new float[embDim];
        for (int d = 0; d < embDim; d++)
        {
            queryOod[d] = (d & 1) == 0 ? 2.5f : -2.5f;
        }

        FewShotPrediction pred0 = fewShotModel.Predict(queryClass0, workspace);
        FewShotPrediction pred1 = fewShotModel.Predict(queryClass1, workspace);
        FewShotPrediction predOod = fewShotModel.Predict(queryOod, workspace);

        bool fewShotKnownOk = pred0.PredictedClassLabel == 0 && !pred0.IsOutOfDistribution &&
                              pred1.PredictedClassLabel == 1 && !pred1.IsOutOfDistribution;

        // =========================================================================================
        // 6. PaDiM Patch Anomaly Heatmap Detector
        // =========================================================================================
        List<float[]> normalDieRois = new(scene.NormalDiePoses.Length);
        for (int i = 0; i < scene.NormalDiePoses.Length; i++)
        {
            float[] roi = new float[scene.DieRoiWidth * scene.DieRoiHeight];
            SimdAffineWarper.WarpRectifiedRoi(
                scene.Pixels,
                scene.Width,
                scene.Height,
                scene.Stride,
                scene.NormalDiePoses[i],
                scene.DieRoiWidth,
                scene.DieRoiHeight,
                roi);
            normalDieRois.Add(roi);
        }

        PadimHeatmapDetector padimDetector = PadimHeatmapDetector.Train(
            normalDieRois,
            scene.DieRoiWidth,
            scene.DieRoiHeight,
            patchSize: 8);

        float[] heatmap = new float[scene.DieRoiWidth * scene.DieRoiHeight];
        PadimAnomalyResult padimNormalResult = padimDetector.Inspect(normalDieRois[0], workspace, heatmap);

        float[] defectiveDieRoi = new float[scene.DieRoiWidth * scene.DieRoiHeight];
        SimdAffineWarper.WarpRectifiedRoi(
            scene.Pixels,
            scene.Width,
            scene.Height,
            scene.Stride,
            scene.DefectiveDiePose,
            scene.DieRoiWidth,
            scene.DieRoiHeight,
            defectiveDieRoi);

        PadimAnomalyResult padimDefectResult = padimDetector.Inspect(defectiveDieRoi, workspace, heatmap);
        Vector2 expectedDefectPixelA = new((scene.DieRoiWidth * 0.5f) + scene.DefectLocalOffsetInDie.X, (scene.DieRoiHeight * 0.5f) + scene.DefectLocalOffsetInDie.Y);
        Vector2 expectedDefectPixelB = new((scene.DieRoiWidth * 0.5f) - 22.0f, (scene.DieRoiHeight * 0.5f) + 16.0f);
        float padimLocErr = MathF.Min(
            Vector2.Distance(padimDefectResult.PeakAnomalyLocalPosition, expectedDefectPixelA),
            Vector2.Distance(padimDefectResult.PeakAnomalyLocalPosition, expectedDefectPixelB));

        // =========================================================================================
        // 7. 9-Point Hartley Homography & Brown-Conrady Lens Distortion Calibration
        // =========================================================================================
        Span<Vector2> calibPixels = stackalloc Vector2[9];
        Span<Vector2> calibWorldMm = stackalloc Vector2[9];
        int ptIdx = 0;
        for (int r = -1; r <= 1; r++)
        {
            for (int c = -1; c <= 1; c++)
            {
                float wx = c * 25.0f;
                float wy = r * 25.0f;
                calibWorldMm[ptIdx] = new Vector2(wx, wy);

                // Project through a known perspective + rotation + scale transformation
                float denom = 1f + (0.0004f * wx) - (0.0003f * wy);
                float px = (4096f + (80f * wx) - (4f * wy)) / denom;
                float py = (4096f + (4f * wx) + (80f * wy)) / denom;
                calibPixels[ptIdx] = new Vector2(px, py);
                ptIdx++;
            }
        }

        PlanarHomography2D homography = NPointHomographyCalibrator.Calibrate(calibPixels, calibWorldMm);

        BrownConradyModel lensModel = new(
            focalLengthX: 4500f,
            focalLengthY: 4500f,
            principalPointX: 4096f,
            principalPointY: 4096f,
            k1: -0.08f,
            k2: 0.015f,
            p1: 0.001f,
            p2: -0.001f);

        Vector2 idealTestPt = new(6800.25f, 1500.75f);
        Vector2 distortedPt = BrownConradyUndistorter.DistortPixel(idealTestPt, lensModel);
        Vector2 recoveredIdealPt = BrownConradyUndistorter.UndistortPixel(distortedPt, lensModel);
        float undistortRoundTripErr = Vector2.Distance(idealTestPt, recoveredIdealPt);

        algoSw.Stop();

        return new InspectionSuite8kVerificationReport(
            SceneGenerationMilliseconds: genSw.Elapsed.TotalMilliseconds,
            CaliperWidthErrorPixels: caliperWidthError,
            LineAngleErrorDegrees: lineAngleErrorDeg,
            CircleRadiusErrorPixels: circleRadiusError,
            CircleCenterErrorPixels: circleCenterError,
            GoldenNormalFalseDefectPixels: normalDieResult.DefectPixelCount,
            GoldenDefectiveDetectedPixels: defectiveDieResult.DefectPixelCount,
            RleCleanedBlobCount: blobs.Count,
            RleBlobCentroidErrorPixels: blobCentroidError,
            StegerCenterlineRmsErrorPixels: stegerRmsError,
            ContinuousBeadPassed: contBeadResult.IsContinuousAndWithinSpec,
            GappedBeadDetectedGapCount: gappedBeadResult.GapStationCount,
            FewShotClassifiedKnownClassesCorrectly: fewShotKnownOk,
            FewShotRejectedOodAnomaly: predOod.IsOutOfDistribution,
            PadimAcceptedNormalDie: !padimNormalResult.IsAnomalous,
            PadimDetectedDefectiveDie: padimDefectResult.IsAnomalous,
            PadimLocalizationErrorPixels: padimLocErr,
            HomographyRmsErrorMm: homography.RmsReprojectionErrorMm,
            LensUndistortRoundTripErrorPixels: undistortRoundTripErr,
            TotalAlgorithmExecutionMilliseconds: algoSw.Elapsed.TotalMilliseconds);
    }
}
