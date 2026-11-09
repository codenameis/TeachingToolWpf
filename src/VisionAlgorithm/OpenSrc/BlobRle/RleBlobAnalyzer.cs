// File: src/VisionAlgorithm/OpenSrc/BlobRle/RleBlobAnalyzer.cs
using System.Numerics;
using System.Runtime.CompilerServices;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.BlobRle;

/// <summary>
/// Geometric moments and shape descriptors of a connected component computed directly from its RLE chords.
/// </summary>
public readonly record struct BlobComponentFeature(
    int ComponentId,
    long AreaPixels,
    Vector2 Centroid,
    float PrincipalAxisAngleRad,
    float Eccentricity,
    int MinCol,
    int MinRow,
    int MaxCol,
    int MaxRow)
{
    public int BoundingWidth => (MaxCol - MinCol) + 1;
    public int BoundingHeight => (MaxRow - MinRow) + 1;
}

/// <summary>
/// Connected-component labeling with path-compressed Disjoint-Set Union (Union-Find) and closed-form algebraic
/// moment integration ($m_{00}, m_{10}, m_{01}, \mu_{20}, \mu_{02}, \mu_{11}$) directly over <see cref="PixelRun"/> chords.
/// </summary>
public static class RleBlobAnalyzer
{
    // [Math Why]: Sum of squares formula: sum_{x=s}^{e} x^2 = L * (2*s^2 + 2*s*e + 2*e^2 - L + 1) / 6, where 1/12 appears in central variance of a unit pixel cell.
    private const double PixelCellVariance = 1.0 / 12.0;

    // [Perf Why]: Component counts up to 128 (~8 KB of MomentAccumulator structs) are allocated on the thread stack with 0 GC pressure.
    private const int MaxStackAllocComponents = 128;

    /// <summary>
    /// Labels 8-connected components in <paramref name="region"/> and computes exact 0th, 1st, and 2nd central moments
    /// in $O(N_{\text{runs}})$ without visiting individual interior pixels.
    /// </summary>
    public static List<BlobComponentFeature> AnalyzeComponents(
        RleRegion region,
        long minimumAreaPixels,
        InspectionWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(workspace);

        IReadOnlyList<PixelRun> runs = region.Runs;
        int runCount = runs.Count;
        if (runCount == 0)
        {
            return [];
        }

        Span<int> parents = workspace.GetIntBufferA(runCount);
        for (int i = 0; i < runCount; i++)
        {
            parents[i] = i;
        }

        // Two-pointer sweep between consecutive rows to merge 8-connected overlapping runs in O(N_runs)
        int prevRowStart = -1;
        int prevRowEnd = -1;
        int prevRowIndex = -2;

        int curRowStart = 0;
        while (curRowStart < runCount)
        {
            int curRow = runs[curRowStart].Row;
            int curRowEnd = curRowStart;
            while (curRowEnd + 1 < runCount && runs[curRowEnd + 1].Row == curRow)
            {
                curRowEnd++;
            }

            if (prevRowStart >= 0 && curRow == prevRowIndex + 1)
            {
                int p = prevRowStart;
                int c = curRowStart;

                while (p <= prevRowEnd && c <= curRowEnd)
                {
                    PixelRun runP = runs[p];
                    PixelRun runC = runs[c];

                    if (runP.Overlaps8Connected(runC))
                    {
                        Union(parents, p, c);
                    }

                    if (runP.EndCol < runC.EndCol)
                    {
                        p++;
                    }
                    else
                    {
                        c++;
                    }
                }
            }

            prevRowStart = curRowStart;
            prevRowEnd = curRowEnd;
            prevRowIndex = curRow;
            curRowStart = curRowEnd + 1;
        }

        // Flatten Union-Find trees and assign contiguous component indices
        Span<int> rootToComponentIndex = workspace.GetIntBufferB(runCount);
        rootToComponentIndex.Fill(-1);
        int componentCount = 0;

        for (int i = 0; i < runCount; i++)
        {
            int root = Find(parents, i);
            parents[i] = root;
            if (rootToComponentIndex[root] < 0)
            {
                rootToComponentIndex[root] = componentCount++;
            }
        }

        Span<MomentAccumulator> accumulators = componentCount <= MaxStackAllocComponents
            ? stackalloc MomentAccumulator[componentCount]
            : new MomentAccumulator[componentCount];
        for (int c = 0; c < componentCount; c++)
        {
            accumulators[c] = MomentAccumulator.CreateEmpty();
        }

        // Closed-form chord integration over each PixelRun
        for (int i = 0; i < runCount; i++)
        {
            PixelRun run = runs[i];
            int compIdx = rootToComponentIndex[parents[i]];
            accumulators[compIdx].AddChord(run.Row, run.StartCol, run.EndCol);
        }

        List<BlobComponentFeature> results = new(componentCount);
        int outputId = 1;
        for (int c = 0; c < componentCount; c++)
        {
            if (accumulators[c].M00 < minimumAreaPixels)
            {
                continue;
            }

            results.Add(accumulators[c].BuildFeature(outputId++));
        }

        return results;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Find(Span<int> parents, int x)
    {
        while (parents[x] != x)
        {
            parents[x] = parents[parents[x]];
            x = parents[x];
        }

        return x;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Union(Span<int> parents, int a, int b)
    {
        int rootA = Find(parents, a);
        int rootB = Find(parents, b);
        if (rootA != rootB)
        {
            if (rootA < rootB)
            {
                parents[rootB] = rootA;
            }
            else
            {
                parents[rootA] = rootB;
            }
        }
    }

    private struct MomentAccumulator
    {
        public long M00;
        public double M10;
        public double M01;
        public double M20;
        public double M02;
        public double M11;
        public int MinCol;
        public int MinRow;
        public int MaxCol;
        public int MaxRow;

        public static MomentAccumulator CreateEmpty() => new()
        {
            MinCol = int.MaxValue,
            MinRow = int.MaxValue,
            MaxCol = int.MinValue,
            MaxRow = int.MinValue
        };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddChord(int row, int startCol, int endCol)
        {
            long length = (endCol - startCol) + 1L;
            // Pixel center convention: column c has center (c + 0.5), row r has center (r + 0.5).
            double yCenter = row + 0.5;
            double xStart = startCol + 0.5;
            double xEnd = endCol + 0.5;

            // [Math Why]: Exact integral of x over the chord of L discrete pixels:
            // sum_{c=s}^{e} (c + 0.5) = L * 0.5 * (xStart + xEnd).
            double sumX = length * 0.5 * (xStart + xEnd);
            double sumY = length * yCenter;

            // [Math Why]: Exact sum of (c + 0.5)^2 for c = s..e plus continuous pixel-cell variance (L / 12):
            // Using centered chord u in [-(L-1)/2, +(L-1)/2]: sum u^2 = L * (L^2 - 1) / 12.
            double chordMeanX = 0.5 * (xStart + xEnd);
            double sumXSq = (length * chordMeanX * chordMeanX)
                + (length * (((double)length * length) - 1.0) * PixelCellVariance)
                + (length * PixelCellVariance);

            double sumYSq = length * ((yCenter * yCenter) + PixelCellVariance);
            double sumXY = sumX * yCenter;

            M00 += length;
            M10 += sumX;
            M01 += sumY;
            M20 += sumXSq;
            M02 += sumYSq;
            M11 += sumXY;

            if (startCol < MinCol)
            {
                MinCol = startCol;
            }

            if (endCol > MaxCol)
            {
                MaxCol = endCol;
            }

            if (row < MinRow)
            {
                MinRow = row;
            }

            if (row > MaxRow)
            {
                MaxRow = row;
            }
        }

        public readonly BlobComponentFeature BuildFeature(int id)
        {
            double invArea = 1.0 / M00;
            double cx = M10 * invArea;
            double cy = M01 * invArea;

            double mu20 = (M20 * invArea) - (cx * cx);
            double mu02 = (M02 * invArea) - (cy * cy);
            double mu11 = (M11 * invArea) - (cx * cy);

            double angle = 0.5 * Math.Atan2(2.0 * mu11, mu20 - mu02);
            double diff = mu20 - mu02;
            double root = Math.Sqrt((diff * diff) + (4.0 * mu11 * mu11));
            double lambda1 = Math.Max(0.0, 0.5 * (mu20 + mu02 + root));
            double lambda2 = Math.Max(0.0, 0.5 * (mu20 + mu02 - root));

            float eccentricity = lambda1 > 1e-9
                ? (float)Math.Sqrt(Math.Clamp(1.0 - (lambda2 / lambda1), 0.0, 1.0))
                : 0f;

            return new BlobComponentFeature(
                ComponentId: id,
                AreaPixels: M00,
                Centroid: new Vector2((float)cx, (float)cy),
                PrincipalAxisAngleRad: (float)angle,
                Eccentricity: eccentricity,
                MinCol: MinCol,
                MinRow: MinRow,
                MaxCol: MaxCol,
                MaxRow: MaxRow);
        }
    }
}
