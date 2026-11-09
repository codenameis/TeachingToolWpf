// File: TeachingReadiness/SubPixelTeachingReadinessEngine.cs
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using TeachingToolWpf.Fluent2.Common;

namespace TeachingToolWpf.Fluent2.TeachingReadiness;

public sealed class SubPixelTeachingReadinessEngine : ITeachingReadinessAnalyzer
{
    private const float GradientWeight = 0.45f; // [Math Why]: Sobel gradient energy carries the primary weight for sub-pixel edge localization variance.
    private const float ContrastWeight = 0.30f; // [Math Why]: Intensity span stabilizes correlation and edge thresholding against sensor noise.
    private const float IsotropyWeight = 0.25f; // [Math Why]: Structure tensor eigenvalue ratio penalizes 1D aperture ambiguity.
    private const double ZeroEigenvalueEpsilon = 1e-6; // [Math Why]: Guards lambda_min / lambda_max against division by zero on flat regions.
    private const double CrossTermDiscriminantFactor = 4.0; // [Math Why]: 2x2 symmetric tensor eigenvalue discriminant is (Jxx - Jyy)^2 + 4 * Jxy^2.
    private const double HalfCircleDegrees = 180.0; // [Math Why]: Principal structure tensor orientation lies in [0, 180) degrees via 0.5 * atan2(2*Jxy, Jxx - Jyy).

    public TeachingResult<TeachingReadinessReport> Analyze(GrayImageView2D image, in TeachingReadinessSpec spec)
    {
        if (!image.IsInitialized)
        {
            return TeachingResult<TeachingReadinessReport>.Failure("Image view is not initialized.");
        }

        if (!spec.IsInitialized)
        {
            return TeachingResult<TeachingReadinessReport>.Failure("Teaching readiness specification is not initialized.");
        }

        if (!spec.ValidateAgainstImage(image.Width, image.Height))
        {
            return TeachingResult<TeachingReadinessReport>.Failure("ROI bounds exceed the image dimensions or are smaller than 3x3.");
        }

        RoiPixelBounds roi = spec.Roi;
        int interiorWidth = roi.InteriorWidth;
        int interiorHeight = roi.InteriorHeight;
        int evaluatedPixels = interiorWidth * interiorHeight;
        if (evaluatedPixels <= 0)
        {
            return TeachingResult<TeachingReadinessReport>.Failure("ROI interior sample count must be positive.");
        }

        AccumulateStructureTensor(
            image,
            roi,
            out long sumGxSquared,
            out long sumGySquared,
            out long sumGxGy,
            out byte minIntensity,
            out byte maxIntensity);

        double invCount = 1.0 / evaluatedPixels;
        double jxx = sumGxSquared * invCount;
        double jyy = sumGySquared * invCount;
        double jxy = sumGxGy * invCount;

        float rmsGradient = (float)Math.Sqrt(jxx + jyy);
        float contrastSpan = maxIntensity - minIntensity;

        double trace = jxx + jyy;
        double diff = jxx - jyy;
        double discriminant = Math.Sqrt((diff * diff) + (CrossTermDiscriminantFactor * jxy * jxy));
        double lambdaMax = 0.5 * (trace + discriminant);
        double lambdaMin = Math.Max(0.0, 0.5 * (trace - discriminant));
        float isotropy = (lambdaMax > ZeroEigenvalueEpsilon)
            ? (float)Math.Clamp(lambdaMin / (lambdaMax + ZeroEigenvalueEpsilon), 0.0, 1.0)
            : 0.0f;

        double orientationRadians = 0.5 * Math.Atan2(2.0 * jxy, diff);
        double orientationDegrees = orientationRadians * (HalfCircleDegrees / Math.PI);
        if (orientationDegrees < 0.0)
        {
            orientationDegrees += HalfCircleDegrees;
        }

        float normalizedGradient = Math.Clamp(rmsGradient / spec.TargetRmsGradient, 0.0f, 1.0f);
        float normalizedContrast = Math.Clamp(contrastSpan / spec.TargetContrastSpan, 0.0f, 1.0f);
        float normalizedIsotropy = Math.Clamp(isotropy / spec.TargetIsotropy, 0.0f, 1.0f);
        float compositeScore = (GradientWeight * normalizedGradient)
            + (ContrastWeight * normalizedContrast)
            + (IsotropyWeight * normalizedIsotropy);

        TeachingReadinessGrade grade = ClassifyGrade(spec, rmsGradient, contrastSpan, isotropy, compositeScore);
        string guidance = BuildGuidance(grade, rmsGradient, contrastSpan, isotropy);

        TeachingReadinessReport report = new(
            grade,
            compositeScore,
            rmsGradient,
            contrastSpan,
            isotropy,
            (float)orientationDegrees,
            evaluatedPixels,
            guidance);

        return TeachingResult<TeachingReadinessReport>.Success(report);
    }

    private static TeachingReadinessGrade ClassifyGrade(
        in TeachingReadinessSpec spec,
        float rmsGradient,
        float contrastSpan,
        float isotropy,
        float compositeScore)
    {
        if ((contrastSpan < spec.MinimumContrastSpan) || (rmsGradient < spec.MinimumRmsGradient))
        {
            return TeachingReadinessGrade.InsufficientContrast;
        }

        if (isotropy < spec.MinimumIsotropy)
        {
            return TeachingReadinessGrade.ApertureAmbiguous;
        }

        if (compositeScore >= spec.OptimalScoreThreshold)
        {
            return TeachingReadinessGrade.OptimalSubPixel;
        }

        return TeachingReadinessGrade.Teachable;
    }

    private static string BuildGuidance(
        TeachingReadinessGrade grade,
        float rmsGradient,
        float contrastSpan,
        float isotropy)
    {
        return grade switch
        {
            TeachingReadinessGrade.OptimalSubPixel => $"최적 티칭 영역 · 에지 에너지({rmsGradient:F1})·명암비({contrastSpan:F0})·2D 등방성({isotropy:F2}) 우수",
            TeachingReadinessGrade.Teachable => $"티칭 가능 · 에지 에너지({rmsGradient:F1}) 양호 · 조명/대비({contrastSpan:F0}) 유지 권장",
            TeachingReadinessGrade.ApertureAmbiguous => $"1D 개구 모호성 주의(등방성 {isotropy:F2}) · 모서리나 비대칭 윤곽을 ROI에 포함하세요",
            _ => $"명암비({contrastSpan:F0})/에지({rmsGradient:F1}) 부족 · 특징이 뚜렷한 윤곽 영역으로 ROI를 이동하세요"
        };
    }

    private static void AccumulateStructureTensor(
        GrayImageView2D image,
        RoiPixelBounds roi,
        out long totalGxSquared,
        out long totalGySquared,
        out long totalGxGy,
        out byte minIntensity,
        out byte maxIntensity)
    {
        totalGxSquared = 0;
        totalGySquared = 0;
        totalGxGy = 0;
        minIntensity = byte.MaxValue;
        maxIntensity = byte.MinValue;

        ReadOnlySpan<byte> buffer = image.Buffer;
        int stride = image.RowStride;
        int startX = roi.X + 1;
        int interiorWidth = roi.InteriorWidth;
        int startY = roi.Y + 1;
        int endY = roi.Bottom - 1;

        for (int y = roi.Y; y < roi.Bottom; y++)
        {
            ReadOnlySpan<byte> fullRow = buffer.Slice((y * stride) + roi.X, roi.Width);
            UpdateMinMaxRow(fullRow, ref minIntensity, ref maxIntensity);
        }

        for (int y = startY; y < endY; y++)
        {
            ReadOnlySpan<byte> prevRow = buffer.Slice((y - 1) * stride, image.Width);
            ReadOnlySpan<byte> currRow = buffer.Slice(y * stride, image.Width);
            ReadOnlySpan<byte> nextRow = buffer.Slice((y + 1) * stride, image.Width);

            if (Vector512.IsHardwareAccelerated && (interiorWidth >= Vector512<short>.Count))
            {
                AccumulateRowKernelAvx512(
                    prevRow,
                    currRow,
                    nextRow,
                    startX,
                    interiorWidth,
                    ref totalGxSquared,
                    ref totalGySquared,
                    ref totalGxGy);
            }
            else if (Vector256.IsHardwareAccelerated && (interiorWidth >= Vector256<short>.Count))
            {
                AccumulateRowKernelAvx2(
                    prevRow,
                    currRow,
                    nextRow,
                    startX,
                    interiorWidth,
                    ref totalGxSquared,
                    ref totalGySquared,
                    ref totalGxGy);
            }
            else
            {
                AccumulateRowKernelScalar(
                    prevRow,
                    currRow,
                    nextRow,
                    startX,
                    interiorWidth,
                    ref totalGxSquared,
                    ref totalGySquared,
                    ref totalGxGy);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void UpdateMinMaxRow(ReadOnlySpan<byte> row, ref byte minVal, ref byte maxVal)
    {
        ref byte baseRef = ref MemoryMarshal.GetReference(row);
        int length = row.Length;
        int index = 0;

        if (Vector256.IsHardwareAccelerated && (length >= Vector256<byte>.Count))
        {
            // [Perf Why]: Vector256.Min/Max scans 32 pixels per instruction before reducing once per row.
            Vector256<byte> vMin = Vector256.Create(minVal);
            Vector256<byte> vMax = Vector256.Create(maxVal);
            int simdLimit = length & ~(Vector256<byte>.Count - 1);
            for (; index < simdLimit; index += Vector256<byte>.Count)
            {
                Vector256<byte> sample = Vector256.LoadUnsafe(ref Unsafe.Add(ref baseRef, index));
                vMin = Vector256.Min(vMin, sample);
                vMax = Vector256.Max(vMax, sample);
            }

            for (int lane = 0; lane < Vector256<byte>.Count; lane++)
            {
                byte laneMin = vMin.GetElement(lane);
                byte laneMax = vMax.GetElement(lane);
                if (laneMin < minVal)
                {
                    minVal = laneMin;
                }

                if (laneMax > maxVal)
                {
                    maxVal = laneMax;
                }
            }
        }

        for (; index < length; index++)
        {
            byte pixel = Unsafe.Add(ref baseRef, index);
            if (pixel < minVal)
            {
                minVal = pixel;
            }

            if (pixel > maxVal)
            {
                maxVal = pixel;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AccumulateRowKernelAvx512(
        ReadOnlySpan<byte> prevRow,
        ReadOnlySpan<byte> currRow,
        ReadOnlySpan<byte> nextRow,
        int startX,
        int interiorWidth,
        ref long totalGxSquared,
        ref long totalGySquared,
        ref long totalGxGy)
    {
        // [Perf Why]: Widens 32 uint8 pixels into Vector512<short> to evaluate 32 3x3 Sobel gradients per instruction without overflow.
        ref byte prevBase = ref MemoryMarshal.GetReference(prevRow);
        ref byte currBase = ref MemoryMarshal.GetReference(currRow);
        ref byte nextBase = ref MemoryMarshal.GetReference(nextRow);

        int vectorStep = Vector512<short>.Count;
        int simdWidth = interiorWidth & ~(vectorStep - 1);
        int offset = 0;

        Vector512<long> accGx2 = Vector512<long>.Zero;
        Vector512<long> accGy2 = Vector512<long>.Zero;
        Vector512<long> accGxy = Vector512<long>.Zero;

        for (; offset < simdWidth; offset += vectorStep)
        {
            int x = startX + offset;
            Vector512<short> tl = Vector512.WidenLower(Vector256.LoadUnsafe(ref Unsafe.Add(ref prevBase, x - 1)).ToVector512Unsafe()).AsInt16();
            Vector512<short> tc = Vector512.WidenLower(Vector256.LoadUnsafe(ref Unsafe.Add(ref prevBase, x)).ToVector512Unsafe()).AsInt16();
            Vector512<short> tr = Vector512.WidenLower(Vector256.LoadUnsafe(ref Unsafe.Add(ref prevBase, x + 1)).ToVector512Unsafe()).AsInt16();

            Vector512<short> ml = Vector512.WidenLower(Vector256.LoadUnsafe(ref Unsafe.Add(ref currBase, x - 1)).ToVector512Unsafe()).AsInt16();
            Vector512<short> mr = Vector512.WidenLower(Vector256.LoadUnsafe(ref Unsafe.Add(ref currBase, x + 1)).ToVector512Unsafe()).AsInt16();

            Vector512<short> bl = Vector512.WidenLower(Vector256.LoadUnsafe(ref Unsafe.Add(ref nextBase, x - 1)).ToVector512Unsafe()).AsInt16();
            Vector512<short> bc = Vector512.WidenLower(Vector256.LoadUnsafe(ref Unsafe.Add(ref nextBase, x)).ToVector512Unsafe()).AsInt16();
            Vector512<short> br = Vector512.WidenLower(Vector256.LoadUnsafe(ref Unsafe.Add(ref nextBase, x + 1)).ToVector512Unsafe()).AsInt16();

            Vector512<short> midDiffX = mr - ml;
            Vector512<short> gx = (tr - tl) + (midDiffX + midDiffX) + (br - bl);

            Vector512<short> midDiffY = bc - tc;
            Vector512<short> gy = (bl - tl) + (midDiffY + midDiffY) + (br - tr);

            (Vector512<int> gxLow, Vector512<int> gxHigh) = Vector512.Widen(gx);
            (Vector512<int> gyLow, Vector512<int> gyHigh) = Vector512.Widen(gy);

            Vector512<int> gx2Low = gxLow * gxLow;
            Vector512<int> gx2High = gxHigh * gxHigh;
            Vector512<int> gy2Low = gyLow * gyLow;
            Vector512<int> gy2High = gyHigh * gyHigh;
            Vector512<int> gxyLow = gxLow * gyLow;
            Vector512<int> gxyHigh = gxHigh * gyHigh;

            (Vector512<long> gx2LL, Vector512<long> gx2LH) = Vector512.Widen(gx2Low);
            (Vector512<long> gx2HL, Vector512<long> gx2HH) = Vector512.Widen(gx2High);
            accGx2 += gx2LL + gx2LH + gx2HL + gx2HH;

            (Vector512<long> gy2LL, Vector512<long> gy2LH) = Vector512.Widen(gy2Low);
            (Vector512<long> gy2HL, Vector512<long> gy2HH) = Vector512.Widen(gy2High);
            accGy2 += gy2LL + gy2LH + gy2HL + gy2HH;

            (Vector512<long> gxyLL, Vector512<long> gxyLH) = Vector512.Widen(gxyLow);
            (Vector512<long> gxyHL, Vector512<long> gxyHH) = Vector512.Widen(gxyHigh);
            accGxy += gxyLL + gxyLH + gxyHL + gxyHH;
        }

        totalGxSquared += Vector512.Sum(accGx2);
        totalGySquared += Vector512.Sum(accGy2);
        totalGxGy += Vector512.Sum(accGxy);

        if (offset < interiorWidth)
        {
            AccumulateRowKernelScalar(
                prevRow,
                currRow,
                nextRow,
                startX + offset,
                interiorWidth - offset,
                ref totalGxSquared,
                ref totalGySquared,
                ref totalGxGy);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AccumulateRowKernelAvx2(
        ReadOnlySpan<byte> prevRow,
        ReadOnlySpan<byte> currRow,
        ReadOnlySpan<byte> nextRow,
        int startX,
        int interiorWidth,
        ref long totalGxSquared,
        ref long totalGySquared,
        ref long totalGxGy)
    {
        // [Perf Why]: Widens 16 uint8 pixels into Vector256<short> to compute 16 3x3 Sobel structure tensor products in parallel.
        ref byte prevBase = ref MemoryMarshal.GetReference(prevRow);
        ref byte currBase = ref MemoryMarshal.GetReference(currRow);
        ref byte nextBase = ref MemoryMarshal.GetReference(nextRow);

        int vectorStep = Vector256<short>.Count;
        int simdWidth = interiorWidth & ~(vectorStep - 1);
        int offset = 0;

        Vector256<long> accGx2 = Vector256<long>.Zero;
        Vector256<long> accGy2 = Vector256<long>.Zero;
        Vector256<long> accGxy = Vector256<long>.Zero;

        for (; offset < simdWidth; offset += vectorStep)
        {
            int x = startX + offset;
            Vector256<short> tl = Vector256.WidenLower(Vector128.LoadUnsafe(ref Unsafe.Add(ref prevBase, x - 1)).ToVector256Unsafe()).AsInt16();
            Vector256<short> tc = Vector256.WidenLower(Vector128.LoadUnsafe(ref Unsafe.Add(ref prevBase, x)).ToVector256Unsafe()).AsInt16();
            Vector256<short> tr = Vector256.WidenLower(Vector128.LoadUnsafe(ref Unsafe.Add(ref prevBase, x + 1)).ToVector256Unsafe()).AsInt16();

            Vector256<short> ml = Vector256.WidenLower(Vector128.LoadUnsafe(ref Unsafe.Add(ref currBase, x - 1)).ToVector256Unsafe()).AsInt16();
            Vector256<short> mr = Vector256.WidenLower(Vector128.LoadUnsafe(ref Unsafe.Add(ref currBase, x + 1)).ToVector256Unsafe()).AsInt16();

            Vector256<short> bl = Vector256.WidenLower(Vector128.LoadUnsafe(ref Unsafe.Add(ref nextBase, x - 1)).ToVector256Unsafe()).AsInt16();
            Vector256<short> bc = Vector256.WidenLower(Vector128.LoadUnsafe(ref Unsafe.Add(ref nextBase, x)).ToVector256Unsafe()).AsInt16();
            Vector256<short> br = Vector256.WidenLower(Vector128.LoadUnsafe(ref Unsafe.Add(ref nextBase, x + 1)).ToVector256Unsafe()).AsInt16();

            Vector256<short> midDiffX = mr - ml;
            Vector256<short> gx = (tr - tl) + (midDiffX + midDiffX) + (br - bl);

            Vector256<short> midDiffY = bc - tc;
            Vector256<short> gy = (bl - tl) + (midDiffY + midDiffY) + (br - tr);

            (Vector256<int> gxLow, Vector256<int> gxHigh) = Vector256.Widen(gx);
            (Vector256<int> gyLow, Vector256<int> gyHigh) = Vector256.Widen(gy);

            Vector256<int> gx2Low = gxLow * gxLow;
            Vector256<int> gx2High = gxHigh * gxHigh;
            Vector256<int> gy2Low = gyLow * gyLow;
            Vector256<int> gy2High = gyHigh * gyHigh;
            Vector256<int> gxyLow = gxLow * gyLow;
            Vector256<int> gxyHigh = gxHigh * gyHigh;

            (Vector256<long> gx2LL, Vector256<long> gx2LH) = Vector256.Widen(gx2Low);
            (Vector256<long> gx2HL, Vector256<long> gx2HH) = Vector256.Widen(gx2High);
            accGx2 += gx2LL + gx2LH + gx2HL + gx2HH;

            (Vector256<long> gy2LL, Vector256<long> gy2LH) = Vector256.Widen(gy2Low);
            (Vector256<long> gy2HL, Vector256<long> gy2HH) = Vector256.Widen(gy2High);
            accGy2 += gy2LL + gy2LH + gy2HL + gy2HH;

            (Vector256<long> gxyLL, Vector256<long> gxyLH) = Vector256.Widen(gxyLow);
            (Vector256<long> gxyHL, Vector256<long> gxyHH) = Vector256.Widen(gxyHigh);
            accGxy += gxyLL + gxyLH + gxyHL + gxyHH;
        }

        totalGxSquared += Vector256.Sum(accGx2);
        totalGySquared += Vector256.Sum(accGy2);
        totalGxGy += Vector256.Sum(accGxy);

        if (offset < interiorWidth)
        {
            AccumulateRowKernelScalar(
                prevRow,
                currRow,
                nextRow,
                startX + offset,
                interiorWidth - offset,
                ref totalGxSquared,
                ref totalGySquared,
                ref totalGxGy);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AccumulateRowKernelScalar(
        ReadOnlySpan<byte> prevRow,
        ReadOnlySpan<byte> currRow,
        ReadOnlySpan<byte> nextRow,
        int startX,
        int count,
        ref long totalGxSquared,
        ref long totalGySquared,
        ref long totalGxGy)
    {
        ref byte prevBase = ref MemoryMarshal.GetReference(prevRow);
        ref byte currBase = ref MemoryMarshal.GetReference(currRow);
        ref byte nextBase = ref MemoryMarshal.GetReference(nextRow);

        int endX = startX + count;
        for (int x = startX; x < endX; x++)
        {
            int tl = Unsafe.Add(ref prevBase, x - 1);
            int tc = Unsafe.Add(ref prevBase, x);
            int tr = Unsafe.Add(ref prevBase, x + 1);

            int ml = Unsafe.Add(ref currBase, x - 1);
            int mr = Unsafe.Add(ref currBase, x + 1);

            int bl = Unsafe.Add(ref nextBase, x - 1);
            int bc = Unsafe.Add(ref nextBase, x);
            int br = Unsafe.Add(ref nextBase, x + 1);

            long gx = (tr - tl) + ((mr - ml) * 2) + (br - bl);
            long gy = (bl - tl) + ((bc - tc) * 2) + (br - tr);

            totalGxSquared += gx * gx;
            totalGySquared += gy * gy;
            totalGxGy += gx * gy;
        }
    }
}
