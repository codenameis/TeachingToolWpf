using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.IndustrialOcrOcv;

/// <summary>
/// Builds or merges an <see cref="IndustrialOcrFontModel"/> from golden character ROIs,
/// integrating <see cref="SimpleShotNormalizer"/> for centered L2 normalization (CL2N) and precomputing
/// horizontal/vertical projection profiles and $4 \times 4$ quadrant stroke coverage densities.
/// </summary>
public static class IndustrialOcrTrainer
{
    private const float StrokeActivationThreshold = 0.32f;
    private const float MinStdDevFloor = 1e-4f;
    private const float PixelMaxIntensity = byte.MaxValue;
    private const float PixelCenterOffset = 0.5f;

    /// <summary>
    /// Trains an <see cref="IndustrialOcrFontModel"/> from golden character image patches.
    /// </summary>
    public static IndustrialOcrFontModel Train(ReadOnlySpan<GoldenCharSample> samples)
    {
        if (samples.IsEmpty)
        {
            throw new ArgumentException("At least one golden glyph patch is required.", nameof(samples));
        }

        return TrainOrMerge(existingModel: null, samples, samples[0].DarkOnLight);
    }

    /// <summary>
    /// Merges an optional existing <see cref="IndustrialOcrFontModel"/> (including models loaded from <c>.ocvfont</c> files
    /// where raw pixel patches are no longer retained) with newly taught <paramref name="newSamples"/>, recomputing
    /// the global mean vector and all CL2N features coherently across the combined template set.
    /// </summary>
    public static IndustrialOcrFontModel TrainOrMerge(
        IndustrialOcrFontModel? existingModel,
        ReadOnlySpan<GoldenCharSample> newSamples,
        bool darkOnLight = true)
    {
        int existingCount = existingModel?.Count ?? 0;
        int newCount = newSamples.Length;
        int totalCount = existingCount + newCount;
        if (totalCount == 0)
        {
            throw new ArgumentException("At least one golden glyph patch is required.", nameof(newSamples));
        }

        int dim = IndustrialOcrGlyphTemplate.GridLength;
        float[] allGrids = new float[totalCount * dim];
        char[] characters = new char[totalCount];
        float[][] rawGrids = new float[totalCount][];
        float[][] hProjs = new float[totalCount][];
        float[][] vProjs = new float[totalCount][];
        float[][] quadDensities = new float[totalCount][];

        if (existingModel is not null)
        {
            ReadOnlySpan<IndustrialOcrGlyphTemplate> prevTemplates = existingModel.Templates;
            for (int i = 0; i < prevTemplates.Length; i++)
            {
                IndustrialOcrGlyphTemplate prev = prevTemplates[i];
                float[] gridCopy = prev.NormalizedGrid.ToArray();
                characters[i] = prev.Character;
                rawGrids[i] = gridCopy;
                hProjs[i] = prev.HorizontalProjection.ToArray();
                vProjs[i] = prev.VerticalProjection.ToArray();
                quadDensities[i] = prev.QuadrantDensities.ToArray();
                gridCopy.AsSpan().CopyTo(allGrids.AsSpan(i * dim, dim));
            }
        }

        for (int j = 0; j < newCount; j++)
        {
            int dstIndex = existingCount + j;
            GoldenCharSample s = newSamples[j];
            float[] grid = new float[dim];
            float[] hProj = new float[IndustrialOcrGlyphTemplate.GridHeight];
            float[] vProj = new float[IndustrialOcrGlyphTemplate.GridWidth];
            float[] quads = new float[IndustrialOcrGlyphTemplate.QuadrantCount];

            ExtractNormalizedGlyphFeatures(
                new ReadOnlyImageView2D<byte>(s.Pixels, s.Width, s.Height, s.Stride),
                s.DarkOnLight,
                grid,
                hProj,
                vProj,
                quads,
                out _);

            characters[dstIndex] = s.Character;
            rawGrids[dstIndex] = grid;
            hProjs[dstIndex] = hProj;
            vProjs[dstIndex] = vProj;
            quadDensities[dstIndex] = quads;
            grid.AsSpan().CopyTo(allGrids.AsSpan(dstIndex * dim, dim));
        }

        float[] globalMean = new float[dim];
        SimpleShotNormalizer.ComputeGlobalMean(allGrids, totalCount, dim, globalMean);

        var templates = new IndustrialOcrGlyphTemplate[totalCount];
        for (int i = 0; i < totalCount; i++)
        {
            float[] cl2n = new float[dim];
            SimpleShotNormalizer.CenterAndL2Normalize(rawGrids[i], globalMean, cl2n);
            templates[i] = new IndustrialOcrGlyphTemplate(
                characters[i],
                rawGrids[i],
                cl2n,
                hProjs[i],
                vProjs[i],
                quadDensities[i]);
        }

        bool resolvedPolarity = newCount > 0
            ? newSamples[0].DarkOnLight
            : (existingModel?.DarkOnLight ?? darkOnLight);
        return new IndustrialOcrFontModel(templates, globalMean, resolvedPolarity);
    }

    /// <summary>
    /// Resamples an arbitrary character patch onto the canonical $16 \times 24$ glyph grid,
    /// normalizes foreground ink response to $[0, 1]$, and extracts 1D projections and $4 \times 4$ quadrant densities.
    /// </summary>
    public static void ExtractNormalizedGlyphFeatures(
        ReadOnlyImageView2D<byte> charPatch,
        bool darkOnLight,
        Span<float> normalizedGridOut,
        Span<float> horizontalProjOut,
        Span<float> verticalProjOut,
        Span<float> quadrantDensitiesOut,
        out float contrastRange)
    {
        charPatch.EnsureValid();
        const int gw = IndustrialOcrGlyphTemplate.GridWidth;
        const int gh = IndustrialOcrGlyphTemplate.GridHeight;

        float minVal = PixelMaxIntensity;
        float maxVal = 0f;

        for (int gy = 0; gy < gh; gy++)
        {
            float wy = ((gy + PixelCenterOffset) / gh) * charPatch.Height;
            for (int gx = 0; gx < gw; gx++)
            {
                float wx = ((gx + PixelCenterOffset) / gw) * charPatch.Width;
                float sample = SubpixelInterpolator.SampleBilinear(charPatch, wx, wy);
                float ink = darkOnLight ? (PixelMaxIntensity - sample) : sample;
                normalizedGridOut[(gy * gw) + gx] = ink;
                if (ink < minVal)
                {
                    minVal = ink;
                }

                if (ink > maxVal)
                {
                    maxVal = ink;
                }
            }
        }

        contrastRange = MathF.Max(0f, maxVal - minVal);
        float invRange = contrastRange > MinStdDevFloor ? (1.0f / contrastRange) : 0.0f;

        horizontalProjOut.Clear();
        verticalProjOut.Clear();
        quadrantDensitiesOut.Clear();

        int cellW = gw / IndustrialOcrGlyphTemplate.QuadrantCols;
        int cellH = gh / IndustrialOcrGlyphTemplate.QuadrantRows;
        float invCellArea = 1.0f / (cellW * cellH);

        for (int gy = 0; gy < gh; gy++)
        {
            int qRow = Math.Min(IndustrialOcrGlyphTemplate.QuadrantRows - 1, gy / cellH);
            float rowSum = 0f;
            for (int gx = 0; gx < gw; gx++)
            {
                int idx = (gy * gw) + gx;
                float norm = Math.Clamp((normalizedGridOut[idx] - minVal) * invRange, 0f, 1f);
                normalizedGridOut[idx] = norm;
                rowSum += norm;
                verticalProjOut[gx] += norm;

                if (norm >= StrokeActivationThreshold)
                {
                    int qCol = Math.Min(IndustrialOcrGlyphTemplate.QuadrantCols - 1, gx / cellW);
                    quadrantDensitiesOut[(qRow * IndustrialOcrGlyphTemplate.QuadrantCols) + qCol] += norm * invCellArea;
                }
            }

            horizontalProjOut[gy] = rowSum / gw;
        }

        for (int gx = 0; gx < gw; gx++)
        {
            verticalProjOut[gx] /= gh;
        }
    }
}
