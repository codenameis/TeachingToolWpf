// File: src/VisionAlgorithm/OpenSrc/PatchAnomaly/MultiScalePatchExtractor.cs
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.PatchAnomaly;

/// <summary>
/// Extracts dense multi-scale local patch embeddings ($D = 12$ channels per spatial cell) from a rectified grayscale ROI
/// using integral-image / box-pooled multi-scale intensity, gradient orientation histograms, and Laplacian energy.
/// Designed for PaDiM (Defard et al., ICPR 2021) spatially-localized Gaussian anomaly modeling without external GPU dependencies.
/// </summary>
public static class MultiScalePatchExtractor
{
    /// <summary>Number of multi-scale texture/gradient descriptor channels per spatial patch cell.</summary>
    public const int EmbeddingDimension = 12;

    // [Math Why]: Minimum ROI dimension (4 px) required to form at least a 2x2 grid of 2x2 patches.
    private const int MinimumRoiDimension = 4;

    // [Math Why]: Small positive floor avoids division by zero when normalizing local contrast.
    private const float ContrastNormEpsilon = 1e-4f;

    // [Math Why]: Discrete 5-point 2D Laplacian stencil weights the center pixel by -4 against its 4 axial neighbors (+1 each).
    private const float LaplacianCenterStencilWeight = 4f;

    // [Math Why]: Unit projection factor cos(45 deg) = sin(45 deg) = 1 / sqrt(2) for 45-degree and 135-degree diagonal gradients.
    private const float InvSqrt2 = 0.70710678f;

    /// <summary>
    /// Extracts a grid of patch embeddings of shape <c>[gridHeight * gridWidth * EmbeddingDimension]</c>
    /// where each spatial cell aggregates a <paramref name="patchSize"/> $\times$ <paramref name="patchSize"/> pixel block
    /// and its $2\times$ context neighborhood.
    /// </summary>
    public static void ExtractPatchGrid(
        ReadOnlySpan<float> rectifiedRoi,
        int roiWidth,
        int roiHeight,
        int patchSize,
        out int gridWidth,
        out int gridHeight,
        Span<float> patchEmbeddingsOut)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(roiWidth, MinimumRoiDimension);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(roiHeight, MinimumRoiDimension);
        ArgumentOutOfRangeException.ThrowIfLessThan(patchSize, 2);
        if (rectifiedRoi.Length < checked(roiWidth * roiHeight))
        {
            throw new ArgumentException("rectifiedRoi buffer is smaller than roiWidth * roiHeight.", nameof(rectifiedRoi));
        }

        gridWidth = Math.Max(1, roiWidth / patchSize);
        gridHeight = Math.Max(1, roiHeight / patchSize);
        int requiredLength = checked(gridWidth * gridHeight * EmbeddingDimension);
        if (patchEmbeddingsOut.Length < requiredLength)
        {
            throw new ArgumentException($"patchEmbeddingsOut length must be >= {requiredLength}.", nameof(patchEmbeddingsOut));
        }

        float invPatchArea = 1f / (patchSize * patchSize);

        for (int gy = 0; gy < gridHeight; gy++)
        {
            int yStart = gy * patchSize;
            for (int gx = 0; gx < gridWidth; gx++)
            {
                int xStart = gx * patchSize;
                int cellIndex = ((gy * gridWidth) + gx) * EmbeddingDimension;
                Span<float> desc = patchEmbeddingsOut.Slice(cellIndex, EmbeddingDimension);

                float sumI = 0f;
                float sumISq = 0f;
                float sumAbsGx = 0f;
                float sumAbsGy = 0f;
                float sumPosGx = 0f;
                float sumNegGx = 0f;
                float sumPosGy = 0f;
                float sumNegGy = 0f;
                float sumDiag1 = 0f;
                float sumDiag2 = 0f;
                float sumLaplacian = 0f;

                for (int py = 0; py < patchSize; py++)
                {
                    int y = yStart + py;
                    int yPrev = Math.Max(0, y - 1);
                    int yNext = Math.Min(roiHeight - 1, y + 1);

                    int rowPrev = yPrev * roiWidth;
                    int rowCurr = y * roiWidth;
                    int rowNext = yNext * roiWidth;

                    for (int px = 0; px < patchSize; px++)
                    {
                        int x = xStart + px;
                        int xPrev = Math.Max(0, x - 1);
                        int xNext = Math.Min(roiWidth - 1, x + 1);

                        float c = rectifiedRoi[rowCurr + x];
                        float l = rectifiedRoi[rowCurr + xPrev];
                        float r = rectifiedRoi[rowCurr + xNext];
                        float u = rectifiedRoi[rowPrev + x];
                        float d = rectifiedRoi[rowNext + x];

                        float gxVal = 0.5f * (r - l);
                        float gyVal = 0.5f * (d - u);
                        float lap = MathF.Abs((l + r + u + d) - (LaplacianCenterStencilWeight * c));

                        sumI += c;
                        sumISq += c * c;
                        sumAbsGx += MathF.Abs(gxVal);
                        sumAbsGy += MathF.Abs(gyVal);

                        if (gxVal >= 0f)
                        {
                            sumPosGx += gxVal;
                        }
                        else
                        {
                            sumNegGx -= gxVal;
                        }

                        if (gyVal >= 0f)
                        {
                            sumPosGy += gyVal;
                        }
                        else
                        {
                            sumNegGy -= gyVal;
                        }

                        // Diagonal directional projections (45 deg and 135 deg)
                        float d1 = InvSqrt2 * (gxVal + gyVal);
                        float d2 = InvSqrt2 * (gxVal - gyVal);
                        sumDiag1 += MathF.Abs(d1);
                        sumDiag2 += MathF.Abs(d2);
                        sumLaplacian += lap;
                    }
                }

                float meanI = sumI * invPatchArea;
                float varI = MathF.Max(0f, (sumISq * invPatchArea) - (meanI * meanI));
                float stdI = MathF.Sqrt(varI);

                // Compute 2x broader neighborhood mean for multi-scale contrast
                float contextMean = ComputeBroadContextMean(rectifiedRoi, roiWidth, roiHeight, xStart, yStart, patchSize);

                desc[0] = meanI;
                desc[1] = stdI;
                desc[2] = meanI - contextMean;
                desc[3] = sumAbsGx * invPatchArea;
                desc[4] = sumAbsGy * invPatchArea;
                desc[5] = sumPosGx * invPatchArea;
                desc[6] = sumNegGx * invPatchArea;
                desc[7] = sumPosGy * invPatchArea;
                desc[8] = sumNegGy * invPatchArea;
                desc[9] = sumDiag1 * invPatchArea;
                desc[10] = sumDiag2 * invPatchArea;
                desc[11] = (sumLaplacian * invPatchArea) + ContrastNormEpsilon;
            }
        }
    }

    private static float ComputeBroadContextMean(
        ReadOnlySpan<float> roi,
        int width,
        int height,
        int xStart,
        int yStart,
        int patchSize)
    {
        int x0 = Math.Max(0, xStart - (patchSize / 2));
        int y0 = Math.Max(0, yStart - (patchSize / 2));
        int x1 = Math.Min(width, xStart + patchSize + (patchSize / 2));
        int y1 = Math.Min(height, yStart + patchSize + (patchSize / 2));

        float sum = 0f;
        int count = Math.Max(1, (x1 - x0) * (y1 - y0));
        for (int y = y0; y < y1; y++)
        {
            int row = y * width;
            for (int x = x0; x < x1; x++)
            {
                sum += roi[row + x];
            }
        }

        return sum / count;
    }
}
