// File: src/VisionAlgorithm/OpenSrc/PatchAnomaly/PadimHeatmapDetector.cs
using System.Numerics;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Engine;

namespace VisionAlgorithm.OpenSrc.PatchAnomaly;

/// <summary>
/// Implements PaDiM ("PaDiM: a Patch Distribution Modeling Framework for Anomaly Detection and Localization",
/// Defard et al., ICPR 2021): fits a spatially-indexed multivariate Gaussian $\mathcal{N}(\mu_{ij}, \Sigma_{ij} + \epsilon I)$
/// at each patch coordinate $(i, j)$ from normal training images, evaluates per-patch Mahalanobis distance at inference,
/// and upsamples the patch distance map to a full-resolution pixel anomaly heatmap via bilinear interpolation.
/// </summary>
public sealed class PadimHeatmapDetector
{
    // [Math Why]: PaDiM (Defard et al., 2021) Eq. (2) adds ridge regularization epsilon * I to each patch covariance
    // to ensure full-rank invertibility even when local patches are homogeneous.
    private const float DefaultCalibrationMultiplier = 4.0f;

    // [Math Why]: Minimum anomaly threshold floor prevents false positives on low-noise synthetic backgrounds.
    private const float MinimumAnomalyScoreThreshold = 8.5f;

    private readonly float[] _patchMeans;
    private readonly LedoitWolfMahalanobis[] _patchMetrics;

    public int RoiWidth { get; }
    public int RoiHeight { get; }
    public int PatchSize { get; }
    public int GridWidth { get; }
    public int GridHeight { get; }
    public float AnomalyThreshold { get; }

    private PadimHeatmapDetector(
        int roiWidth,
        int roiHeight,
        int patchSize,
        int gridWidth,
        int gridHeight,
        float anomalyThreshold,
        float[] patchMeans,
        LedoitWolfMahalanobis[] patchMetrics)
    {
        RoiWidth = roiWidth;
        RoiHeight = roiHeight;
        PatchSize = patchSize;
        GridWidth = gridWidth;
        GridHeight = gridHeight;
        AnomalyThreshold = anomalyThreshold;
        _patchMeans = patchMeans;
        _patchMetrics = patchMetrics;
    }

    /// <summary>
    /// Trains a spatially-localized PaDiM Gaussian distribution model from $N$ normal floating-point ROI patches.
    /// </summary>
    public static PadimHeatmapDetector Train(
        IReadOnlyList<float[]> normalRectifiedRois,
        int roiWidth,
        int roiHeight,
        int patchSize = 8)
    {
        ArgumentNullException.ThrowIfNull(normalRectifiedRois);
        if (normalRectifiedRois.Count == 0)
        {
            throw new ArgumentException("At least one normal ROI is required to train PadimHeatmapDetector.", nameof(normalRectifiedRois));
        }

        int n = normalRectifiedRois.Count;
        int d = MultiScalePatchExtractor.EmbeddingDimension;
        int gridW = roiWidth / patchSize;
        int gridH = roiHeight / patchSize;
        int cellCount = checked(gridW * gridH);

        float[][] allPatchEmbeddings = new float[n][];
        for (int s = 0; s < n; s++)
        {
            allPatchEmbeddings[s] = new float[cellCount * d];
            MultiScalePatchExtractor.ExtractPatchGrid(
                normalRectifiedRois[s],
                roiWidth,
                roiHeight,
                patchSize,
                out _,
                out _,
                allPatchEmbeddings[s]);
        }

        float[] patchMeans = new float[cellCount * d];
        LedoitWolfMahalanobis[] patchMetrics = new LedoitWolfMahalanobis[cellCount];
        float[] cellResiduals = new float[n * d];
        float invN = 1f / n;

        for (int cell = 0; cell < cellCount; cell++)
        {
            int cellOffset = cell * d;
            Span<float> mean = patchMeans.AsSpan(cellOffset, d);

            for (int s = 0; s < n; s++)
            {
                ReadOnlySpan<float> emb = allPatchEmbeddings[s].AsSpan(cellOffset, d);
                for (int k = 0; k < d; k++)
                {
                    mean[k] += emb[k] * invN;
                }
            }

            for (int s = 0; s < n; s++)
            {
                ReadOnlySpan<float> emb = allPatchEmbeddings[s].AsSpan(cellOffset, d);
                Span<float> res = cellResiduals.AsSpan(s * d, d);
                for (int k = 0; k < d; k++)
                {
                    res[k] = emb[k] - mean[k];
                }
            }

            patchMetrics[cell] = LedoitWolfMahalanobis.FitFromCenteredResiduals(cellResiduals, n, d, classClusterCount: 1);
        }

        // Calibrate anomaly threshold from maximum normal training patch distance
        Span<float> scratch = stackalloc float[d];
        float maxNormalDist = 0f;
        for (int s = 0; s < n; s++)
        {
            for (int cell = 0; cell < cellCount; cell++)
            {
                int cellOffset = cell * d;
                float dist = patchMetrics[cell].ComputeDistance(
                    allPatchEmbeddings[s].AsSpan(cellOffset, d),
                    patchMeans.AsSpan(cellOffset, d),
                    scratch);
                if (dist > maxNormalDist)
                {
                    maxNormalDist = dist;
                }
            }
        }

        float calibratedThreshold = MathF.Max(MinimumAnomalyScoreThreshold, maxNormalDist * DefaultCalibrationMultiplier);
        return new PadimHeatmapDetector(
            roiWidth,
            roiHeight,
            patchSize,
            gridW,
            gridH,
            calibratedThreshold,
            patchMeans,
            patchMetrics);
    }

    /// <summary>
    /// Evaluates a rectified ROI and produces a full-resolution $(W \times H)$ anomaly heatmap via bilinear upsampling.
    /// </summary>
    public PadimAnomalyResult Inspect(
        ReadOnlySpan<float> queryRectifiedRoi,
        InspectionWorkspace workspace,
        Span<float> pixelHeatmapOut)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        int pixelCount = checked(RoiWidth * RoiHeight);
        if (queryRectifiedRoi.Length < pixelCount || pixelHeatmapOut.Length < pixelCount)
        {
            throw new ArgumentException("Input and output buffers must have length >= RoiWidth * RoiHeight.");
        }

        int d = MultiScalePatchExtractor.EmbeddingDimension;
        int cellCount = GridWidth * GridHeight;
        Span<float> queryEmbeddings = workspace.GetFloatBufferB(cellCount * d);
        MultiScalePatchExtractor.ExtractPatchGrid(
            queryRectifiedRoi,
            RoiWidth,
            RoiHeight,
            PatchSize,
            out _,
            out _,
            queryEmbeddings);

        Span<float> patchMap = workspace.GetFloatBufferC(cellCount);
        Span<float> scratch = workspace.GetFloatBufferD(d);

        for (int cell = 0; cell < cellCount; cell++)
        {
            int cellOffset = cell * d;
            patchMap[cell] = _patchMetrics[cell].ComputeDistance(
                queryEmbeddings.Slice(cellOffset, d),
                _patchMeans.AsSpan(cellOffset, d),
                scratch);
        }

        // Bilinearly upsample patchMap (GridWidth x GridHeight) to pixelHeatmapOut (RoiWidth x RoiHeight)
        UpsampleBilinear(patchMap, GridWidth, GridHeight, RoiWidth, RoiHeight, PatchSize, pixelHeatmapOut);

        float maxScore = 0f;
        int peakX = 0;
        int peakY = 0;
        int anomalousPixels = 0;

        for (int y = 0; y < RoiHeight; y++)
        {
            int row = y * RoiWidth;
            for (int x = 0; x < RoiWidth; x++)
            {
                float score = pixelHeatmapOut[row + x];
                if (score > maxScore)
                {
                    maxScore = score;
                    peakX = x;
                    peakY = y;
                }

                if (score > AnomalyThreshold)
                {
                    anomalousPixels++;
                }
            }
        }

        return new PadimAnomalyResult(
            IsAnomalous: maxScore > AnomalyThreshold,
            ImageAnomalyScore: maxScore,
            AnomalyThreshold: AnomalyThreshold,
            PeakAnomalyLocalPosition: new Vector2(peakX + 0.5f, peakY + 0.5f),
            AnomalousPixelCount: anomalousPixels);
    }

    private static void UpsampleBilinear(
        ReadOnlySpan<float> patchGrid,
        int gridW,
        int gridH,
        int dstW,
        int dstH,
        int patchSize,
        Span<float> dstHeatmap)
    {
        float invPatch = 1f / patchSize;
        int maxGx = gridW - 1;
        int maxGy = gridH - 1;

        for (int y = 0; y < dstH; y++)
        {
            // Map pixel center (y + 0.5) to patch grid coordinates where patch gy has center (gy + 0.5) * patchSize
            float gyCoord = Math.Clamp(((y + 0.5f) * invPatch) - 0.5f, 0f, maxGy);
            int gy0 = (int)MathF.Floor(gyCoord);
            int gy1 = Math.Min(gy0 + 1, maxGy);
            float fy = gyCoord - gy0;

            int row0 = gy0 * gridW;
            int row1 = gy1 * gridW;
            int dstRow = y * dstW;

            for (int x = 0; x < dstW; x++)
            {
                float gxCoord = Math.Clamp(((x + 0.5f) * invPatch) - 0.5f, 0f, maxGx);
                int gx0 = (int)MathF.Floor(gxCoord);
                int gx1 = Math.Min(gx0 + 1, maxGx);
                float fx = gxCoord - gx0;

                float p00 = patchGrid[row0 + gx0];
                float p10 = patchGrid[row0 + gx1];
                float p01 = patchGrid[row1 + gx0];
                float p11 = patchGrid[row1 + gx1];

                float top = (p00 * (1f - fx)) + (p10 * fx);
                float bot = (p01 * (1f - fx)) + (p11 * fx);
                dstHeatmap[dstRow + x] = (top * (1f - fy)) + (bot * fy);
            }
        }
    }
}
