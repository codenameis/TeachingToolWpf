// File: src/VisionAlgorithm/OpenSrc/ToolBlock/LargestBlobToolNode.cs
using System.Numerics;
using System.Runtime.CompilerServices;
using VisionAlgorithm.OpenSrc.BlobRle;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.ToolBlock;

/// <summary>
/// Zero-allocation ROI tool node that rectifies a fixture-linked region via <see cref="SimdAffineWarper"/>,
/// encodes foreground chords, performs optional morphological RLE filtering, extracts the largest connected component
/// in $O(N_{\text{runs}})$, and transforms the blob centroid and orientation back to world image coordinates via <see cref="FixturePose2D.LocalToWorld"/>.
/// </summary>
public sealed class LargestBlobToolNode : IToolBlockNode
{
    public const string PortInFixture = "InFixture";
    public const string PortOutFound = "OutFound";
    public const string PortOutBlobCount = "OutBlobCount";
    public const string PortOutMaxBlobCenter = "OutMaxBlobCenter";
    public const string PortOutMaxBlobArea = "OutMaxBlobArea";
    public const string PortOutMaxBlobAngleDeg = "OutMaxBlobAngleDeg";
    public const string PortOutEccentricity = "OutEccentricity";

    // [Math Why]: 180.0 / pi converts radians to degrees.
    private const double RadiansToDegrees = 180.0 / Math.PI;

    // [Math Why]: Sum of squares of centered unit cell variance 1/12 used in exact chord second-moment integration.
    private const double PixelCellVariance = 1.0 / 12.0;

    // [Perf Why]: Up to 512 RLE runs and 128 components per ROI are processed on the stack with 0 GC allocation.
    private const int MaxStackRuns = 512;
    private const int MaxStackComponents = 128;

    private readonly LargestBlobToolSpec _spec;
    private readonly TerminalHandle<FixturePose2D> _inFixture;
    private readonly TerminalHandle<bool> _outFound;
    private readonly TerminalHandle<double> _outBlobCount;
    private readonly TerminalHandle<Point2D> _outMaxBlobCenter;
    private readonly TerminalHandle<double> _outMaxBlobArea;
    private readonly TerminalHandle<double> _outMaxBlobAngleDeg;
    private readonly TerminalHandle<double> _outEccentricity;

    private readonly ToolBlockTerminalDescriptor[] _inputDescriptors;
    private readonly ToolBlockTerminalDescriptor[] _outputDescriptors;
    private BlobAnalysisResult _lastResult;

    public int NodeId { get; }
    public string NodeName { get; }
    public LargestBlobToolSpec Spec => _spec;
    public BlobAnalysisResult LastResult => _lastResult;

    public TerminalHandle<FixturePose2D> InFixture => _inFixture;
    public TerminalHandle<bool> OutFound => _outFound;
    public TerminalHandle<double> OutBlobCount => _outBlobCount;
    public TerminalHandle<Point2D> OutMaxBlobCenter => _outMaxBlobCenter;
    public TerminalHandle<double> OutMaxBlobArea => _outMaxBlobArea;
    public TerminalHandle<double> OutMaxBlobAngleDeg => _outMaxBlobAngleDeg;
    public TerminalHandle<double> OutEccentricity => _outEccentricity;

    public LargestBlobToolNode(
        int nodeId,
        string nodeName,
        in LargestBlobToolSpec spec,
        TerminalHandle<FixturePose2D> inFixture,
        TerminalHandle<bool> outFound,
        TerminalHandle<double> outBlobCount,
        TerminalHandle<Point2D> outMaxBlobCenter,
        TerminalHandle<double> outMaxBlobArea,
        TerminalHandle<double> outMaxBlobAngleDeg,
        TerminalHandle<double> outEccentricity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeName);
        spec.EnsureValid();

        NodeId = nodeId;
        NodeName = nodeName;
        _spec = spec;
        _inFixture = inFixture;
        _outFound = outFound;
        _outBlobCount = outBlobCount;
        _outMaxBlobCenter = outMaxBlobCenter;
        _outMaxBlobArea = outMaxBlobArea;
        _outMaxBlobAngleDeg = outMaxBlobAngleDeg;
        _outEccentricity = outEccentricity;

        _inputDescriptors = inFixture.IsBound
            ? [new ToolBlockTerminalDescriptor(PortInFixture, TerminalDirection.Input, TerminalDataKind.Pose, inFixture.SlotIndex)]
            : [];

        _outputDescriptors =
        [
            new ToolBlockTerminalDescriptor(PortOutFound, TerminalDirection.Output, TerminalDataKind.Boolean, outFound.SlotIndex),
            new ToolBlockTerminalDescriptor(PortOutBlobCount, TerminalDirection.Output, TerminalDataKind.Scalar, outBlobCount.SlotIndex),
            new ToolBlockTerminalDescriptor(PortOutMaxBlobCenter, TerminalDirection.Output, TerminalDataKind.Point, outMaxBlobCenter.SlotIndex),
            new ToolBlockTerminalDescriptor(PortOutMaxBlobArea, TerminalDirection.Output, TerminalDataKind.Scalar, outMaxBlobArea.SlotIndex),
            new ToolBlockTerminalDescriptor(PortOutMaxBlobAngleDeg, TerminalDirection.Output, TerminalDataKind.Scalar, outMaxBlobAngleDeg.SlotIndex),
            new ToolBlockTerminalDescriptor(PortOutEccentricity, TerminalDirection.Output, TerminalDataKind.Scalar, outEccentricity.SlotIndex)
        ];
    }

    public ReadOnlySpan<ToolBlockTerminalDescriptor> GetInputTerminals()
        => _inputDescriptors;

    public ReadOnlySpan<ToolBlockTerminalDescriptor> GetOutputTerminals()
        => _outputDescriptors;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Execute(
        ReadOnlyImageView2D<byte> image,
        VisionWorkspace workspace,
        ToolBlockContext context)
    {
        if (!context.TryResolveInputFixture(_inFixture, out FixturePose2D baseFixture))
        {
            WriteFailureOutputs(context);
            return;
        }

        FixturePose2D worldRoiPose = baseFixture.Compose(_spec.LocalRoiOffset);
        int roiW = _spec.RoiWidth;
        int roiH = _spec.RoiHeight;
        int pixelCount = checked(roiW * roiH);

        Span<byte> rectifiedBytes = workspace.GetByteBufferA(pixelCount);
        Span<float> floatScratch = workspace.GetFloatBufferA(pixelCount);
        SimdAffineWarper.WarpAffineBilinear(image, in worldRoiPose, roiW, roiH, rectifiedBytes, floatScratch);

        // Apply zero-allocation 2D box morphology on workspace byte buffers if configured
        ReadOnlySpan<byte> activePatch = ApplyWorkspaceMorphologyIfConfigured(
            rectifiedBytes,
            roiW,
            roiH,
            _spec.MinIntensity,
            _spec.MaxIntensity,
            _spec.MorphologyOperation,
            _spec.MorphologyRadiusX,
            _spec.MorphologyRadiusY,
            workspace);

        if (!TryExtractLargestBlobZeroAlloc(
            activePatch,
            roiW,
            roiH,
            _spec.MinIntensity,
            _spec.MaxIntensity,
            _spec.MinimumAreaPixels,
            workspace,
            out int validBlobCount,
            out BlobComponentFeature largestBlob))
        {
            WriteFailureOutputs(context);
            return;
        }

        // Convert rectified patch centroid [0..roiW, 0..roiH] to local fixture coordinates centered at (roiW * 0.5, roiH * 0.5)
        float localX = largestBlob.Centroid.X - (roiW * 0.5f);
        float localY = largestBlob.Centroid.Y - (roiH * 0.5f);
        Vector2 worldCenterVec = worldRoiPose.LocalToWorld(localX, localY);

        Point2D worldCenter = new(worldCenterVec.X, worldCenterVec.Y);
        double worldAngleDeg = (worldRoiPose.AngleRad + largestBlob.PrincipalAxisAngleRad) * RadiansToDegrees;
        double scaledArea = largestBlob.AreaPixels * (double)worldRoiPose.ScaleX * worldRoiPose.ScaleY;
        FixturePose2D blobWorldPose = new(
            worldCenterVec.X,
            worldCenterVec.Y,
            worldRoiPose.AngleRad + largestBlob.PrincipalAxisAngleRad,
            worldRoiPose.Scale);

        _lastResult = new BlobAnalysisResult(
            Header: VisionResultHeader.SucceededMeasurement(
                category: VisionToolCategory.BlobAnalysis,
                qualityScore: Math.Clamp(1.0f - (largestBlob.Eccentricity * 0.25f), 0.1f, 1.0f),
                primaryMetric: (float)scaledArea,
                matchedModelIndex: 0,
                matchedModelId: largestBlob.ComponentId),
            RoiWorldPose: worldRoiPose,
            PrimaryBlobWorldPose: blobWorldPose,
            BlobCount: validBlobCount,
            TotalBlobAreaPixels: largestBlob.AreaPixels,
            PrimaryBlob: largestBlob,
            BlobSubRegions: ReadOnlyMemory<ReplaySubRegion>.Empty);

        context.WriteBool(_outFound, true, isValid: true);
        context.WriteScalar(_outBlobCount, validBlobCount, isValid: true);
        context.WritePoint(_outMaxBlobCenter, worldCenter, isValid: true);
        context.WriteScalar(_outMaxBlobArea, scaledArea, isValid: true);
        context.WriteScalar(_outMaxBlobAngleDeg, worldAngleDeg, isValid: true);
        context.WriteScalar(_outEccentricity, largestBlob.Eccentricity, isValid: true);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteFailureOutputs(ToolBlockContext context)
    {
        _lastResult = BlobAnalysisResult.Empty(FixturePose2D.Identity);
        context.WriteBool(_outFound, false, isValid: true);
        context.InvalidateScalar(_outBlobCount);
        context.InvalidatePoint(_outMaxBlobCenter);
        context.InvalidateScalar(_outMaxBlobArea);
        context.InvalidateScalar(_outMaxBlobAngleDeg);
        context.InvalidateScalar(_outEccentricity);
    }

    private static ReadOnlySpan<byte> ApplyWorkspaceMorphologyIfConfigured(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        byte minIntensity,
        byte maxIntensity,
        BlobMorphologyOperation operation,
        int radiusX,
        int radiusY,
        VisionWorkspace workspace)
    {
        if (operation == BlobMorphologyOperation.None || (radiusX == 0 && radiusY == 0))
        {
            return source;
        }

        int len = width * height;
        Span<byte> binaryA = workspace.GetByteBufferB(len);
        for (int i = 0; i < len; i++)
        {
            byte v = source[i];
            binaryA[i] = (v >= minIntensity && v <= maxIntensity) ? byte.MaxValue : (byte)0;
        }

        // Reuse float buffers C and D as byte scratch via MemoryMarshal.AsBytes for zero-allocation morphological passes
        Span<byte> scratch1 = System.Runtime.InteropServices.MemoryMarshal.AsBytes(workspace.GetFloatBufferC(len)).Slice(0, len);
        Span<byte> scratch2 = System.Runtime.InteropServices.MemoryMarshal.AsBytes(workspace.GetFloatBufferD(len)).Slice(0, len);

        switch (operation)
        {
            case BlobMorphologyOperation.DilateBox:
                MorphBoxSeparable(binaryA, scratch1, scratch2, width, height, radiusX, radiusY, isDilation: true);
                return scratch2;

            case BlobMorphologyOperation.ErodeBox:
                MorphBoxSeparable(binaryA, scratch1, scratch2, width, height, radiusX, radiusY, isDilation: false);
                return scratch2;

            case BlobMorphologyOperation.OpenBox:
                MorphBoxSeparable(binaryA, scratch1, scratch2, width, height, radiusX, radiusY, isDilation: false);
                MorphBoxSeparable(scratch2, scratch1, binaryA, width, height, radiusX, radiusY, isDilation: true);
                return binaryA;

            case BlobMorphologyOperation.CloseBox:
                MorphBoxSeparable(binaryA, scratch1, scratch2, width, height, radiusX, radiusY, isDilation: true);
                MorphBoxSeparable(scratch2, scratch1, binaryA, width, height, radiusX, radiusY, isDilation: false);
                return binaryA;

            default:
                return source;
        }
    }

    private static void MorphBoxSeparable(
        ReadOnlySpan<byte> input,
        Span<byte> horizontalTemp,
        Span<byte> output,
        int width,
        int height,
        int radiusX,
        int radiusY,
        bool isDilation)
    {
        byte targetVal = isDilation ? byte.MaxValue : (byte)0;
        byte defaultVal = isDilation ? (byte)0 : byte.MaxValue;

        for (int r = 0; r < height; r++)
        {
            int rowOffset = r * width;
            for (int c = 0; c < width; c++)
            {
                int c0 = Math.Max(0, c - radiusX);
                int c1 = Math.Min(width - 1, c + radiusX);
                byte val = defaultVal;
                for (int k = c0; k <= c1; k++)
                {
                    if (input[rowOffset + k] == targetVal)
                    {
                        val = targetVal;
                        break;
                    }
                }

                horizontalTemp[rowOffset + c] = val;
            }
        }

        for (int r = 0; r < height; r++)
        {
            int r0 = Math.Max(0, r - radiusY);
            int r1 = Math.Min(height - 1, r + radiusY);
            int rowOffset = r * width;
            for (int c = 0; c < width; c++)
            {
                byte val = defaultVal;
                for (int ky = r0; ky <= r1; ky++)
                {
                    if (horizontalTemp[(ky * width) + c] == targetVal)
                    {
                        val = targetVal;
                        break;
                    }
                }

                output[rowOffset + c] = val;
            }
        }
    }

    /// <summary>
    /// Zero-allocation RLE chord encoding and Union-Find moment integration over stack/workspace spans.
    /// </summary>
    private static bool TryExtractLargestBlobZeroAlloc(
        ReadOnlySpan<byte> patch,
        int width,
        int height,
        byte minIntensity,
        byte maxIntensity,
        long minimumAreaPixels,
        VisionWorkspace workspace,
        out int validBlobCount,
        out BlobComponentFeature largestBlob)
    {
        validBlobCount = 0;
        largestBlob = default;

        Span<PixelRun> stackRuns = stackalloc PixelRun[MaxStackRuns];
        int runCount = 0;

        for (int r = 0; r < height; r++)
        {
            ReadOnlySpan<byte> row = patch.Slice(r * width, width);
            int c = 0;
            while (c < width)
            {
                while (c < width && (row[c] < minIntensity || row[c] > maxIntensity))
                {
                    c++;
                }

                if (c >= width)
                {
                    break;
                }

                int startCol = c;
                c++;
                while (c < width && row[c] >= minIntensity && row[c] <= maxIntensity)
                {
                    c++;
                }

                if (runCount < MaxStackRuns)
                {
                    stackRuns[runCount++] = new PixelRun(r, startCol, c - 1);
                }
            }
        }

        if (runCount == 0)
        {
            return false;
        }

        Span<int> parents = workspace.GetIntBufferA(runCount);
        for (int i = 0; i < runCount; i++)
        {
            parents[i] = i;
        }

        int prevRowStart = -1;
        int prevRowEnd = -1;
        int prevRowIndex = -2;
        int curRowStart = 0;

        while (curRowStart < runCount)
        {
            int curRow = stackRuns[curRowStart].Row;
            int curRowEnd = curRowStart;
            while (curRowEnd + 1 < runCount && stackRuns[curRowEnd + 1].Row == curRow)
            {
                curRowEnd++;
            }

            if (prevRowStart >= 0 && curRow == prevRowIndex + 1)
            {
                int p = prevRowStart;
                int c = curRowStart;

                while (p <= prevRowEnd && c <= curRowEnd)
                {
                    PixelRun runP = stackRuns[p];
                    PixelRun runC = stackRuns[c];
                    if (runP.Overlaps8Connected(runC))
                    {
                        UnionRuns(parents, p, c);
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

        Span<int> rootToComp = workspace.GetIntBufferB(runCount);
        rootToComp.Fill(-1);
        int compCount = 0;

        for (int i = 0; i < runCount; i++)
        {
            int root = FindRoot(parents, i);
            parents[i] = root;
            if (rootToComp[root] < 0 && compCount < MaxStackComponents)
            {
                rootToComp[root] = compCount++;
            }
        }

        Span<ChordAccumulator> accumulators = stackalloc ChordAccumulator[compCount];
        for (int c = 0; c < compCount; c++)
        {
            accumulators[c] = ChordAccumulator.CreateEmpty();
        }

        for (int i = 0; i < runCount; i++)
        {
            int compIdx = rootToComp[parents[i]];
            if (compIdx >= 0)
            {
                PixelRun run = stackRuns[i];
                accumulators[compIdx].AddChord(run.Row, run.StartCol, run.EndCol);
            }
        }

        int bestIdx = -1;
        long bestArea = -1;
        for (int c = 0; c < compCount; c++)
        {
            long area = accumulators[c].M00;
            if (area >= minimumAreaPixels)
            {
                validBlobCount++;
                if (area > bestArea)
                {
                    bestArea = area;
                    bestIdx = c;
                }
            }
        }

        if (bestIdx < 0)
        {
            return false;
        }

        largestBlob = accumulators[bestIdx].BuildFeature(1);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FindRoot(Span<int> parents, int x)
    {
        while (parents[x] != x)
        {
            parents[x] = parents[parents[x]];
            x = parents[x];
        }

        return x;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void UnionRuns(Span<int> parents, int a, int b)
    {
        int rootA = FindRoot(parents, a);
        int rootB = FindRoot(parents, b);
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

    private struct ChordAccumulator
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

        public static ChordAccumulator CreateEmpty() => new()
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
            double yCenter = row + 0.5;
            double xStart = startCol + 0.5;
            double xEnd = endCol + 0.5;

            double sumX = length * 0.5 * (xStart + xEnd);
            double sumY = length * yCenter;
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

            if (startCol < MinCol) MinCol = startCol;
            if (endCol > MaxCol) MaxCol = endCol;
            if (row < MinRow) MinRow = row;
            if (row > MaxRow) MaxRow = row;
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

            // [Math Why]: Floor 1e-9 avoids division by zero on single-pixel isotropic blobs.
            const double EigenvalueFloor = 1e-9;
            float eccentricity = lambda1 > EigenvalueFloor
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
