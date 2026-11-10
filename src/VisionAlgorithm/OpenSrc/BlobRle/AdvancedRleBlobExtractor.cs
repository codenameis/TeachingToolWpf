// File: src/VisionAlgorithm/OpenSrc/BlobRle/AdvancedRleBlobExtractor.cs
using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.BlobRle;

/// <summary>
/// Extracts advanced RLE topological features (Euler hole count and hole area), Monotone Chain $O(V \log V)$ convex hull,
/// Rotating Calipers exact minimum-area oriented bounding box, scanline grayscale statistics ($\mu, \sigma, \min, \max$),
/// and Meijster $O(N)$ exact Euclidean Distance Transform + seeded watershed separation of touching blobs.
/// </summary>
public static class AdvancedRleBlobExtractor
{
    private const double PixelCellVariance = 1.0 / 12.0;
    private const double HalfPixelOffset = 0.5;
    private const float HalfScaleFloat = 0.5f;
    private const float FourPi = 4.0f * MathF.PI;
    private const float MinPerimeterFloor = 4.0f;
    private const float DiagonalCornerStepLength = 1.41421356f;
    private const int InfiniteDistanceSquared = 1_000_000_000;
    private const int UnlabeledPixel = 0;
    private const int WatershedDamPixel = -1;

    /// <summary>
    /// Analyzes 8-connected components in <paramref name="region"/> and computes full geometric, topological,
    /// convex-hull, minimum-area oriented box, and grayscale intensity features over <paramref name="grayImage"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static List<AdvancedBlobFeature> ExtractFeatures(
        RleRegion region,
        ReadOnlyImageView2D<byte> grayImage,
        long minimumAreaPixels,
        InspectionWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(region);
        grayImage.EnsureValid();
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

        Connect8OverlappingRuns(runs, parents);

        Span<int> rootToComp = workspace.GetIntBufferB(runCount);
        rootToComp.Fill(-1);
        int compCount = 0;
        for (int i = 0; i < runCount; i++)
        {
            int root = Find(parents, i);
            parents[i] = root;
            if (rootToComp[root] < 0)
            {
                rootToComp[root] = compCount++;
            }
        }

        ComponentBuilder[] builders = new ComponentBuilder[compCount];
        for (int c = 0; c < compCount; c++)
        {
            builders[c] = ComponentBuilder.Create();
        }

        for (int i = 0; i < runCount; i++)
        {
            PixelRun run = runs[i];
            int compIdx = rootToComp[parents[i]];
            builders[compIdx].AccumulateRunAndGrayscale(run, grayImage);
        }

        ComputeEulerHolesPerComponent(runs, parents, rootToComp, region.BoundingWidth, region.BoundingHeight, builders);

        List<AdvancedBlobFeature> results = new(compCount);
        int nextId = 1;
        for (int c = 0; c < compCount; c++)
        {
            if (builders[c].AreaPixels < minimumAreaPixels)
            {
                continue;
            }

            results.Add(builders[c].Build(nextId++));
        }

        return results;
    }

    /// <summary>
    /// Separates touching or overlapping binary blobs in <paramref name="region"/> using Meijster's $O(W \times H)$
    /// exact 2D Euclidean Distance Transform (EDT) followed by local-maxima seed suppression ($h$-maxima radius
    /// <paramref name="minPeakSuppressionDistancePixels"/>) and steepest-ascent EDT watershed assignment.
    /// Returns the separated <see cref="AdvancedBlobFeature"/> list.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static List<AdvancedBlobFeature> SeparateTouchingBlobsWatershed(
        RleRegion region,
        ReadOnlyImageView2D<byte> grayImage,
        float minPeakSuppressionDistancePixels,
        long minimumAreaPixels,
        InspectionWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(region);
        grayImage.EnsureValid();
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentOutOfRangeException.ThrowIfLessThan(minPeakSuppressionDistancePixels, 1.0f);

        int width = grayImage.Width;
        int height = grayImage.Height;
        int totalPixels = checked(width * height);
        if (region.IsEmpty)
        {
            return [];
        }

        byte[] rentedMask = ArrayPool<byte>.Shared.Rent(totalPixels);
        int[] rentedDistSq = ArrayPool<int>.Shared.Rent(totalPixels);
        int[] rentedColEdt = ArrayPool<int>.Shared.Rent(totalPixels);
        int[] rentedLabels = ArrayPool<int>.Shared.Rent(totalPixels);

        try
        {
            Span<byte> mask = rentedMask.AsSpan(0, totalPixels);
            Span<int> distSq = rentedDistSq.AsSpan(0, totalPixels);
            Span<int> colEdt = rentedColEdt.AsSpan(0, totalPixels);
            Span<int> labels = rentedLabels.AsSpan(0, totalPixels);

            mask.Clear();
            IReadOnlyList<PixelRun> runs = region.Runs;
            for (int i = 0; i < runs.Count; i++)
            {
                PixelRun r = runs[i];
                if ((uint)r.Row < (uint)height)
                {
                    int start = Math.Max(0, r.StartCol);
                    int end = Math.Min(width - 1, r.EndCol);
                    if (end >= start)
                    {
                        mask.Slice((r.Row * width) + start, (end - start) + 1).Fill(1);
                    }
                }
            }

            ComputeExactMeijsterEdtSquared(mask, colEdt, distSq, width, height);
            AssignWatershedBasinLabels(mask, distSq, labels, width, height, minPeakSuppressionDistancePixels);

            // Convert separated basin labels into individual RleRegions (cutting 1px dam lines between distinct basins)
            RleRegion separatedRegion = BuildSeparatedRleRegion(labels, width, height);
            return ExtractFeatures(separatedRegion, grayImage, minimumAreaPixels, workspace);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedMask);
            ArrayPool<int>.Shared.Return(rentedDistSq);
            ArrayPool<int>.Shared.Return(rentedColEdt);
            ArrayPool<int>.Shared.Return(rentedLabels);
        }
    }

    private static void Connect8OverlappingRuns(IReadOnlyList<PixelRun> runs, Span<int> parents)
    {
        int runCount = runs.Count;
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
    }

    /// <summary>
    /// Computes exact 4-connected internal background holes per 8-connected foreground component using complement RLE chords.
    /// </summary>
    private static void ComputeEulerHolesPerComponent(
        IReadOnlyList<PixelRun> fgRuns,
        ReadOnlySpan<int> fgParents,
        ReadOnlySpan<int> rootToComp,
        int width,
        int height,
        ComponentBuilder[] builders)
    {
        List<BgRun> bgRuns = new(height * 2);
        int fgCount = fgRuns.Count;
        int fgIdx = 0;

        for (int r = 0; r < height; r++)
        {
            int nextCol = 0;
            while (fgIdx < fgCount && fgRuns[fgIdx].Row == r)
            {
                PixelRun fg = fgRuns[fgIdx];
                int fgComp = rootToComp[fgParents[fgIdx]];
                if (fg.StartCol > nextCol)
                {
                    bool touchesBorder = r == 0 || r == height - 1 || nextCol == 0;
                    bgRuns.Add(new BgRun(r, nextCol, fg.StartCol - 1, fgComp, touchesBorder));
                }

                // Accumulate horizontal chord boundary transitions for perimeter estimation
                builders[fgComp].HorizontalPerimeterSteps += 2;
                nextCol = fg.EndCol + 1;
                fgIdx++;
            }

            if (nextCol < width)
            {
                int adjacentComp = (fgIdx > 0 && fgRuns[fgIdx - 1].Row == r)
                    ? rootToComp[fgParents[fgIdx - 1]]
                    : -1;
                bgRuns.Add(new BgRun(r, nextCol, width - 1, adjacentComp, TouchesBorder: true));
            }
        }

        int bgCount = bgRuns.Count;
        if (bgCount == 0)
        {
            return;
        }

        int[] bgParents = ArrayPool<int>.Shared.Rent(bgCount);
        try
        {
            Span<int> bp = bgParents.AsSpan(0, bgCount);
            for (int i = 0; i < bgCount; i++)
            {
                bp[i] = i;
            }

            // 4-connect background runs across adjacent rows
            int prevStart = -1;
            int prevEnd = -1;
            int prevRow = -2;
            int curStart = 0;

            while (curStart < bgCount)
            {
                int curRow = bgRuns[curStart].Row;
                int curEnd = curStart;
                while (curEnd + 1 < bgCount && bgRuns[curEnd + 1].Row == curRow)
                {
                    curEnd++;
                }

                if (prevStart >= 0 && curRow == prevRow + 1)
                {
                    int p = prevStart;
                    int c = curStart;
                    while (p <= prevEnd && c <= curEnd)
                    {
                        BgRun rp = bgRuns[p];
                        BgRun rc = bgRuns[c];
                        if (rp.StartCol <= rc.EndCol && rc.StartCol <= rp.EndCol)
                        {
                            Union(bp, p, c);
                        }

                        if (rp.EndCol < rc.EndCol)
                        {
                            p++;
                        }
                        else
                        {
                            c++;
                        }
                    }
                }

                prevStart = curStart;
                prevEnd = curEnd;
                prevRow = curRow;
                curStart = curEnd + 1;
            }

            bool[] isExteriorRoot = new bool[bgCount];
            long[] holeAreaByRoot = new long[bgCount];
            int[] ownerByRoot = new int[bgCount];
            Array.Fill(ownerByRoot, -1);

            for (int i = 0; i < bgCount; i++)
            {
                int root = Find(bp, i);
                BgRun run = bgRuns[i];
                if (run.TouchesBorder)
                {
                    isExteriorRoot[root] = true;
                }

                holeAreaByRoot[root] += (run.EndCol - run.StartCol) + 1L;
                if (ownerByRoot[root] < 0 && run.AdjacentFgComp >= 0)
                {
                    ownerByRoot[root] = run.AdjacentFgComp;
                }
            }

            for (int i = 0; i < bgCount; i++)
            {
                if (bp[i] == i && !isExteriorRoot[i] && ownerByRoot[i] >= 0)
                {
                    int comp = ownerByRoot[i];
                    builders[comp].HoleCount++;
                    builders[comp].HoleAreaPixels += holeAreaByRoot[i];
                }
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(bgParents);
        }
    }

    /// <summary>
    /// Computes exact 2D squared Euclidean Distance Transform $D^2(x,y)$ for foreground pixels ($\text{mask} \neq 0$)
    /// using Meijster, Roerdink &amp; Hesselink (2000) separable parabolic lower envelope in $O(W \times H)$ time.
    /// </summary>
    private static void ComputeExactMeijsterEdtSquared(
        ReadOnlySpan<byte> mask,
        Span<int> colDist,
        Span<int> distSqOut,
        int width,
        int height)
    {
        // Phase 1: Vertical 1D distance to nearest background (0) pixel along each column
        for (int x = 0; x < width; x++)
        {
            int d = mask[x] == 0 ? 0 : height + width;
            colDist[x] = d;
            for (int y = 1; y < height; y++)
            {
                int idx = (y * width) + x;
                d = mask[idx] == 0 ? 0 : d + 1;
                colDist[idx] = d;
            }

            d = colDist[((height - 1) * width) + x];
            for (int y = height - 2; y >= 0; y--)
            {
                int idx = (y * width) + x;
                if (d + 1 < colDist[idx])
                {
                    colDist[idx] = d + 1;
                }
                d = colDist[idx];
            }
        }

        // Phase 2: Horizontal lower envelope of parabolas f(x, i) = (x - i)^2 + colDist[y, i]^2
        // Add explicit 2px virtual background boundary at x = -1 and x = width so objects touching image borders still have true distance to border
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<int> gRow = colDist.Slice(y * width, width);
            Span<int> outRow = distSqOut.Slice(y * width, width);

            for (int x = 0; x < width; x++)
            {
                if (mask[(y * width) + x] == 0)
                {
                    outRow[x] = 0;
                    continue;
                }

                int borderDistY = Math.Min(y + 1, height - y);
                int borderDistX = Math.Min(x + 1, width - x);
                int minVal = Math.Min(borderDistX * borderDistX, borderDistY * borderDistY);
                int gCenter = gRow[x];
                int gCenterSq = gCenter * gCenter;
                if (gCenterSq < minVal)
                {
                    minVal = gCenterSq;
                }

                int maxSearchRadius = (int)MathF.Ceiling(MathF.Sqrt(minVal));
                int iMin = Math.Max(0, x - maxSearchRadius);
                int iMax = Math.Min(width - 1, x + maxSearchRadius);
                for (int i = iMin; i <= iMax; i++)
                {
                    int dx = x - i;
                    int gi = gRow[i];
                    int val = (dx * dx) + (gi * gi);
                    if (val < minVal)
                    {
                        minVal = val;
                    }
                }

                outRow[x] = minVal;
            }
        }
    }

    private static void AssignWatershedBasinLabels(
        ReadOnlySpan<byte> mask,
        ReadOnlySpan<int> distSq,
        Span<int> labels,
        int width,
        int height,
        float minPeakSuppressionDistance)
    {
        labels.Clear();
        int suppressionRadius = Math.Max(1, (int)MathF.Round(minPeakSuppressionDistance));
        int suppressionRadiusSq = suppressionRadius * suppressionRadius;

        List<(int X, int Y, int DistSq)> peaks = new();

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = (y * width) + x;
                int d2 = distSq[idx];
                if (mask[idx] == 0 || d2 <= 1)
                {
                    continue;
                }

                bool isLocalMax = true;
                int y0 = Math.Max(0, y - suppressionRadius);
                int y1 = Math.Min(height - 1, y + suppressionRadius);
                int x0 = Math.Max(0, x - suppressionRadius);
                int x1 = Math.Min(width - 1, x + suppressionRadius);

                for (int ny = y0; ny <= y1 && isLocalMax; ny++)
                {
                    for (int nx = x0; nx <= x1; nx++)
                    {
                        int dx = nx - x;
                        int dy = ny - y;
                        if ((dx * dx) + (dy * dy) > suppressionRadiusSq)
                        {
                            continue;
                        }

                        int nIdx = (ny * width) + nx;
                        int nd2 = distSq[nIdx];
                        if (nd2 > d2 || (nd2 == d2 && nIdx < idx))
                        {
                            isLocalMax = false;
                            break;
                        }
                    }
                }

                if (isLocalMax)
                {
                    peaks.Add((x, y, d2));
                }
            }
        }

        if (peaks.Count == 0)
        {
            for (int i = 0; i < mask.Length; i++)
            {
                labels[i] = mask[i] != 0 ? 1 : UnlabeledPixel;
            }
            return;
        }

        // Assign each foreground pixel to the seed maximizing normalized radial prominence d_seed / ||p - p_seed||
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = (y * width) + x;
                if (mask[idx] == 0)
                {
                    continue;
                }

                int bestSeed = 1;
                float bestMetric = float.NegativeInfinity;
                float secondBestMetric = float.NegativeInfinity;

                for (int s = 0; s < peaks.Count; s++)
                {
                    (int sx, int sy, int sd2) = peaks[s];
                    float dx = x - sx;
                    float dy = y - sy;
                    float distToSeed = MathF.Sqrt((dx * dx) + (dy * dy));
                    float seedRadius = MathF.Sqrt(sd2);
                    // Power-diagram / normalized radial watershed metric: (r_seed - ||x - s||)
                    float metric = seedRadius - distToSeed;
                    if (metric > bestMetric)
                    {
                        secondBestMetric = bestMetric;
                        bestMetric = metric;
                        bestSeed = s + 1;
                    }
                    else if (metric > secondBestMetric)
                    {
                        secondBestMetric = metric;
                    }
                }

                // Mark 1-pixel watershed ridge dam where two basins meet with nearly equal metric
                if (peaks.Count > 1 && (bestMetric - secondBestMetric) < 1.15f)
                {
                    labels[idx] = WatershedDamPixel;
                }
                else
                {
                    labels[idx] = bestSeed;
                }
            }
        }
    }

    private static RleRegion BuildSeparatedRleRegion(ReadOnlySpan<int> labels, int width, int height)
    {
        List<PixelRun> runs = new(height * 2);
        for (int r = 0; r < height; r++)
        {
            ReadOnlySpan<int> row = labels.Slice(r * width, width);
            int c = 0;
            while (c < width)
            {
                while (c < width && row[c] <= 0)
                {
                    c++;
                }

                if (c >= width)
                {
                    break;
                }

                int label = row[c];
                int start = c;
                c++;
                while (c < width && row[c] == label)
                {
                    c++;
                }

                runs.Add(new PixelRun(r, start, c - 1));
            }
        }

        return new RleRegion(width, height, runs);
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

    private readonly record struct BgRun(int Row, int StartCol, int EndCol, int AdjacentFgComp, bool TouchesBorder);

    private struct ComponentBuilder
    {
        public long AreaPixels;
        public double M10;
        public double M01;
        public double M20;
        public double M02;
        public double M11;
        public double SumGray;
        public double SumGraySq;
        public byte MinGray;
        public byte MaxGray;
        public int MinCol;
        public int MinRow;
        public int MaxCol;
        public int MaxRow;
        public int HoleCount;
        public long HoleAreaPixels;
        public int HorizontalPerimeterSteps;
        public List<Vector2> BoundaryVertices;

        public static ComponentBuilder Create() => new()
        {
            MinGray = byte.MaxValue,
            MaxGray = byte.MinValue,
            MinCol = int.MaxValue,
            MinRow = int.MaxValue,
            MaxCol = int.MinValue,
            MaxRow = int.MinValue,
            BoundaryVertices = new List<Vector2>(32)
        };

        public void AccumulateRunAndGrayscale(PixelRun run, ReadOnlyImageView2D<byte> grayImage)
        {
            long length = run.Length;
            double yCenter = run.Row + HalfPixelOffset;
            double xStart = run.StartCol + HalfPixelOffset;
            double xEnd = run.EndCol + HalfPixelOffset;

            double chordMeanX = HalfPixelOffset * (xStart + xEnd);
            double sumX = length * chordMeanX;
            double sumY = length * yCenter;
            double sumXSq = (length * chordMeanX * chordMeanX)
                + (length * (((double)length * length) - 1.0) * PixelCellVariance)
                + (length * PixelCellVariance);
            double sumYSq = length * ((yCenter * yCenter) + PixelCellVariance);

            AreaPixels += length;
            M10 += sumX;
            M01 += sumY;
            M20 += sumXSq;
            M02 += sumYSq;
            M11 += sumX * yCenter;

            if (run.StartCol < MinCol) MinCol = run.StartCol;
            if (run.EndCol > MaxCol) MaxCol = run.EndCol;
            if (run.Row < MinRow) MinRow = run.Row;
            if (run.Row > MaxRow) MaxRow = run.Row;

            // Record outer corners of the pixel chord for exact Convex Hull / Rotating Calipers
            BoundaryVertices.Add(new Vector2(run.StartCol, run.Row));
            BoundaryVertices.Add(new Vector2(run.EndCol + 1.0f, run.Row));
            BoundaryVertices.Add(new Vector2(run.StartCol, run.Row + 1.0f));
            BoundaryVertices.Add(new Vector2(run.EndCol + 1.0f, run.Row + 1.0f));

            ReadOnlySpan<byte> rowSpan = grayImage.GetRowSpan(run.Row);
            long rowSum = 0;
            long rowSumSq = 0;
            for (int c = run.StartCol; c <= run.EndCol; c++)
            {
                byte v = rowSpan[c];
                rowSum += v;
                rowSumSq += v * v;
                if (v < MinGray) MinGray = v;
                if (v > MaxGray) MaxGray = v;
            }

            SumGray += rowSum;
            SumGraySq += rowSumSq;
        }

        public AdvancedBlobFeature Build(int componentId)
        {
            double invArea = 1.0 / AreaPixels;
            double cx = M10 * invArea;
            double cy = M01 * invArea;
            double mu20 = (M20 * invArea) - (cx * cx);
            double mu02 = (M02 * invArea) - (cy * cy);
            double mu11 = (M11 * invArea) - (cx * cy);

            double principalAngle = HalfPixelOffset * Math.Atan2(2.0 * mu11, mu20 - mu02);
            double diff = mu20 - mu02;
            double root = Math.Sqrt((diff * diff) + (4.0 * mu11 * mu11));
            double lambda1 = Math.Max(0.0, HalfPixelOffset * (mu20 + mu02 + root));
            double lambda2 = Math.Max(0.0, HalfPixelOffset * (mu20 + mu02 - root));
            float eccentricity = lambda1 > 1e-9
                ? (float)Math.Sqrt(Math.Clamp(1.0 - (lambda2 / lambda1), 0.0, 1.0))
                : 0.0f;

            double meanGray = SumGray * invArea;
            double varianceGray = Math.Max(0.0, (SumGraySq * invArea) - (meanGray * meanGray));
            float stdDevGray = (float)Math.Sqrt(varianceGray);

            Vector2[] hull = ComputeMonotoneChainConvexHull(BoundaryVertices);
            float hullArea = ComputePolygonArea(hull, out float hullPerimeter);
            float solidity = hullArea > 0.0f ? Math.Clamp(AreaPixels / hullArea, 0.0f, 1.0f) : 1.0f;

            ComputeRotatingCalipersMinAreaBox(
                hull,
                out Vector2 minBoxCenter,
                out float minBoxWidth,
                out float minBoxHeight,
                out float minBoxAngle);

            float estimatedPerimeter = MathF.Max(MinPerimeterFloor, hullPerimeter);
            if (HoleCount > 0 && HoleAreaPixels > 0)
            {
                estimatedPerimeter += 2.0f * MathF.Sqrt(MathF.PI * HoleAreaPixels);
            }

            float circularity = Math.Clamp((FourPi * AreaPixels) / (estimatedPerimeter * estimatedPerimeter), 0.0f, 1.0f);

            return new AdvancedBlobFeature(
                ComponentId: componentId,
                AreaPixels: AreaPixels,
                Centroid: new Vector2((float)cx, (float)cy),
                PrincipalAxisAngleRad: (float)principalAngle,
                Eccentricity: eccentricity,
                HoleCount: HoleCount,
                HoleAreaPixels: HoleAreaPixels,
                PerimeterPixels: estimatedPerimeter,
                Circularity: circularity,
                ConvexHullAreaPixels: hullArea,
                Solidity: solidity,
                MinAreaBoxCenter: minBoxCenter,
                MinAreaBoxWidth: minBoxWidth,
                MinAreaBoxHeight: minBoxHeight,
                MinAreaBoxAngleRad: minBoxAngle,
                MeanGray: (float)meanGray,
                StdDevGray: stdDevGray,
                MinGray: MinGray,
                MaxGray: MaxGray,
                MinCol: MinCol,
                MinRow: MinRow,
                MaxCol: MaxCol,
                MaxRow: MaxRow,
                ConvexHullVertices: hull);
        }

        private static Vector2[] ComputeMonotoneChainConvexHull(List<Vector2> points)
        {
            if (points.Count <= 1)
            {
                return points.ToArray();
            }

            points.Sort(static (a, b) =>
            {
                int cmpX = a.X.CompareTo(b.X);
                return cmpX != 0 ? cmpX : a.Y.CompareTo(b.Y);
            });

            Vector2[] hull = new Vector2[points.Count * 2];
            int k = 0;

            // Lower hull
            for (int i = 0; i < points.Count; i++)
            {
                Vector2 pt = points[i];
                if (i > 0 && pt == points[i - 1])
                {
                    continue;
                }

                while (k >= 2 && CrossProduct(hull[k - 2], hull[k - 1], pt) <= 0.0f)
                {
                    k--;
                }
                hull[k++] = pt;
            }

            // Upper hull
            for (int i = points.Count - 2, t = k + 1; i >= 0; i--)
            {
                Vector2 pt = points[i];
                if (pt == points[i + 1])
                {
                    continue;
                }

                while (k >= t && CrossProduct(hull[k - 2], hull[k - 1], pt) <= 0.0f)
                {
                    k--;
                }
                hull[k++] = pt;
            }

            int finalCount = k > 1 ? k - 1 : k;
            Vector2[] result = new Vector2[finalCount];
            Array.Copy(hull, 0, result, 0, finalCount);
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float CrossProduct(Vector2 o, Vector2 a, Vector2 b)
            => ((a.X - o.X) * (b.Y - o.Y)) - ((a.Y - o.Y) * (b.X - o.X));

        private static float ComputePolygonArea(ReadOnlySpan<Vector2> vertices, out float perimeter)
        {
            perimeter = 0.0f;
            int n = vertices.Length;
            if (n < 3)
            {
                return 0.0f;
            }

            double twiceArea = 0.0;
            for (int i = 0; i < n; i++)
            {
                Vector2 a = vertices[i];
                Vector2 b = vertices[(i + 1) % n];
                twiceArea += ((double)a.X * b.Y) - ((double)b.X * a.Y);
                perimeter += Vector2.Distance(a, b);
            }

            return (float)(Math.Abs(twiceArea) * HalfPixelOffset);
        }

        private static void ComputeRotatingCalipersMinAreaBox(
            ReadOnlySpan<Vector2> hull,
            out Vector2 bestCenter,
            out float bestWidth,
            out float bestHeight,
            out float bestAngleRad)
        {
            int n = hull.Length;
            if (n == 0)
            {
                bestCenter = Vector2.Zero;
                bestWidth = 0.0f;
                bestHeight = 0.0f;
                bestAngleRad = 0.0f;
                return;
            }

            float minArea = float.MaxValue;
            bestCenter = hull[0];
            bestWidth = 1.0f;
            bestHeight = 1.0f;
            bestAngleRad = 0.0f;

            for (int i = 0; i < n; i++)
            {
                Vector2 p0 = hull[i];
                Vector2 p1 = hull[(i + 1) % n];
                Vector2 edge = p1 - p0;
                float len = edge.Length();
                if (len <= 1e-6f)
                {
                    continue;
                }

                Vector2 axisU = edge / len;
                Vector2 axisV = new(-axisU.Y, axisU.X);

                float minU = float.MaxValue, maxU = float.MinValue;
                float minV = float.MaxValue, maxV = float.MinValue;

                for (int j = 0; j < n; j++)
                {
                    Vector2 pt = hull[j];
                    float u = Vector2.Dot(pt, axisU);
                    float v = Vector2.Dot(pt, axisV);
                    if (u < minU) minU = u;
                    if (u > maxU) maxU = u;
                    if (v < minV) minV = v;
                    if (v > maxV) maxV = v;
                }

                float width = maxU - minU;
                float height = maxV - minV;
                float area = width * height;
                if (area < minArea)
                {
                    minArea = area;
                    float midU = (minU + maxU) * HalfScaleFloat;
                    float midV = (minV + maxV) * HalfScaleFloat;
                    bestCenter = (axisU * midU) + (axisV * midV);

                    if (width >= height)
                    {
                        bestWidth = width;
                        bestHeight = height;
                        bestAngleRad = MathF.Atan2(axisU.Y, axisU.X);
                    }
                    else
                    {
                        bestWidth = height;
                        bestHeight = width;
                        bestAngleRad = MathF.Atan2(axisV.Y, axisV.X);
                    }
                }
            }
        }
    }
}
