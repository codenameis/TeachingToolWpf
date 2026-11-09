// File: src/VisionAlgorithm/OpenSrc/BlobRle/RleMorphology.cs
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace VisionAlgorithm.OpenSrc.BlobRle;

/// <summary>
/// Interval-algebra morphological operators (Dilation, Erosion, Opening, Closing) operating directly on <see cref="RleRegion"/>
/// chords without iterating over background pixels.
/// </summary>
public static class RleMorphology
{
    // [Perf Why]: Row index tables up to 4096 rows (16 KB) fit on the stack without heap allocation.
    private const int MaxStackAllocRows = 4096;

    /// <summary>
    /// Dilates an <see cref="RleRegion"/> by a rectangular structuring element of half-widths $(radiusX, radiusY)$
    /// using a 2-stage separable $O(N_{\text{runs}})$ sort-free algorithm:
    /// Stage 1 expands and linearly merges same-row intervals into a single flat <see cref="List{T}"/>;
    /// Stage 2 merges sorted row slices across $[r - radiusY, r + radiusY]$ via two-pointer interval union.
    /// </summary>
    public static RleRegion DilateBox(RleRegion source, int radiusX, int radiusY)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(radiusX);
        ArgumentOutOfRangeException.ThrowIfNegative(radiusY);

        if (source.IsEmpty || (radiusX == 0 && radiusY == 0))
        {
            return source;
        }

        int width = source.BoundingWidth;
        int height = source.BoundingHeight;
        int maxCol = width - 1;
        int maxRow = height - 1;
        IReadOnlyList<PixelRun> srcRuns = source.Runs;

        // Stage 1: Horizontal dilation + linear same-row interval merging (no sorting, no per-row List allocations).
        List<PixelRun> horizontalRuns = new(srcRuns.Count);
        Span<int> rowStart = height <= MaxStackAllocRows ? stackalloc int[height] : new int[height];
        Span<int> rowCount = height <= MaxStackAllocRows ? stackalloc int[height] : new int[height];
        rowStart.Fill(-1);
        rowCount.Clear();

        int activeRow = -1;
        int curStart = 0;
        int curEnd = -1;

        for (int i = 0; i < srcRuns.Count; i++)
        {
            PixelRun run = srcRuns[i];
            int r = run.Row;
            int expandedStart = Math.Max(0, run.StartCol - radiusX);
            int expandedEnd = Math.Min(maxCol, run.EndCol + radiusX);

            if (r != activeRow)
            {
                if (activeRow >= 0)
                {
                    AppendHorizontalRun(horizontalRuns, rowStart, rowCount, activeRow, curStart, curEnd);
                }

                activeRow = r;
                curStart = expandedStart;
                curEnd = expandedEnd;
            }
            else if (expandedStart <= curEnd + 1)
            {
                if (expandedEnd > curEnd)
                {
                    curEnd = expandedEnd;
                }
            }
            else
            {
                AppendHorizontalRun(horizontalRuns, rowStart, rowCount, activeRow, curStart, curEnd);
                curStart = expandedStart;
                curEnd = expandedEnd;
            }
        }

        if (activeRow >= 0)
        {
            AppendHorizontalRun(horizontalRuns, rowStart, rowCount, activeRow, curStart, curEnd);
        }

        if (radiusY == 0 || horizontalRuns.Count == 0)
        {
            return new RleRegion(width, height, horizontalRuns);
        }

        // Stage 2: Vertical dilation across [r - radiusY, r + radiusY] via two-pointer sorted interval union.
        ReadOnlySpan<PixelRun> horizontalSpan = CollectionsMarshal.AsSpan(horizontalRuns);
        List<PixelRun> mergedRuns = new(horizontalRuns.Count * 2);
        List<PixelRun> currentUnion = [];
        List<PixelRun> nextUnion = [];

        for (int r = 0; r < height; r++)
        {
            int minSourceRow = Math.Max(0, r - radiusY);
            int maxSourceRow = Math.Min(maxRow, r + radiusY);
            currentUnion.Clear();

            for (int ky = minSourceRow; ky <= maxSourceRow; ky++)
            {
                int count = rowCount[ky];
                if (count == 0)
                {
                    continue;
                }

                ReadOnlySpan<PixelRun> kySlice = horizontalSpan.Slice(rowStart[ky], count);
                UnionSortedIntervalSpans(r, CollectionsMarshal.AsSpan(currentUnion), kySlice, nextUnion);
                (currentUnion, nextUnion) = (nextUnion, currentUnion);
            }

            for (int i = 0; i < currentUnion.Count; i++)
            {
                mergedRuns.Add(currentUnion[i]);
            }
        }

        return new RleRegion(width, height, mergedRuns);
    }

    /// <summary>
    /// Erodes an <see cref="RleRegion"/> by a rectangular structuring element of half-widths $(radiusX, radiusY)$
    /// using separable horizontal chord shrinkage and vertical interval intersection across $2 \cdot radiusY + 1$ rows.
    /// Uses a single flat <see cref="List{T}"/> plus row offset/count spans to avoid per-row list heap allocations.
    /// </summary>
    public static RleRegion ErodeBox(RleRegion source, int radiusX, int radiusY)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(radiusX);
        ArgumentOutOfRangeException.ThrowIfNegative(radiusY);

        if (source.IsEmpty || (radiusX == 0 && radiusY == 0))
        {
            return source;
        }

        int width = source.BoundingWidth;
        int height = source.BoundingHeight;
        IReadOnlyList<PixelRun> srcRuns = source.Runs;

        // Step 1: Horizontal erosion shrinks each chord by radiusX on both ends: [StartCol + radiusX, EndCol - radiusX]
        // Store all surviving runs contiguously in a single flat list and record per-row [rowStart, rowCount].
        List<PixelRun> erodedRuns = new(srcRuns.Count);
        Span<int> rowStart = height <= MaxStackAllocRows ? stackalloc int[height] : new int[height];
        Span<int> rowCount = height <= MaxStackAllocRows ? stackalloc int[height] : new int[height];
        rowStart.Fill(-1);
        rowCount.Clear();

        for (int i = 0; i < srcRuns.Count; i++)
        {
            PixelRun run = srcRuns[i];
            int start = run.StartCol + radiusX;
            int end = run.EndCol - radiusX;
            if (start <= end)
            {
                int r = run.Row;
                if (rowStart[r] < 0)
                {
                    rowStart[r] = erodedRuns.Count;
                }

                rowCount[r]++;
                erodedRuns.Add(new PixelRun(r, start, end));
            }
        }

        if (radiusY == 0 || erodedRuns.Count == 0)
        {
            return new RleRegion(width, height, erodedRuns);
        }

        // Step 2: Vertical erosion intersects intervals across [r - radiusY, r + radiusY] using ReadOnlySpan<PixelRun> row slices.
        ReadOnlySpan<PixelRun> erodedSpan = CollectionsMarshal.AsSpan(erodedRuns);
        List<PixelRun> outputRuns = new(erodedRuns.Count);
        List<PixelRun> currentIntersection = [];
        List<PixelRun> nextIntersection = [];

        for (int r = radiusY; r < height - radiusY; r++)
        {
            int baseRow = r - radiusY;
            int baseCount = rowCount[baseRow];
            if (baseCount == 0)
            {
                continue;
            }

            bool missingNeighborRow = false;
            for (int offset = -radiusY + 1; offset <= radiusY; offset++)
            {
                if (rowCount[r + offset] == 0)
                {
                    missingNeighborRow = true;
                    break;
                }
            }

            if (missingNeighborRow)
            {
                continue;
            }

            ReadOnlySpan<PixelRun> baseSlice = erodedSpan.Slice(rowStart[baseRow], baseCount);
            currentIntersection.Clear();
            for (int i = 0; i < baseSlice.Length; i++)
            {
                currentIntersection.Add(baseSlice[i]);
            }

            bool empty = false;
            for (int offset = -radiusY + 1; offset <= radiusY; offset++)
            {
                int otherRow = r + offset;
                ReadOnlySpan<PixelRun> otherSlice = erodedSpan.Slice(rowStart[otherRow], rowCount[otherRow]);

                IntersectSortedIntervalSpans(r, CollectionsMarshal.AsSpan(currentIntersection), otherSlice, nextIntersection);
                if (nextIntersection.Count == 0)
                {
                    empty = true;
                    break;
                }

                (currentIntersection, nextIntersection) = (nextIntersection, currentIntersection);
            }

            if (!empty)
            {
                for (int i = 0; i < currentIntersection.Count; i++)
                {
                    PixelRun run = currentIntersection[i];
                    outputRuns.Add(new PixelRun(r, run.StartCol, run.EndCol));
                }
            }
        }

        return new RleRegion(width, height, outputRuns);
    }

    /// <summary>Performs morphological Opening ($\text{Dilate}(\text{Erode}(R))$).</summary>
    public static RleRegion OpenBox(RleRegion source, int radiusX, int radiusY)
        => DilateBox(ErodeBox(source, radiusX, radiusY), radiusX, radiusY);

    /// <summary>Performs morphological Closing ($\text{Erode}(\text{Dilate}(R))$).</summary>
    public static RleRegion CloseBox(RleRegion source, int radiusX, int radiusY)
        => ErodeBox(DilateBox(source, radiusX, radiusY), radiusX, radiusY);

    /// <summary>Alias for <see cref="CloseBox"/> performing morphological Closing ($\text{Erode}(\text{Dilate}(R))$).</summary>
    public static RleRegion ClosingBox(RleRegion source, int radiusX, int radiusY)
        => CloseBox(source, radiusX, radiusY);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AppendHorizontalRun(
        List<PixelRun> runs,
        Span<int> rowStart,
        Span<int> rowCount,
        int row,
        int startCol,
        int endCol)
    {
        if (rowStart[row] < 0)
        {
            rowStart[row] = runs.Count;
        }

        rowCount[row]++;
        runs.Add(new PixelRun(row, startCol, endCol));
    }

    private static void UnionSortedIntervalSpans(
        int targetRow,
        ReadOnlySpan<PixelRun> spanA,
        ReadOnlySpan<PixelRun> spanB,
        List<PixelRun> output)
    {
        output.Clear();
        int i = 0;
        int j = 0;
        bool hasActive = false;
        int curStart = 0;
        int curEnd = -1;

        while (i < spanA.Length || j < spanB.Length)
        {
            PixelRun next;
            if (j >= spanB.Length || (i < spanA.Length && spanA[i].StartCol <= spanB[j].StartCol))
            {
                next = spanA[i++];
            }
            else
            {
                next = spanB[j++];
            }

            if (!hasActive)
            {
                curStart = next.StartCol;
                curEnd = next.EndCol;
                hasActive = true;
            }
            else if (next.StartCol <= curEnd + 1)
            {
                if (next.EndCol > curEnd)
                {
                    curEnd = next.EndCol;
                }
            }
            else
            {
                output.Add(new PixelRun(targetRow, curStart, curEnd));
                curStart = next.StartCol;
                curEnd = next.EndCol;
            }
        }

        if (hasActive)
        {
            output.Add(new PixelRun(targetRow, curStart, curEnd));
        }
    }

    private static void IntersectSortedIntervalSpans(
        int targetRow,
        ReadOnlySpan<PixelRun> spanA,
        ReadOnlySpan<PixelRun> spanB,
        List<PixelRun> output)
    {
        output.Clear();
        int i = 0;
        int j = 0;

        while (i < spanA.Length && j < spanB.Length)
        {
            int start = Math.Max(spanA[i].StartCol, spanB[j].StartCol);
            int end = Math.Min(spanA[i].EndCol, spanB[j].EndCol);
            if (start <= end)
            {
                output.Add(new PixelRun(targetRow, start, end));
            }

            if (spanA[i].EndCol < spanB[j].EndCol)
            {
                i++;
            }
            else
            {
                j++;
            }
        }
    }
}
