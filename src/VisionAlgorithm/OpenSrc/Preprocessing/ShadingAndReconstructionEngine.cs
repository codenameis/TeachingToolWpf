// File: src/VisionAlgorithm/OpenSrc/Preprocessing/ShadingAndReconstructionEngine.cs
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.Preprocessing;

/// <summary>
/// Commercial-grade illumination shading correction, Perona-Malik anisotropic diffusion, and Vincent (1993)
/// grayscale morphological reconstruction engine (HALCON <c>estimate_background_illumination</c>,
/// <c>anisotropic_diffusion</c>, and <c>fill_up</c> / <c>opening_by_reconstruction</c> parity).
/// </summary>
public static class ShadingAndReconstructionEngine
{
    private const int PolynomialBasisCount = 6;
    private const int PolynomialNormalMatrixSize = PolynomialBasisCount * PolynomialBasisCount;
    private const int MaxDimensionSubsampleDivisor = 4;
    private const double HalfDimensionScale = 0.5;
    private const float HalfDimensionScaleFloat = 0.5f;
    private const double TikhonovRegularizationEpsilon = 1e-8;
    private const float MinMultiplicativeBackgroundFloor = 8.0f;
    private const float QuarterConnectivityFactor = 0.25f;
    private const float MinNormalizedCoordinateSpan = 1.0f;
    private const int BasisTermConstantIndex = 0;
    private const int BasisTermUIndex = 1;
    private const int BasisTermVIndex = 2;
    private const int BasisTermUSquaredIndex = 3;
    private const int BasisTermUVIndex = 4;
    private const int BasisTermVSquaredIndex = 5;
    private const int SimdLane0 = 0;
    private const int SimdLane1 = 1;
    private const int SimdLane2 = 2;
    private const int SimdLane3 = 3;
    private const int SimdLane4 = 4;
    private const int SimdLane5 = 5;
    private const int SimdLane6 = 6;
    private const int SimdLane7 = 7;

    /// <summary>
    /// Fits a 2D biquadratic illumination surface $B(u,v) = c_0 + c_1 u + c_2 v + c_3 u^2 + c_4 u v + c_5 v^2$
    /// over normalized coordinates $u, v \in [-1, 1]$ via regularized $6 \times 6$ Cholesky / Gaussian elimination
    /// and flattens uneven lighting or lens vignetting across <paramref name="source"/> using <see cref="Vector256{T}"/> FMA.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void ApplyPolynomialShadingCorrection(
        ReadOnlyImageView2D<byte> source,
        in PolynomialShadingSpec spec,
        Span<byte> destination,
        int destinationStride)
    {
        source.EnsureValid();
        spec.EnsureInitialized();
        ValidateDestination(source.Width, source.Height, destination, destinationStride);

        Span<double> coefficients = stackalloc double[PolynomialBasisCount];
        EstimateBiquadraticSurfaceCoefficients(source, spec.SubsampleStep, coefficients);

        EvaluateAndCorrectSurfaceSimd(
            source,
            coefficients,
            spec.TargetBaseGray,
            spec.IsMultiplicativeGain,
            destination,
            destinationStride);
    }

    /// <summary>
    /// Convenience overload accepting raw strided spans for <see cref="ApplyPolynomialShadingCorrection(ReadOnlyImageView2D{byte}, in PolynomialShadingSpec, Span{byte}, int)"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ApplyPolynomialShadingCorrection(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        int sourceStride,
        in PolynomialShadingSpec spec,
        Span<byte> destination,
        int destinationStride)
        => ApplyPolynomialShadingCorrection(
            new ReadOnlyImageView2D<byte>(source, width, height, sourceStride),
            in spec,
            destination,
            destinationStride);

    /// <summary>
    /// Applies Perona-Malik 2D Anisotropic Diffusion:
    /// $I^{(t+1)}_{x,y} = I^{(t)}_{x,y} + \frac{\lambda}{4} \sum_{d \in \{N,S,E,W\}} c(\|\nabla_d I\|)\,\nabla_d I$
    /// with rational quadratic edge-stopping diffusivity $c(\Delta) = \frac{1}{1 + (\Delta / \kappa)^2}$,
    /// preserving sharp crack/edge boundaries while homogenizing interior grain noise.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void ApplyAnisotropicDiffusion(
        ReadOnlyImageView2D<byte> source,
        in AnisotropicDiffusionSpec spec,
        Span<byte> destination,
        int destinationStride)
    {
        source.EnsureValid();
        spec.EnsureInitialized();
        ValidateDestination(source.Width, source.Height, destination, destinationStride);

        int width = source.Width;
        int height = source.Height;
        int totalPixels = checked(width * height);

        float[] rentedPing = ArrayPool<float>.Shared.Rent(totalPixels);
        float[] rentedPong = ArrayPool<float>.Shared.Rent(totalPixels);
        try
        {
            Span<float> current = rentedPing.AsSpan(0, totalPixels);
            Span<float> next = rentedPong.AsSpan(0, totalPixels);

            ConvertByteViewToContiguousFloat(source, current);

            float invKappaSq = 1.0f / (spec.KappaContrast * spec.KappaContrast);
            float scaledLambda = spec.TimeStepLambda * QuarterConnectivityFactor;

            for (int iter = 0; iter < spec.Iterations; iter++)
            {
                DiffuseSingleIterationSimd(current, next, width, height, invKappaSq, scaledLambda);
                Span<float> temp = current;
                current = next;
                next = temp;
            }

            ConvertContiguousFloatToStridedByte(current, width, height, destination, destinationStride);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rentedPing);
            ArrayPool<float>.Shared.Return(rentedPong);
        }
    }

    /// <summary>
    /// Convenience overload accepting raw strided spans for <see cref="ApplyAnisotropicDiffusion(ReadOnlyImageView2D{byte}, in AnisotropicDiffusionSpec, Span{byte}, int)"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ApplyAnisotropicDiffusion(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        int sourceStride,
        in AnisotropicDiffusionSpec spec,
        Span<byte> destination,
        int destinationStride)
        => ApplyAnisotropicDiffusion(
            new ReadOnlyImageView2D<byte>(source, width, height, sourceStride),
            in spec,
            destination,
            destinationStride);

    /// <summary>
    /// Applies Vincent (1993) Hybrid Grayscale Morphological Reconstruction (Opening-by-Reconstruction or Closing-by-Reconstruction / Hole Filling).
    /// Unlike standard morphological opening/closing, geodesic reconstruction eliminates dark pinholes or bright spatter
    /// smaller than <see cref="MorphologicalReconstructionSpec.MarkerRadius"/> while leaving surviving object contours 100% undistorted.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void ApplyMorphologicalReconstruction(
        ReadOnlyImageView2D<byte> source,
        in MorphologicalReconstructionSpec spec,
        Span<byte> destination,
        int destinationStride)
    {
        source.EnsureValid();
        spec.EnsureInitialized();
        ValidateDestination(source.Width, source.Height, destination, destinationStride);

        int width = source.Width;
        int height = source.Height;
        int totalPixels = checked(width * height);

        byte[] rentedMask = ArrayPool<byte>.Shared.Rent(totalPixels);
        byte[] rentedTemp = ArrayPool<byte>.Shared.Rent(totalPixels);
        byte[] rentedMarker = ArrayPool<byte>.Shared.Rent(totalPixels);
        int[] rentedQueue = ArrayPool<int>.Shared.Rent(totalPixels);
        byte[] rentedInQueue = ArrayPool<byte>.Shared.Rent(totalPixels);

        try
        {
            Span<byte> mask = rentedMask.AsSpan(0, totalPixels);
            Span<byte> temp = rentedTemp.AsSpan(0, totalPixels);
            Span<byte> marker = rentedMarker.AsSpan(0, totalPixels);
            Span<int> queue = rentedQueue.AsSpan(0, totalPixels);
            Span<byte> inQueue = rentedInQueue.AsSpan(0, totalPixels);

            // [Math Why]: By duality of geodesic reconstruction, Closing-by-Reconstruction (filling dark holes/cracks)
            // on I(x,y) is exact Opening-by-Reconstruction on the inverted image ~I(x,y) = 255 - I(x,y).
            CopyOrInvertSourceToContiguous(source, mask, invert: spec.IsFillDarkHoles);

            SeparableFlatErode(mask, temp, marker, width, height, spec.MarkerRadius);
            ReconstructByDilationVincent(marker, mask, width, height, queue, inQueue);

            CopyOrInvertContiguousToDestination(marker, width, height, destination, destinationStride, invert: spec.IsFillDarkHoles);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedMask);
            ArrayPool<byte>.Shared.Return(rentedTemp);
            ArrayPool<byte>.Shared.Return(rentedMarker);
            ArrayPool<int>.Shared.Return(rentedQueue);
            ArrayPool<byte>.Shared.Return(rentedInQueue);
        }
    }

    /// <summary>
    /// Convenience overload accepting raw strided spans for <see cref="ApplyMorphologicalReconstruction(ReadOnlyImageView2D{byte}, in MorphologicalReconstructionSpec, Span{byte}, int)"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ApplyMorphologicalReconstruction(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        int sourceStride,
        in MorphologicalReconstructionSpec spec,
        Span<byte> destination,
        int destinationStride)
        => ApplyMorphologicalReconstruction(
            new ReadOnlyImageView2D<byte>(source, width, height, sourceStride),
            in spec,
            destination,
            destinationStride);

    private static void EstimateBiquadraticSurfaceCoefficients(
        ReadOnlyImageView2D<byte> source,
        int subsampleStep,
        Span<double> coefficientsOut)
    {
        int width = source.Width;
        int height = source.Height;
        int step = Math.Max(1, Math.Min(subsampleStep, Math.Min(width, height) / MaxDimensionSubsampleDivisor));

        Span<double> ata = stackalloc double[PolynomialNormalMatrixSize];
        Span<double> atb = stackalloc double[PolynomialBasisCount];
        Span<double> basis = stackalloc double[PolynomialBasisCount];
        ata.Clear();
        atb.Clear();

        double halfW = Math.Max(MinNormalizedCoordinateSpan, (width - 1) * HalfDimensionScale);
        double halfH = Math.Max(MinNormalizedCoordinateSpan, (height - 1) * HalfDimensionScale);
        double invHalfW = 1.0 / halfW;
        double invHalfH = 1.0 / halfH;

        for (int y = 0; y < height; y += step)
        {
            double v = (y - halfH) * invHalfH;
            double vv = v * v;
            ReadOnlySpan<byte> row = source.GetRowSpan(y);

            for (int x = 0; x < width; x += step)
            {
                double u = (x - halfW) * invHalfW;
                basis[BasisTermConstantIndex] = 1.0;
                basis[BasisTermUIndex] = u;
                basis[BasisTermVIndex] = v;
                basis[BasisTermUSquaredIndex] = u * u;
                basis[BasisTermUVIndex] = u * v;
                basis[BasisTermVSquaredIndex] = vv;

                double z = row[x];
                for (int r = 0; r < PolynomialBasisCount; r++)
                {
                    double br = basis[r];
                    atb[r] += br * z;
                    int rowBase = r * PolynomialBasisCount;
                    for (int c = r; c < PolynomialBasisCount; c++)
                    {
                        ata[rowBase + c] += br * basis[c];
                    }
                }
            }
        }

        // Symmetrize normal matrix and apply tiny Tikhonov diagonal damping for rank safety on 1D degenerate strips
        for (int r = 0; r < PolynomialBasisCount; r++)
        {
            ata[(r * PolynomialBasisCount) + r] += TikhonovRegularizationEpsilon;
            for (int c = 0; c < r; c++)
            {
                ata[(r * PolynomialBasisCount) + c] = ata[(c * PolynomialBasisCount) + r];
            }
        }

        if (!SmallMatrixSolver.TrySolveLinearSystem(ata, atb, PolynomialBasisCount, coefficientsOut))
        {
            coefficientsOut.Clear();
            coefficientsOut[0] = PolynomialShadingSpec.DefaultTargetBaseGray;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void EvaluateAndCorrectSurfaceSimd(
        ReadOnlyImageView2D<byte> source,
        ReadOnlySpan<double> coeffs,
        byte targetBaseGray,
        bool isMultiplicativeGain,
        Span<byte> destination,
        int destinationStride)
    {
        int width = source.Width;
        int height = source.Height;

        float c0 = (float)coeffs[BasisTermConstantIndex];
        float c1 = (float)coeffs[BasisTermUIndex];
        float c2 = (float)coeffs[BasisTermVIndex];
        float c3 = (float)coeffs[BasisTermUSquaredIndex];
        float c4 = (float)coeffs[BasisTermUVIndex];
        float c5 = (float)coeffs[BasisTermVSquaredIndex];
        float targetBase = targetBaseGray;

        float halfW = MathF.Max(MinNormalizedCoordinateSpan, (width - 1) * HalfDimensionScaleFloat);
        float halfH = MathF.Max(MinNormalizedCoordinateSpan, (height - 1) * HalfDimensionScaleFloat);
        float invHalfW = 1.0f / halfW;
        float invHalfH = 1.0f / halfH;

        Vector256<float> vRamp = Vector256.Create(
            (float)SimdLane0,
            (float)SimdLane1,
            (float)SimdLane2,
            (float)SimdLane3,
            (float)SimdLane4,
            (float)SimdLane5,
            (float)SimdLane6,
            (float)SimdLane7);
        Vector256<float> vHalfW = Vector256.Create(halfW);
        Vector256<float> vInvHalfW = Vector256.Create(invHalfW);
        Vector256<float> vC3 = Vector256.Create(c3);
        Vector256<float> vTargetBase = Vector256.Create(targetBase);
        Vector256<float> vZero = Vector256<float>.Zero;
        Vector256<float> vMaxByte = Vector256.Create((float)byte.MaxValue);
        Vector256<float> vMinBg = Vector256.Create(MinMultiplicativeBackgroundFloor);

        int simdWidth = width & ~(Vector256<float>.Count - 1);

        for (int y = 0; y < height; y++)
        {
            float v = (y - halfH) * invHalfH;
            // [Math Why]: Group row-invariant polynomial terms: B(u,v) = rowConst + u * (rowSlope + c3 * u).
            float rowConst = c0 + (c2 * v) + (c5 * v * v);
            float rowSlope = c1 + (c4 * v);

            Vector256<float> vRowConst = Vector256.Create(rowConst);
            Vector256<float> vRowSlope = Vector256.Create(rowSlope);

            ReadOnlySpan<byte> srcRow = source.GetRowSpan(y);
            Span<byte> dstRow = destination.Slice(y * destinationStride, width);
            ref byte srcRef = ref MemoryMarshal.GetReference(srcRow);
            ref byte dstRef = ref MemoryMarshal.GetReference(dstRow);

            int x = 0;
            if (Vector256.IsHardwareAccelerated && width >= Vector256<float>.Count)
            {
                for (; x < simdWidth; x += Vector256<float>.Count)
                {
                    Vector256<float> vX = Vector256.Create((float)x) + vRamp;
                    Vector256<float> vU = (vX - vHalfW) * vInvHalfW;

                    Vector256<float> vBg = Fma.IsSupported
                        ? Fma.MultiplyAdd(vU, Fma.MultiplyAdd(vC3, vU, vRowSlope), vRowConst)
                        : vRowConst + (vU * (vRowSlope + (vC3 * vU)));

                    Vector256<float> vSrc = Vector256.Create(
                        (float)Unsafe.Add(ref srcRef, x + SimdLane0),
                        (float)Unsafe.Add(ref srcRef, x + SimdLane1),
                        (float)Unsafe.Add(ref srcRef, x + SimdLane2),
                        (float)Unsafe.Add(ref srcRef, x + SimdLane3),
                        (float)Unsafe.Add(ref srcRef, x + SimdLane4),
                        (float)Unsafe.Add(ref srcRef, x + SimdLane5),
                        (float)Unsafe.Add(ref srcRef, x + SimdLane6),
                        (float)Unsafe.Add(ref srcRef, x + SimdLane7));

                    Vector256<float> vCorrected = isMultiplicativeGain
                        ? (vSrc * vTargetBase) / Vector256.Max(vMinBg, vBg)
                        : (vSrc - vBg) + vTargetBase;

                    Vector256<float> vClamped = Vector256.Min(vMaxByte, Vector256.Max(vZero, Vector256.Round(vCorrected)));
                    for (int lane = 0; lane < Vector256<float>.Count; lane++)
                    {
                        Unsafe.Add(ref dstRef, x + lane) = (byte)vClamped.GetElement(lane);
                    }
                }
            }

            for (; x < width; x++)
            {
                float u = (x - halfW) * invHalfW;
                float bg = rowConst + (u * (rowSlope + (c3 * u)));
                float srcVal = Unsafe.Add(ref srcRef, x);
                float corrected = isMultiplicativeGain
                    ? (srcVal * targetBase) / MathF.Max(MinMultiplicativeBackgroundFloor, bg)
                    : (srcVal - bg) + targetBase;
                Unsafe.Add(ref dstRef, x) = (byte)Math.Clamp((int)MathF.Round(corrected), byte.MinValue, byte.MaxValue);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void DiffuseSingleIterationSimd(
        ReadOnlySpan<float> src,
        Span<float> dst,
        int width,
        int height,
        float invKappaSq,
        float scaledLambda)
    {
        Vector256<float> vOne = Vector256.Create(1.0f);
        Vector256<float> vInvKappaSq = Vector256.Create(invKappaSq);
        Vector256<float> vScaledLambda = Vector256.Create(scaledLambda);

        for (int y = 0; y < height; y++)
        {
            int yNorth = y > 0 ? y - 1 : 0;
            int ySouth = y + 1 < height ? y + 1 : height - 1;

            ReadOnlySpan<float> rowNorth = src.Slice(yNorth * width, width);
            ReadOnlySpan<float> rowCenter = src.Slice(y * width, width);
            ReadOnlySpan<float> rowSouth = src.Slice(ySouth * width, width);
            Span<float> rowDst = dst.Slice(y * width, width);

            // Left boundary pixel (x = 0)
            rowDst[0] = DiffuseScalarPixel(
                c: rowCenter[0],
                n: rowNorth[0],
                s: rowSouth[0],
                w: rowCenter[0],
                e: rowCenter[width > 1 ? 1 : 0],
                invKappaSq,
                scaledLambda);

            int x = 1;
            int interiorLimit = width - 1;
            if (Vector256.IsHardwareAccelerated && interiorLimit - 1 >= Vector256<float>.Count)
            {
                ref float nRef = ref MemoryMarshal.GetReference(rowNorth);
                ref float cRef = ref MemoryMarshal.GetReference(rowCenter);
                ref float sRef = ref MemoryMarshal.GetReference(rowSouth);
                ref float dRef = ref MemoryMarshal.GetReference(rowDst);

                int simdLimit = interiorLimit - Vector256<float>.Count;
                for (; x <= simdLimit; x += Vector256<float>.Count)
                {
                    Vector256<float> vC = Vector256.LoadUnsafe(ref cRef, (nuint)x);
                    Vector256<float> dN = Vector256.LoadUnsafe(ref nRef, (nuint)x) - vC;
                    Vector256<float> dS = Vector256.LoadUnsafe(ref sRef, (nuint)x) - vC;
                    Vector256<float> dW = Vector256.LoadUnsafe(ref cRef, (nuint)(x - 1)) - vC;
                    Vector256<float> dE = Vector256.LoadUnsafe(ref cRef, (nuint)(x + 1)) - vC;

                    Vector256<float> fluxN = dN / (vOne + (dN * dN * vInvKappaSq));
                    Vector256<float> fluxS = dS / (vOne + (dS * dS * vInvKappaSq));
                    Vector256<float> fluxW = dW / (vOne + (dW * dW * vInvKappaSq));
                    Vector256<float> fluxE = dE / (vOne + (dE * dE * vInvKappaSq));

                    Vector256<float> totalFlux = fluxN + fluxS + fluxW + fluxE;
                    Vector256<float> updated = vC + (vScaledLambda * totalFlux);
                    updated.StoreUnsafe(ref dRef, (nuint)x);
                }
            }

            for (; x < interiorLimit; x++)
            {
                rowDst[x] = DiffuseScalarPixel(
                    c: rowCenter[x],
                    n: rowNorth[x],
                    s: rowSouth[x],
                    w: rowCenter[x - 1],
                    e: rowCenter[x + 1],
                    invKappaSq,
                    scaledLambda);
            }

            if (width > 1)
            {
                int last = width - 1;
                rowDst[last] = DiffuseScalarPixel(
                    c: rowCenter[last],
                    n: rowNorth[last],
                    s: rowSouth[last],
                    w: rowCenter[last - 1],
                    e: rowCenter[last],
                    invKappaSq,
                    scaledLambda);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float DiffuseScalarPixel(
        float c,
        float n,
        float s,
        float w,
        float e,
        float invKappaSq,
        float scaledLambda)
    {
        float dN = n - c;
        float dS = s - c;
        float dW = w - c;
        float dE = e - c;

        float flux = (dN / (1.0f + (dN * dN * invKappaSq)))
                   + (dS / (1.0f + (dS * dS * invKappaSq)))
                   + (dW / (1.0f + (dW * dW * invKappaSq)))
                   + (dE / (1.0f + (dE * dE * invKappaSq)));
        return c + (scaledLambda * flux);
    }

    private static void SeparableFlatErode(
        ReadOnlySpan<byte> src,
        Span<byte> tempHorizontal,
        Span<byte> dst,
        int width,
        int height,
        int radius)
    {
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> srcRow = src.Slice(y * width, width);
            Span<byte> tmpRow = tempHorizontal.Slice(y * width, width);
            for (int x = 0; x < width; x++)
            {
                int x0 = Math.Max(0, x - radius);
                int x1 = Math.Min(width - 1, x + radius);
                byte minVal = byte.MaxValue;
                for (int k = x0; k <= x1; k++)
                {
                    byte v = srcRow[k];
                    if (v < minVal)
                    {
                        minVal = v;
                    }
                }
                tmpRow[x] = minVal;
            }
        }

        for (int y = 0; y < height; y++)
        {
            int y0 = Math.Max(0, y - radius);
            int y1 = Math.Min(height - 1, y + radius);
            Span<byte> dstRow = dst.Slice(y * width, width);
            tempHorizontal.Slice(y0 * width, width).CopyTo(dstRow);

            for (int ky = y0 + 1; ky <= y1; ky++)
            {
                ReadOnlySpan<byte> rowK = tempHorizontal.Slice(ky * width, width);
                for (int x = 0; x < width; x++)
                {
                    if (rowK[x] < dstRow[x])
                    {
                        dstRow[x] = rowK[x];
                    }
                }
            }
        }
    }

    /// <summary>
    /// Vincent (1993) hybrid raster-antirasters + breadth-first propagation geodesic dilation:
    /// $J^{(k+1)} = \min(\delta_1(J^{(k)}), I)$ until exact morphological convergence.
    /// </summary>
    private static void ReconstructByDilationVincent(
        Span<byte> marker,
        ReadOnlySpan<byte> mask,
        int width,
        int height,
        Span<int> queue,
        Span<byte> inQueue)
    {
        // Pass 1: Forward raster scan (top-to-bottom, left-to-right) propagating N+ filter neighborhood
        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * width;
            for (int x = 0; x < width; x++)
            {
                int idx = rowOffset + x;
                byte maxVal = marker[idx];
                if (x > 0 && marker[idx - 1] > maxVal)
                {
                    maxVal = marker[idx - 1];
                }
                if (y > 0)
                {
                    int upIdx = idx - width;
                    if (marker[upIdx] > maxVal)
                    {
                        maxVal = marker[upIdx];
                    }
                    if (x > 0 && marker[upIdx - 1] > maxVal)
                    {
                        maxVal = marker[upIdx - 1];
                    }
                    if (x + 1 < width && marker[upIdx + 1] > maxVal)
                    {
                        maxVal = marker[upIdx + 1];
                    }
                }

                byte maskVal = mask[idx];
                marker[idx] = maxVal < maskVal ? maxVal : maskVal;
            }
        }

        // Pass 2: Backward anti-raster scan (bottom-to-top, right-to-left) + seed FIFO wavefront queue
        inQueue.Clear();
        int qHead = 0;
        int qTail = 0;
        int qCapacity = queue.Length;

        for (int y = height - 1; y >= 0; y--)
        {
            int rowOffset = y * width;
            for (int x = width - 1; x >= 0; x--)
            {
                int idx = rowOffset + x;
                byte maxVal = marker[idx];
                if (x + 1 < width && marker[idx + 1] > maxVal)
                {
                    maxVal = marker[idx + 1];
                }
                if (y + 1 < height)
                {
                    int downIdx = idx + width;
                    if (marker[downIdx] > maxVal)
                    {
                        maxVal = marker[downIdx];
                    }
                    if (x > 0 && marker[downIdx - 1] > maxVal)
                    {
                        maxVal = marker[downIdx - 1];
                    }
                    if (x + 1 < width && marker[downIdx + 1] > maxVal)
                    {
                        maxVal = marker[downIdx + 1];
                    }
                }

                byte maskVal = mask[idx];
                byte updated = maxVal < maskVal ? maxVal : maskVal;
                marker[idx] = updated;

                // Enqueue if any N- neighbor can still be raised by this pixel
                bool canPropagate =
                    (x + 1 < width && marker[idx + 1] < updated && marker[idx + 1] < mask[idx + 1]) ||
                    (y + 1 < height && marker[idx + width] < updated && marker[idx + width] < mask[idx + width]) ||
                    (y + 1 < height && x > 0 && marker[idx + width - 1] < updated && marker[idx + width - 1] < mask[idx + width - 1]) ||
                    (y + 1 < height && x + 1 < width && marker[idx + width + 1] < updated && marker[idx + width + 1] < mask[idx + width + 1]);

                if (canPropagate)
                {
                    queue[qTail] = idx;
                    qTail = (qTail + 1) % qCapacity;
                    inQueue[idx] = 1;
                }
            }
        }

        // Pass 3: Exact FIFO wavefront propagation to convergence
        while (qHead != qTail)
        {
            int p = queue[qHead];
            qHead = (qHead + 1) % qCapacity;
            inQueue[p] = 0;

            byte pVal = marker[p];
            int py = p / width;
            int px = p - (py * width);

            int yMin = py > 0 ? py - 1 : 0;
            int yMax = py + 1 < height ? py + 1 : height - 1;
            int xMin = px > 0 ? px - 1 : 0;
            int xMax = px + 1 < width ? px + 1 : width - 1;

            for (int ny = yMin; ny <= yMax; ny++)
            {
                int nRow = ny * width;
                for (int nx = xMin; nx <= xMax; nx++)
                {
                    int q = nRow + nx;
                    byte qVal = marker[q];
                    if (qVal < pVal)
                    {
                        byte qMask = mask[q];
                        if (qVal != qMask)
                        {
                            byte newVal = pVal < qMask ? pVal : qMask;
                            if (newVal > qVal)
                            {
                                marker[q] = newVal;
                                if (inQueue[q] == 0)
                                {
                                    queue[qTail] = q;
                                    qTail = (qTail + 1) % qCapacity;
                                    inQueue[q] = 1;
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    private static void ConvertByteViewToContiguousFloat(ReadOnlyImageView2D<byte> source, Span<float> dst)
    {
        int width = source.Width;
        int height = source.Height;
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> srcRow = source.GetRowSpan(y);
            Span<float> dstRow = dst.Slice(y * width, width);
            for (int x = 0; x < width; x++)
            {
                dstRow[x] = srcRow[x];
            }
        }
    }

    private static void ConvertContiguousFloatToStridedByte(
        ReadOnlySpan<float> src,
        int width,
        int height,
        Span<byte> destination,
        int destinationStride)
    {
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<float> srcRow = src.Slice(y * width, width);
            Span<byte> dstRow = destination.Slice(y * destinationStride, width);
            for (int x = 0; x < width; x++)
            {
                dstRow[x] = (byte)Math.Clamp((int)MathF.Round(srcRow[x]), byte.MinValue, byte.MaxValue);
            }
        }
    }

    private static void CopyOrInvertSourceToContiguous(ReadOnlyImageView2D<byte> source, Span<byte> dst, bool invert)
    {
        int width = source.Width;
        int height = source.Height;
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> srcRow = source.GetRowSpan(y);
            Span<byte> dstRow = dst.Slice(y * width, width);
            if (!invert)
            {
                srcRow.CopyTo(dstRow);
            }
            else
            {
                for (int x = 0; x < width; x++)
                {
                    dstRow[x] = (byte)(byte.MaxValue - srcRow[x]);
                }
            }
        }
    }

    private static void CopyOrInvertContiguousToDestination(
        ReadOnlySpan<byte> src,
        int width,
        int height,
        Span<byte> destination,
        int destinationStride,
        bool invert)
    {
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> srcRow = src.Slice(y * width, width);
            Span<byte> dstRow = destination.Slice(y * destinationStride, width);
            if (!invert)
            {
                srcRow.CopyTo(dstRow);
            }
            else
            {
                for (int x = 0; x < width; x++)
                {
                    dstRow[x] = (byte)(byte.MaxValue - srcRow[x]);
                }
            }
        }
    }

    private static void ValidateDestination(int width, int height, Span<byte> destination, int destinationStride)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(destinationStride, width);
        int requiredLength = ((height - 1) * destinationStride) + width;
        if (destination.Length < requiredLength)
        {
            throw new ArgumentException("Destination buffer is smaller than required strided dimensions.", nameof(destination));
        }
    }
}
