// File: src/VisionAlgorithm/OpenSrc/LightweightOcr/FastRleTextLineDetector.cs
using VisionAlgorithm.OpenSrc.BlobRle;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.LightweightOcr;

/// <summary>
/// High-speed $O(N_{\text{runs}})$ text-line bounding box detector combining horizontal gradient contrast enhancement
/// with <see cref="RleRegion.Threshold"/>, <see cref="RleMorphology.ClosingBox"/>, and <see cref="RleBlobAnalyzer.AnalyzeComponents"/>.
/// </summary>
public static class FastRleTextLineDetector
{
    private const byte DefaultGradientThreshold = 28;
    private const int DefaultClosingRadiusX = 14;
    private const int DefaultClosingRadiusY = 3;
    private const int MinLineWidthPixels = 48;
    private const int MinLineHeightPixels = 10;
    private const int MaxLineHeightPixels = 120;
    private const float MinAspectRatio = 2.2f;
    private const int BoxPaddingX = 10;
    private const int BoxPaddingY = 5;

    /// <summary>
    /// Detects horizontal or slightly tilted text-line boxes in <paramref name="image"/> and writes them sorted top-to-bottom into <paramref name="linesOut"/>.
    /// </summary>
    public static int DetectTextLines(
        ReadOnlyImageView2D<byte> image,
        VisionWorkspace workspace,
        Span<TextLineBox> linesOut,
        byte gradientThreshold = DefaultGradientThreshold,
        int closingRadiusX = DefaultClosingRadiusX,
        int closingRadiusY = DefaultClosingRadiusY)
    {
        image.EnsureValid();
        ArgumentNullException.ThrowIfNull(workspace);

        int w = image.Width;
        int h = image.Height;
        Span<byte> gradMap = workspace.GetByteBufferA(checked(w * h), clear: true);

        // Compute horizontal Sobel/central difference magnitude |I(x+1, y) - I(x-1, y)| + 0.5 * |I(x, y+1) - I(x, y-1)|
        for (int y = 1; y < h - 1; y++)
        {
            ReadOnlySpan<byte> rowPrev = image.RowSpan(y - 1);
            ReadOnlySpan<byte> rowCur = image.RowSpan(y);
            ReadOnlySpan<byte> rowNext = image.RowSpan(y + 1);
            Span<byte> dstRow = gradMap.Slice(y * w, w);

            for (int x = 1; x < w - 1; x++)
            {
                int gx = Math.Abs(rowCur[x + 1] - rowCur[x - 1]);
                int gy = Math.Abs(rowNext[x] - rowPrev[x]) >> 1;
                int mag = Math.Min(255, gx + gy);
                dstRow[x] = (byte)mag;
            }
        }

        RleRegion rawRegion = RleRegion.Threshold(gradMap, w, h, w, gradientThreshold);
        RleRegion closedRegion = RleMorphology.ClosingBox(rawRegion, closingRadiusX, closingRadiusY);
        List<BlobComponentFeature> blobs = RleBlobAnalyzer.AnalyzeComponents(
            closedRegion,
            minimumAreaPixels: MinLineWidthPixels * MinLineHeightPixels / 2,
            workspace);

        // Filter valid text-line blobs and sort top-to-bottom by centroid Y
        Span<int> validIndices = stackalloc int[Math.Min(64, blobs.Count)];
        int validCount = 0;

        for (int i = 0; i < blobs.Count && validCount < validIndices.Length; i++)
        {
            BlobComponentFeature b = blobs[i];
            int bw = b.BoundingWidth;
            int bh = b.BoundingHeight;
            float aspect = (float)bw / Math.Max(1, bh);

            if (bw >= MinLineWidthPixels && bh >= MinLineHeightPixels && bh <= MaxLineHeightPixels && aspect >= MinAspectRatio)
            {
                validIndices[validCount++] = i;
            }
        }

        // Insertion sort by Centroid.Y ascending
        for (int i = 1; i < validCount; i++)
        {
            int keyIdx = validIndices[i];
            float keyY = blobs[keyIdx].Centroid.Y;
            int j = i - 1;
            while (j >= 0 && blobs[validIndices[j]].Centroid.Y > keyY)
            {
                validIndices[j + 1] = validIndices[j];
                j--;
            }

            validIndices[j + 1] = keyIdx;
        }

        int outCount = Math.Min(validCount, linesOut.Length);
        for (int i = 0; i < outCount; i++)
        {
            BlobComponentFeature b = blobs[validIndices[i]];
            int minCol = Math.Max(0, b.MinCol - BoxPaddingX);
            int maxCol = Math.Min(w - 1, b.MaxCol + BoxPaddingX);
            int minRow = Math.Max(0, b.MinRow - BoxPaddingY);
            int maxRow = Math.Min(h - 1, b.MaxRow + BoxPaddingY);
            int boxW = (maxCol - minCol) + 1;
            int boxH = (maxRow - minRow) + 1;

            float cx = (minCol + maxCol + 1) * 0.5f;
            float cy = (minRow + maxRow + 1) * 0.5f;
            float angle = MathF.Abs(b.PrincipalAxisAngleRad) <= 0.25f ? b.PrincipalAxisAngleRad : 0f;

            linesOut[i] = new TextLineBox(
                i,
                new FixturePose2D(cx, cy, angle, 1f),
                boxW,
                boxH,
                minCol,
                minRow,
                maxCol,
                maxRow,
                b.AreaPixels);
        }

        return outCount;
    }
}
