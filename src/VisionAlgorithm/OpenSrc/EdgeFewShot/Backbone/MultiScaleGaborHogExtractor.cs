// File: src/VisionAlgorithm/OpenSrc/EdgeFewShot/Backbone/MultiScaleGaborHogExtractor.cs
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace VisionAlgorithm.OpenSrc.EdgeFewShot.Backbone;

/// <summary>
/// Zero-dependency mid-layer feature map extractor combining fine-scale (<c>Layer 2</c>, 4x4 cell stride, 16 channels)
/// and coarse-scale (<c>Layer 3</c>, 8x8 cell stride, 16 channels) oriented Sobel/Gabor-like gradient, Laplacian,
/// and quadrant-sensitive directional texture responses. Operates in 0-byte heap allocation using caller-provided scratch spans.
/// </summary>
public sealed class MultiScaleGaborHogExtractor : IFeatureMapExtractor
{
    public const int DefaultLayer2CellSize = 4;
    public const int DefaultLayer3CellSize = 8;
    public const int ChannelsPerScale = 16;

    private const int OrientationBinCount = 8;
    private const int GradientMagChannel = 8;
    private const int PositiveEnergyChannel = 9;
    private const int NegativeEnergyChannel = 10;
    private const int LaplacianEnergyChannel = 11;
    private const int HorizontalRippleChannel = 12;
    private const int VerticalRippleChannel = 13;
    private const int DiagonalCheckerEnergyChannel = 14;
    private const int ExcessPeakEnergyChannel = 15;

    // [Math Why]: Maps unsigned orientation [0, pi) into 8 discrete orientation bins (8 / pi).
    private const float RadianToBinScale = OrientationBinCount / MathF.PI;
    private const float GradientMagnitudeEpsilon = 1e-6f;
    private const float CenterSuppressionThreshold = 0.25f;
    private const float HighDeviationThreshold = 0.85f;

    public int Layer2Channels => ChannelsPerScale;

    public int Layer3Channels => ChannelsPerScale;

    public int TotalConcatenatedChannels => Layer2Channels + Layer3Channels;

    public (int GridWidth, int GridHeight) GetLayer2Dimensions(int roiWidth, int roiHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(roiWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(roiHeight);
        return (System.Math.Max(1, roiWidth / DefaultLayer2CellSize), System.Math.Max(1, roiHeight / DefaultLayer2CellSize));
    }

    public (int GridWidth, int GridHeight) GetLayer3Dimensions(int roiWidth, int roiHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(roiWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(roiHeight);
        return (System.Math.Max(1, roiWidth / DefaultLayer3CellSize), System.Math.Max(1, roiHeight / DefaultLayer3CellSize));
    }

    public int GetRequiredScratchFloats(int roiWidth, int roiHeight)
    {
        var (w2, h2) = GetLayer2Dimensions(roiWidth, roiHeight);
        return checked(w2 * h2);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void ExtractMidLayerMaps(
        ReadOnlySpan<float> normalizedRoi,
        int roiWidth,
        int roiHeight,
        Span<float> layer2Out,
        Span<float> layer3Out,
        Span<float> scratchBuffer)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(roiWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(roiHeight);
        var requiredRoiFloats = checked(roiWidth * roiHeight);
        if (normalizedRoi.Length < requiredRoiFloats)
        {
            throw new ArgumentException("normalizedRoi span is smaller than roiWidth * roiHeight.", nameof(normalizedRoi));
        }

        var (w2, h2) = GetLayer2Dimensions(roiWidth, roiHeight);
        var (w3, h3) = GetLayer3Dimensions(roiWidth, roiHeight);
        var l2Total = checked(Layer2Channels * w2 * h2);
        var l3Total = checked(Layer3Channels * w3 * h3);

        if (layer2Out.Length < l2Total)
        {
            throw new ArgumentException("layer2Out span is smaller than required Layer 2 elements.", nameof(layer2Out));
        }

        if (layer3Out.Length < l3Total)
        {
            throw new ArgumentException("layer3Out span is smaller than required Layer 3 elements.", nameof(layer3Out));
        }

        ComputeScaleFeatureMap(normalizedRoi, roiWidth, roiHeight, w2, h2, layer2Out.Slice(0, l2Total));
        ComputeScaleFeatureMap(normalizedRoi, roiWidth, roiHeight, w3, h3, layer3Out.Slice(0, l3Total));
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ComputeScaleFeatureMap(
        ReadOnlySpan<float> roi,
        int roiWidth,
        int roiHeight,
        int gridWidth,
        int gridHeight,
        Span<float> mapOut)
    {
        mapOut.Clear();
        var planeSize = gridWidth * gridHeight;
        var maxRoiX = roiWidth - 1;
        var maxRoiY = roiHeight - 1;

        ref var roiBase = ref MemoryMarshal.GetReference(roi);

        for (var gy = 0; gy < gridHeight; gy++)
        {
            var yStart = (gy * roiHeight) / gridHeight;
            var yEnd = System.Math.Max(yStart + 1, ((gy + 1) * roiHeight) / gridHeight);

            for (var gx = 0; gx < gridWidth; gx++)
            {
                var xStart = (gx * roiWidth) / gridWidth;
                var xEnd = System.Math.Max(xStart + 1, ((gx + 1) * roiWidth) / gridWidth);
                var cellIndex = (gy * gridWidth) + gx;
                var invCellArea = 1f / ((xEnd - xStart) * (yEnd - yStart));

                for (var y = yStart; y < yEnd; y++)
                {
                    var yPrev = y > 0 ? y - 1 : 0;
                    var yNext = y < maxRoiY ? y + 1 : maxRoiY;
                    var rowPrev = yPrev * roiWidth;
                    var rowCurr = y * roiWidth;
                    var rowNext = yNext * roiWidth;

                    for (var x = xStart; x < xEnd; x++)
                    {
                        var xPrev = x > 0 ? x - 1 : 0;
                        var xNext = x < maxRoiX ? x + 1 : maxRoiX;

                        var center = Unsafe.Add(ref roiBase, rowCurr + x);
                        var left = Unsafe.Add(ref roiBase, rowCurr + xPrev);
                        var right = Unsafe.Add(ref roiBase, rowCurr + xNext);
                        var up = Unsafe.Add(ref roiBase, rowPrev + x);
                        var down = Unsafe.Add(ref roiBase, rowNext + x);
                        var upLeft = Unsafe.Add(ref roiBase, rowPrev + xPrev);
                        var upRight = Unsafe.Add(ref roiBase, rowPrev + xNext);
                        var downLeft = Unsafe.Add(ref roiBase, rowNext + xPrev);
                        var downRight = Unsafe.Add(ref roiBase, rowNext + xNext);

                        var dx = (right - left) * 0.5f;
                        var dy = (down - up) * 0.5f;
                        var gradMag = MathF.Sqrt((dx * dx) + (dy * dy));

                        if (gradMag > GradientMagnitudeEpsilon)
                        {
                            var angle = MathF.Atan2(dy, dx);
                            if (angle < 0f)
                            {
                                angle += MathF.PI;
                            }

                            var binFloat = angle * RadianToBinScale;
                            var bin0 = ((int)binFloat) & (OrientationBinCount - 1);
                            var bin1 = (bin0 + 1) & (OrientationBinCount - 1);
                            var frac = binFloat - MathF.Floor(binFloat);

                            mapOut[(bin0 * planeSize) + cellIndex] += gradMag * (1f - frac) * invCellArea;
                            mapOut[(bin1 * planeSize) + cellIndex] += gradMag * frac * invCellArea;
                        }

                        var posDeviation = MathF.Max(0f, center - CenterSuppressionThreshold);
                        var negDeviation = MathF.Max(0f, -center - CenterSuppressionThreshold);
                        var laplacian = MathF.Abs((left + right + up + down) - (center * 4f));
                        var hRipple = MathF.Abs((left + right) - (center * 2f));
                        var vRipple = MathF.Abs((up + down) - (center * 2f));
                        var diagonalCross = MathF.Max(0f, MathF.Abs((upLeft + downRight) - (upRight + downLeft)) - (gradMag * 0.75f));
                        var excessPeak = MathF.Max(0f, MathF.Max(posDeviation, negDeviation) - HighDeviationThreshold);

                        mapOut[(GradientMagChannel * planeSize) + cellIndex] += gradMag * invCellArea;
                        mapOut[(PositiveEnergyChannel * planeSize) + cellIndex] += posDeviation * invCellArea;
                        mapOut[(NegativeEnergyChannel * planeSize) + cellIndex] += negDeviation * invCellArea;
                        mapOut[(LaplacianEnergyChannel * planeSize) + cellIndex] += laplacian * invCellArea;
                        mapOut[(HorizontalRippleChannel * planeSize) + cellIndex] += hRipple * invCellArea;
                        mapOut[(VerticalRippleChannel * planeSize) + cellIndex] += vRipple * invCellArea;
                        mapOut[(DiagonalCheckerEnergyChannel * planeSize) + cellIndex] += diagonalCross * invCellArea;
                        mapOut[(ExcessPeakEnergyChannel * planeSize) + cellIndex] += excessPeak * invCellArea;
                    }
                }
            }
        }
    }
}
