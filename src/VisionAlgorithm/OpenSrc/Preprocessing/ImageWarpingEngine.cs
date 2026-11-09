// File: src/VisionAlgorithm/OpenSrc/Preprocessing/ImageWarpingEngine.cs
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using OpenCvSharp;
using VisionAlgorithm.OpenSrc.Calibration;

namespace VisionAlgorithm.OpenSrc.Preprocessing;

/// <summary>
/// Dedicated subpixel geometric image warping engine accelerated via zero-copy native OpenCV C++ AVX2/IPP kernels
/// (<see cref="Cv2.WarpAffine"/>, <see cref="Cv2.WarpPerspective"/>, <see cref="Cv2.InitUndistortRectifyMap"/>, <see cref="Cv2.Remap"/>)
/// and <see cref="Vector256{T}"/> SIMD coordinate generation:
/// 1. Affine &amp; Shear Warping (rotation, anisotropic scale, horizontal/vertical shear, translation)
/// 2. Perspective Keystone &amp; Arbitrary Quad-to-Rectangle Homography Rectification ($3 \times 3$ projective transform)
/// 3. Polar / Annular Ring Unwrapping (circular O-ring, wafer bevel, and ring-pad inspection into flat Cartesian strip)
/// 4. Brown-Conrady Radial &amp; Tangential Lens Distortion Rectification
/// </summary>
public static class ImageWarpingEngine
{
    private const float DegreesToRadians = MathF.PI / 180.0f;
    private const float TwoPi = MathF.PI * 2.0f;
    private const float MinDeterminantFloor = 1e-4f;

    /// <summary>
    /// Executes the unified <see cref="ImageWarpSpec"/> transformation from <paramref name="source"/> into <paramref name="destination"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void ApplyWarp(
        ReadOnlySpan<byte> source,
        int sourceWidth,
        int sourceHeight,
        int sourceStride,
        in ImageWarpSpec spec,
        Span<byte> destination,
        int destinationWidth,
        int destinationHeight,
        int destinationStride)
    {
        spec.EnsureInitialized();
        ValidateWarpBuffers(source, sourceWidth, sourceHeight, sourceStride, destination, destinationWidth, destinationHeight, destinationStride);

        switch (spec.Kind)
        {
            case WarpTransformKind.Affine:
                WarpAffineShear(
                    source,
                    sourceWidth,
                    sourceHeight,
                    sourceStride,
                    spec.AngleDegrees * DegreesToRadians,
                    spec.ScaleX,
                    spec.ScaleY,
                    spec.ShearX,
                    spec.ShearY,
                    destination,
                    destinationWidth,
                    destinationHeight,
                    destinationStride);
                break;

            case WarpTransformKind.PerspectiveKeystone:
                WarpPerspectiveKeystone(
                    source,
                    sourceWidth,
                    sourceHeight,
                    sourceStride,
                    spec.KeystoneX,
                    spec.KeystoneY,
                    spec.AngleDegrees * DegreesToRadians,
                    destination,
                    destinationWidth,
                    destinationHeight,
                    destinationStride);
                break;

            case WarpTransformKind.PolarAnnularUnwrap:
                float cx = sourceWidth * 0.5f;
                float cy = sourceHeight * 0.5f;
                float outerRadius = MathF.Min(cx, cy);
                float innerRadius = outerRadius * spec.PolarInnerRadiusRatio;
                WarpPolarUnwrap(
                    source,
                    sourceWidth,
                    sourceHeight,
                    sourceStride,
                    new Vector2(cx, cy),
                    innerRadius,
                    outerRadius,
                    spec.AngleDegrees * DegreesToRadians,
                    destination,
                    destinationWidth,
                    destinationHeight,
                    destinationStride);
                break;

            case WarpTransformKind.BrownConradyUndistort:
                float focal = MathF.Max(sourceWidth, sourceHeight);
                var lensModel = new BrownConradyModel(
                    focalLengthX: focal,
                    focalLengthY: focal,
                    principalPointX: sourceWidth * 0.5f,
                    principalPointY: sourceHeight * 0.5f,
                    k1: spec.RadialK1,
                    k2: spec.RadialK2,
                    k3: 0f,
                    p1: spec.ShearX * 0.01f,
                    p2: spec.ShearY * 0.01f);
                WarpLensUndistort(
                    source,
                    sourceWidth,
                    sourceHeight,
                    sourceStride,
                    in lensModel,
                    destination,
                    destinationWidth,
                    destinationHeight,
                    destinationStride);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(spec));
        }
    }

    /// <summary>
    /// Applies a 2D Affine + Shear transformation around the image center via zero-copy OpenCV native C++ <see cref="Cv2.WarpAffine"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static unsafe void WarpAffineShear(
        ReadOnlySpan<byte> source,
        int sourceWidth,
        int sourceHeight,
        int sourceStride,
        float angleRadians,
        float scaleX,
        float scaleY,
        float shearX,
        float shearY,
        Span<byte> destination,
        int destinationWidth,
        int destinationHeight,
        int destinationStride)
    {
        ValidateWarpBuffers(source, sourceWidth, sourceHeight, sourceStride, destination, destinationWidth, destinationHeight, destinationStride);

        float cos = MathF.Cos(angleRadians);
        float sin = MathF.Sin(angleRadians);

        // Forward matrix M = R(theta) * [scaleX, shearX; shearY, scaleY]
        float m00 = (cos * scaleX) - (sin * shearY);
        float m01 = (cos * shearX) - (sin * scaleY);
        float m10 = (sin * scaleX) + (cos * shearY);
        float m11 = (sin * shearX) + (cos * scaleY);

        float det = (m00 * m11) - (m01 * m10);
        float invDet = MathF.Abs(det) > MinDeterminantFloor
            ? 1.0f / det
            : (det >= 0f ? 1.0f / MinDeterminantFloor : -1.0f / MinDeterminantFloor);

        double i00 = m11 * invDet;
        double i01 = -m01 * invDet;
        double i10 = -m10 * invDet;
        double i11 = m00 * invDet;

        double srcCenterX = (sourceWidth * 0.5) - 0.5;
        double srcCenterY = (sourceHeight * 0.5) - 0.5;
        double u0 = 0.5 - (destinationWidth * 0.5);
        double v0 = 0.5 - (destinationHeight * 0.5);

        using var invAffineMat = new Mat(2, 3, MatType.CV_64FC1);
        var mSpan = new Span<double>((void*)invAffineMat.DataPointer, 6);
        mSpan[0] = i00;
        mSpan[1] = i01;
        mSpan[2] = srcCenterX + (i00 * u0) + (i01 * v0);
        mSpan[3] = i10;
        mSpan[4] = i11;
        mSpan[5] = srcCenterY + (i10 * u0) + (i11 * v0);

        ExecuteNativeWarpAffine(source, sourceWidth, sourceHeight, sourceStride, invAffineMat, destination, destinationWidth, destinationHeight, destinationStride);
    }

    /// <summary>
    /// Applies a 2D Perspective Keystone tilt $(g_x, g_y)$ and in-plane rotation $\theta$ via zero-copy OpenCV native C++ <see cref="Cv2.WarpPerspective"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static unsafe void WarpPerspectiveKeystone(
        ReadOnlySpan<byte> source,
        int sourceWidth,
        int sourceHeight,
        int sourceStride,
        float keystoneX,
        float keystoneY,
        float angleRadians,
        Span<byte> destination,
        int destinationWidth,
        int destinationHeight,
        int destinationStride)
    {
        ValidateWarpBuffers(source, sourceWidth, sourceHeight, sourceStride, destination, destinationWidth, destinationHeight, destinationStride);

        double cos = Math.Cos(angleRadians);
        double sin = Math.Sin(angleRadians);
        double srcCenterX = (sourceWidth * 0.5) - 0.5;
        double srcCenterY = (sourceHeight * 0.5) - 0.5;
        double dstHalfW = Math.Max(0.5, destinationWidth * 0.5);
        double dstHalfH = Math.Max(0.5, destinationHeight * 0.5);
        double u0 = 0.5 - dstHalfW;
        double v0 = 0.5 - dstHalfH;

        double g0 = keystoneX / dstHalfW;
        double g1 = keystoneY / dstHalfH;
        double g2 = 1.0 + (g0 * u0) + (g1 * v0);

        using var invHomographyMat = new Mat(3, 3, MatType.CV_64FC1);
        var hSpan = new Span<double>((void*)invHomographyMat.DataPointer, 9);
        hSpan[0] = (srcCenterX * g0) + cos;
        hSpan[1] = (srcCenterX * g1) - sin;
        hSpan[2] = (srcCenterX * g2) + (cos * u0) - (sin * v0);
        hSpan[3] = (srcCenterY * g0) + sin;
        hSpan[4] = (srcCenterY * g1) + cos;
        hSpan[5] = (srcCenterY * g2) + (sin * u0) + (cos * v0);
        hSpan[6] = g0;
        hSpan[7] = g1;
        hSpan[8] = g2;

        ExecuteNativeWarpPerspective(source, sourceWidth, sourceHeight, sourceStride, invHomographyMat, destination, destinationWidth, destinationHeight, destinationStride);
    }

    /// <summary>
    /// Rectifies an arbitrary 4-corner quadrilateral <paramref name="sourceQuadCorners"/> in the source image
    /// into a fronto-parallel $(destinationWidth \times destinationHeight)$ rectangle via Hartley DLT + Gauss-Newton homography
    /// and zero-copy OpenCV native C++ <see cref="Cv2.WarpPerspective"/>.
    /// </summary>
    public static unsafe void WarpQuadToRect(
        ReadOnlySpan<byte> source,
        int sourceWidth,
        int sourceHeight,
        int sourceStride,
        ReadOnlySpan<Vector2> sourceQuadCorners,
        Span<byte> destination,
        int destinationWidth,
        int destinationHeight,
        int destinationStride)
    {
        if (sourceQuadCorners.Length != 4)
        {
            throw new ArgumentException("Exactly 4 quadrilateral corners (TopLeft, TopRight, BottomRight, BottomLeft) are required.", nameof(sourceQuadCorners));
        }
        ValidateWarpBuffers(source, sourceWidth, sourceHeight, sourceStride, destination, destinationWidth, destinationHeight, destinationStride);

        Span<Vector2> dstCorners =
        [
            new Vector2(0f, 0f),
            new Vector2(destinationWidth - 1f, 0f),
            new Vector2(destinationWidth - 1f, destinationHeight - 1f),
            new Vector2(0f, destinationHeight - 1f)
        ];

        PlanarHomography2D dstToSrc = NPointHomographyCalibrator.Calibrate(dstCorners, sourceQuadCorners);

        using var invHomographyMat = new Mat(3, 3, MatType.CV_64FC1);
        var hSpan = new Span<double>((void*)invHomographyMat.DataPointer, 9);
        dstToSrc.ForwardMatrix.CopyTo(hSpan);

        ExecuteNativeWarpPerspective(source, sourceWidth, sourceHeight, sourceStride, invHomographyMat, destination, destinationWidth, destinationHeight, destinationStride);
    }

    /// <summary>
    /// Unwraps a circular or annular ring region $[r_{\text{in}}, r_{\text{out}}] \times [\theta_0, \theta_0 + 2\pi)$
    /// into a flat rectangular strip using <see cref="Vector256{T}"/> SIMD coordinate maps and OpenCV native C++ <see cref="Cv2.Remap"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static unsafe void WarpPolarUnwrap(
        ReadOnlySpan<byte> source,
        int sourceWidth,
        int sourceHeight,
        int sourceStride,
        Vector2 center,
        float innerRadius,
        float outerRadius,
        float startAngleRadians,
        Span<byte> destination,
        int destinationWidth,
        int destinationHeight,
        int destinationStride)
    {
        ValidateWarpBuffers(source, sourceWidth, sourceHeight, sourceStride, destination, destinationWidth, destinationHeight, destinationStride);
        if (innerRadius < 0f || outerRadius <= innerRadius)
        {
            throw new ArgumentOutOfRangeException(nameof(outerRadius), "outerRadius must be > innerRadius >= 0.");
        }

        using var mapXMat = new Mat(destinationHeight, destinationWidth, MatType.CV_32FC1);
        using var mapYMat = new Mat(destinationHeight, destinationWidth, MatType.CV_32FC1);

        Span<float> cosLut = destinationWidth <= 2048 ? stackalloc float[destinationWidth] : new float[destinationWidth];
        Span<float> sinLut = destinationWidth <= 2048 ? stackalloc float[destinationWidth] : new float[destinationWidth];

        float angleStep = TwoPi / destinationWidth;
        for (int x = 0; x < destinationWidth; x++)
        {
            float theta = startAngleRadians + ((x + 0.5f) * angleStep);
            (sinLut[x], cosLut[x]) = MathF.SinCos(theta);
        }

        float radiusSpan = outerRadius - innerRadius;
        float invHeight = 1.0f / destinationHeight;
        float cx = center.X - 0.5f;
        float cy = center.Y - 0.5f;

        int totalPixels = checked(destinationWidth * destinationHeight);
        var mapXSpan = new Span<float>((void*)mapXMat.DataPointer, totalPixels);
        var mapYSpan = new Span<float>((void*)mapYMat.DataPointer, totalPixels);
        ref float cosBase = ref MemoryMarshal.GetReference(cosLut);
        ref float sinBase = ref MemoryMarshal.GetReference(sinLut);

        for (int y = 0; y < destinationHeight; y++)
        {
            float radius = innerRadius + (((y + 0.5f) * invHeight) * radiusSpan);
            int rowOffset = y * destinationWidth;
            ref float mxRow = ref MemoryMarshal.GetReference(mapXSpan.Slice(rowOffset, destinationWidth));
            ref float myRow = ref MemoryMarshal.GetReference(mapYSpan.Slice(rowOffset, destinationWidth));

            int x = 0;
            if (Vector256.IsHardwareAccelerated && destinationWidth >= Vector256<float>.Count)
            {
                int vecCount = Vector256<float>.Count;
                int limit = destinationWidth & ~(vecCount - 1);
                Vector256<float> vCx = Vector256.Create(cx);
                Vector256<float> vCy = Vector256.Create(cy);
                Vector256<float> vRad = Vector256.Create(radius);

                for (; x < limit; x += vecCount)
                {
                    Vector256<float> vCos = Vector256.LoadUnsafe(ref cosBase, (nuint)x);
                    Vector256<float> vSin = Vector256.LoadUnsafe(ref sinBase, (nuint)x);
                    Vector256<float> vSx = Fma.IsSupported ? Fma.MultiplyAdd(vRad, vCos, vCx) : vCx + (vRad * vCos);
                    Vector256<float> vSy = Fma.IsSupported ? Fma.MultiplyAdd(vRad, vSin, vCy) : vCy + (vRad * vSin);
                    vSx.StoreUnsafe(ref mxRow, (nuint)x);
                    vSy.StoreUnsafe(ref myRow, (nuint)x);
                }
            }

            for (; x < destinationWidth; x++)
            {
                Unsafe.Add(ref mxRow, x) = cx + (radius * Unsafe.Add(ref cosBase, x));
                Unsafe.Add(ref myRow, x) = cy + (radius * Unsafe.Add(ref sinBase, x));
            }
        }

        ExecuteNativeRemap(source, sourceWidth, sourceHeight, sourceStride, mapXMat, mapYMat, destination, destinationWidth, destinationHeight, destinationStride);
    }

    /// <summary>
    /// Rectifies radial and tangential lens distortion across the image via OpenCV native C++
    /// <see cref="Cv2.InitUndistortRectifyMap"/> and <see cref="Cv2.Remap"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static unsafe void WarpLensUndistort(
        ReadOnlySpan<byte> source,
        int sourceWidth,
        int sourceHeight,
        int sourceStride,
        in BrownConradyModel model,
        Span<byte> destination,
        int destinationWidth,
        int destinationHeight,
        int destinationStride)
    {
        model.EnsureValid();
        ValidateWarpBuffers(source, sourceWidth, sourceHeight, sourceStride, destination, destinationWidth, destinationHeight, destinationStride);

        using var cameraMatrix = new Mat(3, 3, MatType.CV_64FC1, Scalar.All(0));
        var kSpan = new Span<double>((void*)cameraMatrix.DataPointer, 9);
        kSpan[0] = model.FocalLengthX;
        kSpan[2] = model.PrincipalPointX;
        kSpan[4] = model.FocalLengthY;
        kSpan[5] = model.PrincipalPointY;
        kSpan[8] = 1.0;

        double scaleX = (double)destinationWidth / sourceWidth;
        double scaleY = (double)destinationHeight / sourceHeight;
        using var newCameraMatrix = new Mat(3, 3, MatType.CV_64FC1, Scalar.All(0));
        var nkSpan = new Span<double>((void*)newCameraMatrix.DataPointer, 9);
        nkSpan[0] = model.FocalLengthX * scaleX;
        nkSpan[2] = model.PrincipalPointX * scaleX;
        nkSpan[4] = model.FocalLengthY * scaleY;
        nkSpan[5] = model.PrincipalPointY * scaleY;
        nkSpan[8] = 1.0;

        using var distCoeffs = new Mat(1, 5, MatType.CV_64FC1);
        var dSpan = new Span<double>((void*)distCoeffs.DataPointer, 5);
        dSpan[0] = model.K1;
        dSpan[1] = model.K2;
        dSpan[2] = model.P1;
        dSpan[3] = model.P2;
        dSpan[4] = model.K3;

        using var emptyRectification = new Mat();
        using var map1 = new Mat();
        using var map2 = new Mat();
        Cv2.InitUndistortRectifyMap(
            cameraMatrix,
            distCoeffs,
            emptyRectification,
            newCameraMatrix,
            new Size(destinationWidth, destinationHeight),
            MatType.CV_32FC1,
            map1,
            map2);

        ExecuteNativeRemap(source, sourceWidth, sourceHeight, sourceStride, map1, map2, destination, destinationWidth, destinationHeight, destinationStride);
    }

    private static unsafe void ExecuteNativeWarpAffine(
        ReadOnlySpan<byte> source,
        int sourceWidth,
        int sourceHeight,
        int sourceStride,
        Mat invAffineMat,
        Span<byte> destination,
        int destinationWidth,
        int destinationHeight,
        int destinationStride)
    {
        bool isAlias = Unsafe.AreSame(
            ref MemoryMarshal.GetReference(source),
            ref MemoryMarshal.GetReference(destination));

        fixed (byte* pSrc = source)
        fixed (byte* pDst = destination)
        {
            using var srcMat = Mat.FromPixelData(sourceHeight, sourceWidth, MatType.CV_8UC1, (nint)pSrc, sourceStride);
            if (isAlias)
            {
                using var tempDst = new Mat(destinationHeight, destinationWidth, MatType.CV_8UC1);
                Cv2.WarpAffine(
                    srcMat,
                    tempDst,
                    invAffineMat,
                    new Size(destinationWidth, destinationHeight),
                    InterpolationFlags.Linear | InterpolationFlags.WarpInverseMap,
                    BorderTypes.Replicate);
                using var dstMat = Mat.FromPixelData(destinationHeight, destinationWidth, MatType.CV_8UC1, (nint)pDst, destinationStride);
                tempDst.CopyTo(dstMat);
            }
            else
            {
                using var dstMat = Mat.FromPixelData(destinationHeight, destinationWidth, MatType.CV_8UC1, (nint)pDst, destinationStride);
                Cv2.WarpAffine(
                    srcMat,
                    dstMat,
                    invAffineMat,
                    new Size(destinationWidth, destinationHeight),
                    InterpolationFlags.Linear | InterpolationFlags.WarpInverseMap,
                    BorderTypes.Replicate);
            }
        }
    }

    private static unsafe void ExecuteNativeWarpPerspective(
        ReadOnlySpan<byte> source,
        int sourceWidth,
        int sourceHeight,
        int sourceStride,
        Mat invHomographyMat,
        Span<byte> destination,
        int destinationWidth,
        int destinationHeight,
        int destinationStride)
    {
        bool isAlias = Unsafe.AreSame(
            ref MemoryMarshal.GetReference(source),
            ref MemoryMarshal.GetReference(destination));

        fixed (byte* pSrc = source)
        fixed (byte* pDst = destination)
        {
            using var srcMat = Mat.FromPixelData(sourceHeight, sourceWidth, MatType.CV_8UC1, (nint)pSrc, sourceStride);
            if (isAlias)
            {
                using var tempDst = new Mat(destinationHeight, destinationWidth, MatType.CV_8UC1);
                Cv2.WarpPerspective(
                    srcMat,
                    tempDst,
                    invHomographyMat,
                    new Size(destinationWidth, destinationHeight),
                    InterpolationFlags.Linear | InterpolationFlags.WarpInverseMap,
                    BorderTypes.Replicate);
                using var dstMat = Mat.FromPixelData(destinationHeight, destinationWidth, MatType.CV_8UC1, (nint)pDst, destinationStride);
                tempDst.CopyTo(dstMat);
            }
            else
            {
                using var dstMat = Mat.FromPixelData(destinationHeight, destinationWidth, MatType.CV_8UC1, (nint)pDst, destinationStride);
                Cv2.WarpPerspective(
                    srcMat,
                    dstMat,
                    invHomographyMat,
                    new Size(destinationWidth, destinationHeight),
                    InterpolationFlags.Linear | InterpolationFlags.WarpInverseMap,
                    BorderTypes.Replicate);
            }
        }
    }

    private static unsafe void ExecuteNativeRemap(
        ReadOnlySpan<byte> source,
        int sourceWidth,
        int sourceHeight,
        int sourceStride,
        Mat mapX,
        Mat mapY,
        Span<byte> destination,
        int destinationWidth,
        int destinationHeight,
        int destinationStride)
    {
        bool isAlias = Unsafe.AreSame(
            ref MemoryMarshal.GetReference(source),
            ref MemoryMarshal.GetReference(destination));

        fixed (byte* pSrc = source)
        fixed (byte* pDst = destination)
        {
            using var srcMat = Mat.FromPixelData(sourceHeight, sourceWidth, MatType.CV_8UC1, (nint)pSrc, sourceStride);
            if (isAlias)
            {
                using var tempDst = new Mat(destinationHeight, destinationWidth, MatType.CV_8UC1);
                Cv2.Remap(srcMat, tempDst, mapX, mapY, InterpolationFlags.Linear, BorderTypes.Replicate);
                using var dstMat = Mat.FromPixelData(destinationHeight, destinationWidth, MatType.CV_8UC1, (nint)pDst, destinationStride);
                tempDst.CopyTo(dstMat);
            }
            else
            {
                using var dstMat = Mat.FromPixelData(destinationHeight, destinationWidth, MatType.CV_8UC1, (nint)pDst, destinationStride);
                Cv2.Remap(srcMat, dstMat, mapX, mapY, InterpolationFlags.Linear, BorderTypes.Replicate);
            }
        }
    }

    private static void ValidateWarpBuffers(
        ReadOnlySpan<byte> source,
        int sourceWidth,
        int sourceHeight,
        int sourceStride,
        Span<byte> destination,
        int destinationWidth,
        int destinationHeight,
        int destinationStride)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || destinationWidth <= 0 || destinationHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceWidth), "Source and destination dimensions must be > 0.");
        }
        if (sourceStride < sourceWidth || destinationStride < destinationWidth)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceStride), "Row stride must be >= width.");
        }
        int minSrcLen = ((sourceHeight - 1) * sourceStride) + sourceWidth;
        int minDstLen = ((destinationHeight - 1) * destinationStride) + destinationWidth;
        if (source.Length < minSrcLen || destination.Length < minDstLen)
        {
            throw new ArgumentException("Source or destination buffer is smaller than required strided dimensions.");
        }
    }
}
