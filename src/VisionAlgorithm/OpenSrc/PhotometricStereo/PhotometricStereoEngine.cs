using System.Buffers;

namespace VisionAlgorithm.OpenSrc.PhotometricStereo;

/// <summary>
/// 4-directional Lambertian Photometric Stereo engine (Woodham formulation).
/// Reconstructs per-pixel surface normal vectors $\mathbf{n}(x,y) = (n_x, n_y, n_z)$ and reflectance albedo $\rho(x,y)$
/// from 4 directional light images (East $0^\circ$, North $90^\circ$, West $180^\circ$, South $270^\circ$ at slant angle $\sigma$),
/// then computes differential surface Mean Curvature $H = \frac{1}{2}(\frac{\partial p}{\partial x} + \frac{\partial q}{\partial y})$
/// and Gaussian Curvature $K$ to isolate 3D micro-dents, scratches, and embossing regardless of 2D albedo contrast.
/// </summary>
public static class PhotometricStereoEngine
{
    private const double DegToRad = Math.PI / 180.0;
    private const double DefaultSlantDeg = 45.0;
    private const double MinNormalZ = 1e-3;
    private const byte MaxByteIntensity = 255;

    /// <summary>
    /// Evaluates a 4-quadrant composite Photometric Stereo frame (Top-Left = East $0^\circ$, Top-Right = North $90^\circ$,
    /// Bottom-Left = West $180^\circ$, Bottom-Right = South $270^\circ$) or synthesizes directional lighting on a single-frame ROI.
    /// </summary>
    public static PhotometricStereoResult AnalyzeCompositeOrSingleFrame(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        int stride,
        double slantAngleDeg = DefaultSlantDeg,
        double curvatureThreshold = 0.085)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width);

        var quadW = width / 2;
        var quadH = height / 2;
        if (quadW < 16 || quadH < 16)
        {
            return new PhotometricStereoResult(width, height, 0, 0, 0, 0, 0, 0, 0, 0, [], []);
        }

        var quadPixels = quadW * quadH;
        var iEastPool = ArrayPool<byte>.Shared.Rent(quadPixels);
        var iNorthPool = ArrayPool<byte>.Shared.Rent(quadPixels);
        var iWestPool = ArrayPool<byte>.Shared.Rent(quadPixels);
        var iSouthPool = ArrayPool<byte>.Shared.Rent(quadPixels);

        try
        {
            var iEast = iEastPool.AsSpan(0, quadPixels);
            var iNorth = iNorthPool.AsSpan(0, quadPixels);
            var iWest = iWestPool.AsSpan(0, quadPixels);
            var iSouth = iSouthPool.AsSpan(0, quadPixels);

            for (var y = 0; y < quadH; y++)
            {
                var topRow = y * stride;
                var botRow = (y + quadH) * stride;
                var dstRow = y * quadW;

                for (var x = 0; x < quadW; x++)
                {
                    iEast[dstRow + x] = source[topRow + x];
                    iNorth[dstRow + x] = source[topRow + x + quadW];
                    iWest[dstRow + x] = source[botRow + x];
                    iSouth[dstRow + x] = source[botRow + x + quadW];
                }
            }

            return SolveFourLightLambertian(
                iEast,
                iNorth,
                iWest,
                iSouth,
                quadW,
                quadH,
                slantAngleDeg,
                curvatureThreshold);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(iEastPool);
            ArrayPool<byte>.Shared.Return(iNorthPool);
            ArrayPool<byte>.Shared.Return(iWestPool);
            ArrayPool<byte>.Shared.Return(iSouthPool);
        }
    }

    /// <summary>
    /// Solves the 4-directional Lambertian linear system $\mathbf{I} = \rho \mathbf{L} \mathbf{n}$ in closed least-squares form:
    /// For symmetric ring illumination at slant $\sigma$ and azimuths $\tau \in \{0^\circ, 90^\circ, 180^\circ, 270^\circ\}$:
    /// $g_x = \frac{I_E - I_W}{2 \sin\sigma}$, $g_y = \frac{I_S - I_N}{2 \sin\sigma}$, $g_z = \frac{I_E + I_N + I_W + I_S}{4 \cos\sigma}$,
    /// $\rho = \|\mathbf{g}\|_2$, and $\mathbf{n} = \mathbf{g} / \rho$.
    /// </summary>
    public static PhotometricStereoResult SolveFourLightLambertian(
        ReadOnlySpan<byte> imageEast,
        ReadOnlySpan<byte> imageNorth,
        ReadOnlySpan<byte> imageWest,
        ReadOnlySpan<byte> imageSouth,
        int width,
        int height,
        double slantAngleDeg = DefaultSlantDeg,
        double curvatureThreshold = 0.085)
    {
        var pixelCount = width * height;
        var safeSlant = Math.Clamp(slantAngleDeg, 15.0, 75.0) * DegToRad;
        var sinSlant = Math.Sin(safeSlant);
        var cosSlant = Math.Cos(safeSlant);
        var invTwoSin = 1.0 / (2.0 * sinSlant);
        var invFourCos = 1.0 / (4.0 * cosSlant);

        var nxPool = ArrayPool<double>.Shared.Rent(pixelCount);
        var nyPool = ArrayPool<double>.Shared.Rent(pixelCount);
        var nzPool = ArrayPool<double>.Shared.Rent(pixelCount);

        var albedoBytes = new byte[pixelCount];
        var curvatureBytes = new byte[pixelCount];

        try
        {
            var nx = nxPool.AsSpan(0, pixelCount);
            var ny = nyPool.AsSpan(0, pixelCount);
            var nz = nzPool.AsSpan(0, pixelCount);

            var sumAlbedo = 0.0;
            for (var i = 0; i < pixelCount; i++)
            {
                var iE = (double)imageEast[i];
                var iN = (double)imageNorth[i];
                var iW = (double)imageWest[i];
                var iS = (double)imageSouth[i];

                var gx = (iE - iW) * invTwoSin;
                var gy = (iS - iN) * invTwoSin;
                var gz = (iE + iN + iW + iS) * invFourCos;

                var rho = Math.Sqrt((gx * gx) + (gy * gy) + (gz * gz));
                sumAlbedo += rho;
                albedoBytes[i] = (byte)Math.Clamp((int)Math.Round(rho), 0, MaxByteIntensity);

                if (rho > 1.0)
                {
                    nx[i] = gx / rho;
                    ny[i] = gy / rho;
                    nz[i] = Math.Max(MinNormalZ, gz / rho);
                }
                else
                {
                    nx[i] = 0.0;
                    ny[i] = 0.0;
                    nz[i] = 1.0;
                }
            }

            var maxAbsMeanCurvature = 0.0;
            var maxAbsGaussianCurvature = 0.0;
            var defectPixels = 0;
            var peakCurvature = 0.0;
            var peakX = width * 0.5;
            var peakY = height * 0.5;
            var safeThreshold = Math.Clamp(curvatureThreshold, 0.02, 0.60);

            var defectMaskPool = ArrayPool<byte>.Shared.Rent(pixelCount);
            try
            {
                var defectMask = defectMaskPool.AsSpan(0, pixelCount);
                defectMask.Clear();

                for (var y = 2; y < height - 2; y++)
                {
                    var row = y * width;
                    for (var x = 2; x < width - 2; x++)
                    {
                        var idx = row + x;
                        var dnxDx = (nx[idx + 1] - nx[idx - 1]) * 0.5;
                        var dnxDy = (nx[idx + width] - nx[idx - width]) * 0.5;
                        var dnyDx = (ny[idx + 1] - ny[idx - 1]) * 0.5;
                        var dnyDy = (ny[idx + width] - ny[idx - width]) * 0.5;

                        var meanCurv = 0.5 * (dnxDx + dnyDy);
                        var gaussCurv = (dnxDx * dnyDy) - (dnxDy * dnyDx);

                        var absH = Math.Abs(meanCurv);
                        var absK = Math.Abs(gaussCurv);

                        if (absH > maxAbsMeanCurvature)
                        {
                            maxAbsMeanCurvature = absH;
                        }

                        if (absK > maxAbsGaussianCurvature)
                        {
                            maxAbsGaussianCurvature = absK;
                        }

                        var vis = 128.0 + (meanCurv * 420.0);
                        curvatureBytes[idx] = (byte)Math.Clamp((int)Math.Round(vis), 0, MaxByteIntensity);

                        if (absH >= safeThreshold)
                        {
                            defectPixels++;
                            defectMask[idx] = 1;
                            if (absH > peakCurvature)
                            {
                                peakCurvature = absH;
                                peakX = x;
                                peakY = y;
                            }
                        }
                    }
                }

                var defectRegions = CountConnectedDefectRegions(defectMask, width, height, minArea: 4);

                return new PhotometricStereoResult(
                    width,
                    height,
                    pixelCount > 0 ? sumAlbedo / pixelCount : 0.0,
                    maxAbsMeanCurvature,
                    maxAbsGaussianCurvature,
                    defectPixels,
                    defectRegions,
                    peakX,
                    peakY,
                    peakCurvature,
                    albedoBytes,
                    curvatureBytes);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(defectMaskPool);
            }
        }
        finally
        {
            ArrayPool<double>.Shared.Return(nxPool);
            ArrayPool<double>.Shared.Return(nyPool);
            ArrayPool<double>.Shared.Return(nzPool);
        }
    }

    private static int CountConnectedDefectRegions(Span<byte> mask, int width, int height, int minArea)
    {
        var regions = 0;
        var queuePool = ArrayPool<int>.Shared.Rent(width * height);
        try
        {
            var queue = queuePool.AsSpan(0, width * height);
            for (var y = 1; y < height - 1; y++)
            {
                var row = y * width;
                for (var x = 1; x < width - 1; x++)
                {
                    var startIdx = row + x;
                    if (mask[startIdx] != 1)
                    {
                        continue;
                    }

                    var head = 0;
                    var tail = 0;
                    queue[tail++] = startIdx;
                    mask[startIdx] = 2;
                    var area = 0;

                    while (head < tail)
                    {
                        var curr = queue[head++];
                        area++;
                        var cy = curr / width;
                        var cx = curr - (cy * width);

                        for (var dy = -1; dy <= 1; dy++)
                        {
                            var ny = cy + dy;
                            if ((uint)ny >= (uint)height)
                            {
                                continue;
                            }

                            var nRow = ny * width;
                            for (var dx = -1; dx <= 1; dx++)
                            {
                                var nx = cx + dx;
                                if ((uint)nx >= (uint)width)
                                {
                                    continue;
                                }

                                var nIdx = nRow + nx;
                                if (mask[nIdx] == 1)
                                {
                                    mask[nIdx] = 2;
                                    queue[tail++] = nIdx;
                                }
                            }
                        }
                    }

                    if (area >= minArea)
                    {
                        regions++;
                    }
                }
            }

            return regions;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(queuePool);
        }
    }
}
