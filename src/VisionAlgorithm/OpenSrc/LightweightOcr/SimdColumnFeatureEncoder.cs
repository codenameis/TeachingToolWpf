// File: src/VisionAlgorithm/OpenSrc/LightweightOcr/SimdColumnFeatureEncoder.cs
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.LightweightOcr;

/// <summary>
/// AVX2-accelerated 1-pass column sequence encoder that normalizes a rectified text strip (height $32\text{ px}$),
/// suppresses uneven illumination gradients, and computes per-timestep $[T \times \text{NumClasses}]$ CTC logits.
/// </summary>
public static class SimdColumnFeatureEncoder
{
    private const int Avx2LaneCount = 8;
    private const int IlluminationBlockWidth = 24;
    private const float ValleyBlankLogitBoost = 8.5f;
    private const float SpaceLogitBoost = 9.0f;
    private const float ActiveCharBlankLogit = -3.0f;
    private const float LogitScaleFactor = 10.0f;

    /// <summary>
    /// Extracts the $D = 424$ normalized feature vector for a single character patch or column window.
    /// </summary>
    public static void ExtractWindowFeature(
        ReadOnlyImageView2D<byte> patch,
        bool darkOnLight,
        Span<float> rawFeatureOut)
    {
        patch.EnsureValid();
        const int gw = LightweightSequenceModel.FeatureCols;
        const int gh = LightweightSequenceModel.FeatureRows;
        const int spatialLen = LightweightSequenceModel.SpatialFeatureLength;

        Span<float> grid = rawFeatureOut.Slice(0, spatialLen);
        Span<float> hProj = rawFeatureOut.Slice(spatialLen, gh);
        Span<float> vProj = rawFeatureOut.Slice(spatialLen + gh, gw);

        float minVal = 255f;
        float maxVal = 0f;

        for (int gy = 0; gy < gh; gy++)
        {
            float wy = ((gy + 0.5f) / gh) * patch.Height;
            for (int gx = 0; gx < gw; gx++)
            {
                float wx = ((gx + 0.5f) / gw) * patch.Width;
                float sample = SubpixelInterpolator.SampleBilinear(patch, wx, wy);
                float ink = darkOnLight ? (255f - sample) : sample;
                grid[(gy * gw) + gx] = ink;
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

        float range = MathF.Max(1e-4f, maxVal - minVal);
        float invRange = 1.0f / range;
        hProj.Clear();
        vProj.Clear();

        for (int gy = 0; gy < gh; gy++)
        {
            float rowSum = 0f;
            for (int gx = 0; gx < gw; gx++)
            {
                int idx = (gy * gw) + gx;
                float norm = Math.Clamp((grid[idx] - minVal) * invRange, 0f, 1f);
                grid[idx] = norm;
                rowSum += norm;
                vProj[gx] += norm;
            }

            hProj[gy] = (rowSum / gw) * 1.5f;
        }

        for (int gx = 0; gx < gw; gx++)
        {
            vProj[gx] = (vProj[gx] / gh) * 1.5f;
        }
    }

    /// <summary>
    /// Encodes a rectified text strip into a $[T \times \text{NumClasses}]$ CTC logit matrix in a single horizontal pass.
    /// </summary>
    public static int EncodeStripToLogits(
        ReadOnlyImageView2D<byte> rawStrip,
        LightweightSequenceModel model,
        in LightweightOcrParams ocrParams,
        VisionWorkspace workspace,
        Span<float> logitsOut)
    {
        rawStrip.EnsureValid();
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(workspace);

        int w = rawStrip.Width;
        int h = rawStrip.Height;
        int strideCol = Math.Max(1, ocrParams.ColumnStride);
        int timeSteps = w / strideCol;
        int numClasses = model.NumClasses;

        if (logitsOut.Length < checked(timeSteps * numClasses))
        {
            throw new ArgumentException("logitsOut buffer is smaller than timeSteps * numClasses.", nameof(logitsOut));
        }

        // Step 1: Local block illumination detrending so uneven lighting gradients do not shift ink thresholds
        Span<byte> cleanStrip = workspace.GetByteBufferB(checked(w * h));
        DetrendStripIllumination(rawStrip, ocrParams.DarkOnLight, ocrParams.DetrendIllumination, cleanStrip);

        // Step 2: Vertical ink profile & tight vertical row bounds
        Span<int> colInkHits = stackalloc int[w];
        Span<int> rowInkHits = stackalloc int[h];
        colInkHits.Clear();
        rowInkHits.Clear();

        for (int r = 0; r < h; r++)
        {
            int rowOff = r * w;
            for (int c = 0; c < w; c++)
            {
                if (cleanStrip[rowOff + c] < 168)
                {
                    colInkHits[c]++;
                    rowInkHits[r]++;
                }
            }
        }

        int topRow = 0;
        int bottomRow = h - 1;
        while (topRow < h && rowInkHits[topRow] <= 1)
        {
            topRow++;
        }

        while (bottomRow > topRow && rowInkHits[bottomRow] <= 1)
        {
            bottomRow--;
        }

        if (topRow >= bottomRow)
        {
            // Entire strip is empty -> emit all Blank tokens
            for (int t = 0; t < timeSteps; t++)
            {
                Span<float> stepLogits = logitsOut.Slice(t * numClasses, numClasses);
                stepLogits.Clear();
                stepLogits[LightweightSequenceModel.BlankTokenIndex] = ValleyBlankLogitBoost;
            }

            return timeSteps;
        }

        // Identify contiguous character column spans and inter-character valleys (including tight kerning valleys)
        Span<int> spanStart = stackalloc int[128];
        Span<int> spanEnd = stackalloc int[128];
        int spanCount = ExtractColumnSpansWithTightValleySplit(colInkHits, bottomRow - topRow + 1, spanStart, spanEnd);

        // Estimate median character width to detect inter-word spaces
        int sumCharW = 0;
        for (int s = 0; s < spanCount; s++)
        {
            sumCharW += (spanEnd[s] - spanStart[s]) + 1;
        }

        float avgCharW = spanCount > 0 ? (float)sumCharW / spanCount : 14f;
        float spaceGapThreshold = MathF.Max(14.0f, avgCharW * 1.15f);
        int spaceClassIdx = model.IndexOfChar(' ');

        // Default all timesteps to Blank (index 0)
        for (int t = 0; t < timeSteps; t++)
        {
            Span<float> stepLogits = logitsOut.Slice(t * numClasses, numClasses);
            stepLogits.Fill(-2.0f);
            stepLogits[LightweightSequenceModel.BlankTokenIndex] = ValleyBlankLogitBoost;
        }

        Span<float> rawFeat = stackalloc float[LightweightSequenceModel.FeatureDimension];
        Span<float> cl2nFeat = stackalloc float[LightweightSequenceModel.FeatureDimension];
        Span<float> spanClassLogits = stackalloc float[numClasses];

        for (int s = 0; s < spanCount; s++)
        {
            int cs = spanStart[s];
            int ce = spanEnd[s];

            // Refine vertical bounds for this character span
            int cTop = topRow;
            int cBot = bottomRow;
            while (cTop < cBot && !HasRowInk(cleanStrip, w, cTop, cs, ce))
            {
                cTop++;
            }

            while (cBot > cTop && !HasRowInk(cleanStrip, w, cBot, cs, ce))
            {
                cBot--;
            }

            ReadOnlySpan<byte> patchSlice = cleanStrip.Slice((cTop * w) + cs);
            ReadOnlyImageView2D<byte> patchView = new(patchSlice, (ce - cs) + 1, (cBot - cTop) + 1, w);

            ExtractWindowFeature(patchView, darkOnLight: true, rawFeat);
            SimpleShotNormalizer.CenterAndL2Normalize(rawFeat, model.GlobalMean, cl2nFeat);

            spanClassLogits[LightweightSequenceModel.BlankTokenIndex] = ActiveCharBlankLogit;
            for (int c = 1; c < numClasses; c++)
            {
                float dot = ComputeDotAvx2(cl2nFeat, model.GetClassWeights(c));
                spanClassLogits[c] = dot * LogitScaleFactor;
            }

            // Assign character logits to the interior timesteps of this span, leaving at least 1 Blank timestep between spans
            int tStart = Math.Clamp((cs + 1) / strideCol, 0, timeSteps - 1);
            int tEnd = Math.Clamp((ce - 1) / strideCol, tStart, timeSteps - 1);
            if (s > 0)
            {
                int prevTEnd = Math.Clamp((spanEnd[s - 1] - 1) / strideCol, 0, timeSteps - 1);
                if (tStart <= prevTEnd + 1)
                {
                    tStart = Math.Min(timeSteps - 1, prevTEnd + 2);
                    tEnd = Math.Max(tStart, tEnd);
                }

                // If the gap before this span is a word space, emit the Space token in the gap with Blank separators
                int gapPixels = cs - spanEnd[s - 1] - 1;
                int centerDist = ((cs + ce) / 2) - ((spanStart[s - 1] + spanEnd[s - 1]) / 2);
                if (spaceClassIdx > 0 && gapPixels >= spaceGapThreshold && centerDist >= avgCharW * 1.85f)
                {
                    int tSpace = (prevTEnd + tStart) / 2;
                    if (tSpace > prevTEnd && tSpace < tStart)
                    {
                        Span<float> spaceStep = logitsOut.Slice(tSpace * numClasses, numClasses);
                        spaceStep.Fill(-2.0f);
                        spaceStep[LightweightSequenceModel.BlankTokenIndex] = ActiveCharBlankLogit;
                        spaceStep[spaceClassIdx] = SpaceLogitBoost;
                    }
                }
            }

            for (int t = tStart; t <= tEnd; t++)
            {
                spanClassLogits.CopyTo(logitsOut.Slice(t * numClasses, numClasses));
            }
        }

        return timeSteps;
    }

    private static bool HasRowInk(ReadOnlySpan<byte> cleanStrip, int stride, int row, int startCol, int endCol)
    {
        int off = row * stride;
        for (int c = startCol; c <= endCol; c++)
        {
            if (cleanStrip[off + c] < 168)
            {
                return true;
            }
        }

        return false;
    }

    private static void DetrendStripIllumination(
        ReadOnlyImageView2D<byte> rawStrip,
        bool darkOnLight,
        bool detrend,
        Span<byte> normalizedOut)
    {
        int w = rawStrip.Width;
        int h = rawStrip.Height;

        if (!detrend)
        {
            for (int r = 0; r < h; r++)
            {
                ReadOnlySpan<byte> srcRow = rawStrip.RowSpan(r);
                Span<byte> dstRow = normalizedOut.Slice(r * w, w);
                for (int c = 0; c < w; c++)
                {
                    dstRow[c] = darkOnLight ? srcRow[c] : (byte)(255 - srcRow[c]);
                }
            }

            return;
        }

        // Estimate per-column background envelope from top and bottom quiet rows (3x3 median smoothed)
        Span<float> colBackground = stackalloc float[w];
        for (int c = 0; c < w; c++)
        {
            int c0 = Math.Max(0, c - IlluminationBlockWidth / 2);
            int c1 = Math.Min(w - 1, c + IlluminationBlockWidth / 2);
            float maxVal = 0f;

            for (int r = 0; r < h; r += 2)
            {
                ReadOnlySpan<byte> row = rawStrip.RowSpan(r);
                for (int x = c0; x <= c1; x += 4)
                {
                    float v = darkOnLight ? row[x] : (255 - row[x]);
                    if (v > maxVal)
                    {
                        maxVal = v;
                    }
                }
            }

            colBackground[c] = MathF.Max(60f, maxVal);
        }

        for (int r = 0; r < h; r++)
        {
            ReadOnlySpan<byte> srcRow = rawStrip.RowSpan(r);
            Span<byte> dstRow = normalizedOut.Slice(r * w, w);
            for (int c = 0; c < w; c++)
            {
                float v = darkOnLight ? srcRow[c] : (255 - srcRow[c]);
                float norm = (v / colBackground[c]) * 235f;
                dstRow[c] = (byte)Math.Clamp((int)MathF.Round(norm), 0, 255);
            }
        }
    }

    private static int ExtractColumnSpansWithTightValleySplit(
        ReadOnlySpan<int> colInkHits,
        int lineHeight,
        Span<int> spanStart,
        Span<int> spanEnd)
    {
        int w = colInkHits.Length;
        int minHits = 1;
        int count = 0;
        int c = 0;

        while (c < w && count < spanStart.Length)
        {
            while (c < w && colInkHits[c] < minHits)
            {
                c++;
            }

            if (c >= w)
            {
                break;
            }

            int s = c;
            while (c < w && colInkHits[c] >= minHits)
            {
                c++;
            }

            int e = c - 1;
            int spanW = (e - s) + 1;
            if (spanW < 3)
            {
                continue;
            }

            // If a span is wider than a single character due to tight kerning, split at deep local valleys
            int maxSingleW = Math.Max(18, (int)MathF.Round(lineHeight * 0.88f));
            int expectedPitch = Math.Max(10, (int)MathF.Round(lineHeight * 0.62f));
            int curS = s;

            while (((e - curS) + 1) > maxSingleW && count < spanStart.Length - 1)
            {
                int targetSplit = curS + expectedPitch - 1;
                int searchMin = Math.Max(curS + 5, targetSplit - 5);
                int searchMax = Math.Min(e - 5, targetSplit + 5);
                int bestCol = Math.Clamp(targetSplit, curS + 5, e - 5);
                int minV = int.MaxValue;

                for (int k = searchMin; k <= searchMax; k++)
                {
                    if (colInkHits[k] < minV)
                    {
                        minV = colInkHits[k];
                        bestCol = k;
                    }
                }

                spanStart[count] = curS;
                spanEnd[count] = bestCol;
                count++;
                curS = bestCol + 1;
            }

            if (curS <= e && count < spanStart.Length)
            {
                spanStart[count] = curS;
                spanEnd[count] = e;
                count++;
            }
        }

        return count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ComputeDotAvx2(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int len = Math.Min(a.Length, b.Length);
        int i = 0;
        float sum = 0f;

        if (Avx2.IsSupported && len >= Avx2LaneCount)
        {
            Vector256<float> acc = Vector256<float>.Zero;
            ref float aRef = ref MemoryMarshal.GetReference(a);
            ref float bRef = ref MemoryMarshal.GetReference(b);
            int simdLimit = len - (len % Avx2LaneCount);

            for (; i < simdLimit; i += Avx2LaneCount)
            {
                Vector256<float> va = Vector256.LoadUnsafe(ref Unsafe.Add(ref aRef, i));
                Vector256<float> vb = Vector256.LoadUnsafe(ref Unsafe.Add(ref bRef, i));
                acc = Avx.Add(acc, Avx.Multiply(va, vb));
            }

            Vector128<float> low = acc.GetLower();
            Vector128<float> high = acc.GetUpper();
            Vector128<float> sum128 = Sse.Add(low, high);
            Vector128<float> shuf = Sse.Shuffle(sum128, sum128, 0b_01_00_11_10);
            sum128 = Sse.Add(sum128, shuf);
            shuf = Sse.Shuffle(sum128, sum128, 0b_00_00_00_01);
            sum = Sse.AddScalar(sum128, shuf).ToScalar();
        }

        for (; i < len; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }
}
