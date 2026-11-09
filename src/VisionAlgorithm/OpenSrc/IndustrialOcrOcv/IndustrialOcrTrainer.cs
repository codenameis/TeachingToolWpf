// File: src/VisionAlgorithm/OpenSrc/IndustrialOcrOcv/IndustrialOcrTrainer.cs
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.IndustrialOcrOcv;

/// <summary>
/// Golden character training sample pairing a character label with its cropped grayscale patch.
/// </summary>
public readonly record struct GoldenCharSample(
    char Character,
    byte[] Pixels,
    int Width,
    int Height,
    int Stride,
    bool DarkOnLight = true);

/// <summary>
/// Builds an <see cref="IndustrialOcrFontModel"/> from golden character ROIs or procedural stroke definitions,
/// integrating <see cref="SimpleShotNormalizer"/> for centered L2 normalization (CL2N) and precomputing
/// horizontal/vertical projection profiles and $4 \times 4$ quadrant stroke coverage densities.
/// </summary>
public static class IndustrialOcrTrainer
{
    private const float StrokeActivationThreshold = 0.32f;
    private const float MinStdDevFloor = 1e-4f;

    /// <summary>
    /// Trains an <see cref="IndustrialOcrFontModel"/> from a list of golden character image samples.
    /// </summary>
    public static IndustrialOcrFontModel Train(ReadOnlySpan<GoldenCharSample> samples)
    {
        if (samples.IsEmpty)
        {
            throw new ArgumentException("At least one golden character sample is required.", nameof(samples));
        }

        int count = samples.Length;
        int dim = IndustrialOcrGlyphTemplate.GridLength;
        float[] allGrids = new float[count * dim];
        float[][] rawGrids = new float[count][];
        float[][] hProjs = new float[count][];
        float[][] vProjs = new float[count][];
        float[][] quadDensities = new float[count][];

        for (int i = 0; i < count; i++)
        {
            GoldenCharSample s = samples[i];
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

            rawGrids[i] = grid;
            hProjs[i] = hProj;
            vProjs[i] = vProj;
            quadDensities[i] = quads;
            grid.AsSpan().CopyTo(allGrids.AsSpan(i * dim, dim));
        }

        float[] globalMean = new float[dim];
        SimpleShotNormalizer.ComputeGlobalMean(allGrids, count, dim, globalMean);

        var templates = new IndustrialOcrGlyphTemplate[count];
        for (int i = 0; i < count; i++)
        {
            float[] cl2n = new float[dim];
            SimpleShotNormalizer.CenterAndL2Normalize(rawGrids[i], globalMean, cl2n);
            templates[i] = new IndustrialOcrGlyphTemplate(
                samples[i].Character,
                rawGrids[i],
                cl2n,
                hProjs[i],
                vProjs[i],
                quadDensities[i]);
        }

        return new IndustrialOcrFontModel(templates, globalMean);
    }

    /// <summary>
    /// Resamples an arbitrary character patch onto the canonical $16 \times 24$ glyph grid using
    /// <see cref="SubpixelInterpolator.SampleBilinearPixelCenter"/>, normalizes foreground ink response to $[0, 1]$,
    /// and extracts 1D projections and $4 \times 4$ quadrant stroke coverage ratios.
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

        float minVal = 255f;
        float maxVal = 0f;

        for (int gy = 0; gy < gh; gy++)
        {
            float wy = ((gy + 0.5f) / gh) * charPatch.Height;
            for (int gx = 0; gx < gw; gx++)
            {
                float wx = ((gx + 0.5f) / gw) * charPatch.Width;
                float sample = SubpixelInterpolator.SampleBilinear(charPatch, wx, wy);
                float ink = darkOnLight ? (255f - sample) : sample;
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

        int cellW = gw / IndustrialOcrGlyphTemplate.QuadrantCols; // 4
        int cellH = gh / IndustrialOcrGlyphTemplate.QuadrantRows; // 6
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
