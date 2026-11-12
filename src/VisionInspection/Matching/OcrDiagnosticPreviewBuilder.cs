using System.Buffers;
using System.Collections.Immutable;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.IndustrialOcrOcv;
using VisionOcr.PpOcr;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// OCR 파라미터(초해상도 배율, 언샤프닝 이득, 적응 대비 오프셋, 도트 브리징 반경, 예상 피치) 조절 시
/// 전처리 강화 영상과 적응형 이진화+문자 분할 오버레이 영상을 실시간으로 합성하는 빌더.
/// </summary>
public static class OcrDiagnosticPreviewBuilder
{
    private const int MinPreviewWidth = 64;
    private const int MaxPreviewWidth = 360;
    private const int MinPreviewHeight = 28;
    private const int MaxPreviewHeight = 140;
    private const int LocalHalfWindowX = 14;
    private const int LocalHalfWindowY = 8;
    private const int ChannelsBgra = 4;
    private const byte MaxByteIntensity = 255;
    private const float BilinearHalfOffset = 0.5f;
    private const float QuarterNeighborWeight = 0.25f;

    // BGRA32 palette constants for binarized & character segmentation overlay
    private const byte BgBlue = 28;
    private const byte BgGreen = 18;
    private const byte BgRed = 14;

    private const byte FgPassBlue = 248;
    private const byte FgPassGreen = 236;
    private const byte FgPassRed = 190;

    private const byte BoxPassBlue = 153;
    private const byte BoxPassGreen = 211;
    private const byte BoxPassRed = 52;

    private const byte BoxWarnBlue = 36;
    private const byte BoxWarnGreen = 191;
    private const byte BoxWarnRed = 251;

    private const byte PitchGridBlue = 145;
    private const byte PitchGridGreen = 90;
    private const byte PitchGridRed = 38;

    /// <summary>
    /// 정류된 ROI 그레이스케일 뷰와 OCR 판독 결과로부터 실시간 전처리·이진화 진단 스냅샷을 생성한다.
    /// </summary>
    public static OcrDiagnosticSnapshot Build(
        ReadOnlyImageView2D<byte> roiView,
        PpOcrRegionResult ocrResult,
        InspectionSuiteAlgorithmKind algorithm,
        InspectionSuiteParameters parameters)
    {
        roiView.EnsureValid();

        int srcW = roiView.Width;
        int srcH = roiView.Height;

        float aspect = (float)srcW / Math.Max(1, srcH);
        int dstW = Math.Clamp(srcW, MinPreviewWidth, MaxPreviewWidth);
        int dstH = Math.Clamp((int)MathF.Round(dstW / Math.Max(0.25f, aspect)), MinPreviewHeight, MaxPreviewHeight);

        int grayCount = checked(dstW * dstH);
        int bgraCount = checked(grayCount * ChannelsBgra);

        float[]? rentedBilinear = null;
        byte[]? rentedEnhanced = null;
        int[]? rentedIntegral = null;

        try
        {
            rentedBilinear = ArrayPool<float>.Shared.Rent(grayCount);
            rentedEnhanced = ArrayPool<byte>.Shared.Rent(grayCount);
            int integralStride = dstW + 1;
            int integralCount = checked((dstH + 1) * integralStride);
            rentedIntegral = ArrayPool<int>.Shared.Rent(integralCount);

            Span<float> bilinear = rentedBilinear.AsSpan(0, grayCount);
            Span<byte> enhanced = rentedEnhanced.AsSpan(0, grayCount);
            Span<int> integral = rentedIntegral.AsSpan(0, integralCount);

            const float DefaultPreviewUnsharpGain = 0.20f;
            const int DefaultPreviewDotRadius = 0;
            const float DefaultPreviewContrastOffset = 14.0f;
            const float MinAcceptClamp = 0.05f;
            const float MaxAcceptClamp = 1.0f;

            _ = algorithm;
            float unsharpGain = DefaultPreviewUnsharpGain;
            int dotRadius = DefaultPreviewDotRadius;
            float contrastOffset = DefaultPreviewContrastOffset;
            float acceptThreshold = Math.Clamp(parameters.PpOcrMinConfidence, MinAcceptClamp, MaxAcceptClamp);

            SampleBilinear(roiView, dstW, dstH, bilinear);
            ApplyUnsharpAndDotClosing(bilinear, dstW, dstH, unsharpGain, dotRadius, ocrResult.DarkOnLight, enhanced);
            BuildIntegralImage(enhanced, dstW, dstH, integralStride, integral);

            byte[] outGray = enhanced.ToArray();
            byte[] outBgra = new byte[bgraCount];

            RenderBinarizedAndCharacterBoxes(
                enhanced,
                integral,
                integralStride,
                dstW,
                dstH,
                srcW,
                srcH,
                contrastOffset,
                acceptThreshold,
                ocrResult,
                algorithm,
                parameters,
                outBgra);

            string charSetTag = parameters.OcrAllowedCharacters == OcrAllowedCharacterSet.All
                ? string.Empty
                : $" · 문자집합={OcrCharacterSetFilter.GetDisplayName(parameters.OcrAllowedCharacters)}";
            string presetTag = parameters.PpOcrPreset == VisionOcr.PpOcr.PpOcrSpeedPreset.HighSpeedProduction
                ? "고속양산(배치)"
                : "범용자동";
            string preprocBadge =
                $"PP-OCRv4 ({presetTag}) · H=48 정규화 · 수락확률 ≥ {acceptThreshold:F2} · 극성={parameters.PpOcrPolarity}" + charSetTag;

            return new OcrDiagnosticSnapshot(
                PreviewWidth: dstW,
                PreviewHeight: dstH,
                EnhancedGrayPixels: ImmutableCollectionsMarshalSnapshot(outGray),
                BinarizedRgbaPixels: ImmutableCollectionsMarshalSnapshot(outBgra),
                RecognizedText: ocrResult.FullText,
                Lines: ocrResult.Lines.ToImmutableArray(),
                Characters: ocrResult.Characters.ToImmutableArray(),
                MeanConfidence: ocrResult.MeanConfidence,
                MinConfidence: ocrResult.MinConfidence,
                AcceptThreshold: acceptThreshold,
                ForegroundContrastOffset: contrastOffset,
                DarkOnLight: ocrResult.DarkOnLight,
                PreprocessingBadge: preprocBadge);
        }
        finally
        {
            if (rentedBilinear is not null)
            {
                ArrayPool<float>.Shared.Return(rentedBilinear);
            }
            if (rentedEnhanced is not null)
            {
                ArrayPool<byte>.Shared.Return(rentedEnhanced);
            }
            if (rentedIntegral is not null)
            {
                ArrayPool<int>.Shared.Return(rentedIntegral);
            }
        }
    }

    private static ImmutableArray<byte> ImmutableCollectionsMarshalSnapshot(byte[] array)
        => System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(array);

    private static void SampleBilinear(
        ReadOnlyImageView2D<byte> src,
        int dstW,
        int dstH,
        Span<float> dst)
    {
        int sw = src.Width;
        int sh = src.Height;
        float scaleX = (float)sw / dstW;
        float scaleY = (float)sh / dstH;

        for (int dy = 0; dy < dstH; dy++)
        {
            float sy = ((dy + BilinearHalfOffset) * scaleY) - BilinearHalfOffset;
            int y0 = Math.Clamp((int)MathF.Floor(sy), 0, sh - 1);
            int y1 = Math.Clamp(y0 + 1, 0, sh - 1);
            float fy = Math.Clamp(sy - y0, 0f, 1f);

            ReadOnlySpan<byte> row0 = src.RowSpan(y0);
            ReadOnlySpan<byte> row1 = src.RowSpan(y1);
            int dstRowOffset = dy * dstW;

            for (int dx = 0; dx < dstW; dx++)
            {
                float sx = ((dx + BilinearHalfOffset) * scaleX) - BilinearHalfOffset;
                int x0 = Math.Clamp((int)MathF.Floor(sx), 0, sw - 1);
                int x1 = Math.Clamp(x0 + 1, 0, sw - 1);
                float fx = Math.Clamp(sx - x0, 0f, 1f);

                float top = row0[x0] + ((row0[x1] - row0[x0]) * fx);
                float bot = row1[x0] + ((row1[x1] - row1[x0]) * fx);
                dst[dstRowOffset + dx] = top + ((bot - top) * fy);
            }
        }
    }

    private static void ApplyUnsharpAndDotClosing(
        ReadOnlySpan<float> bilinear,
        int width,
        int height,
        float unsharpGain,
        int dotClosingRadius,
        bool darkOnLight,
        Span<byte> dstEnhanced)
    {
        for (int y = 0; y < height; y++)
        {
            int ym1 = Math.Max(0, y - 1) * width;
            int y0 = y * width;
            int yp1 = Math.Min(height - 1, y + 1) * width;

            for (int x = 0; x < width; x++)
            {
                int xm1 = Math.Max(0, x - 1);
                int xp1 = Math.Min(width - 1, x + 1);

                float center = bilinear[y0 + x];
                float neighborMean = (bilinear[ym1 + x] + bilinear[yp1 + x] + bilinear[y0 + xm1] + bilinear[y0 + xp1]) * QuarterNeighborWeight;
                float sharpened = center + ((center - neighborMean) * unsharpGain);
                dstEnhanced[y0 + x] = (byte)Math.Clamp((int)MathF.Round(sharpened), 0, MaxByteIntensity);
            }
        }

        if (dotClosingRadius <= 0)
        {
            return;
        }

        int pixelCount = width * height;
        byte[]? rentedMorph = ArrayPool<byte>.Shared.Rent(pixelCount);
        try
        {
            Span<byte> morphTmp = rentedMorph.AsSpan(0, pixelCount);
            int r = Math.Min(dotClosingRadius, 3);

            // Morphological closing on foreground strokes (min-filter then max-filter for DarkOnLight, or vice versa)
            for (int y = 0; y < height; y++)
            {
                int yMin = Math.Max(0, y - r);
                int yMax = Math.Min(height - 1, y + r);
                int rowOff = y * width;
                for (int x = 0; x < width; x++)
                {
                    int xMin = Math.Max(0, x - r);
                    int xMax = Math.Min(width - 1, x + r);
                    byte extreme = dstEnhanced[rowOff + x];
                    for (int ny = yMin; ny <= yMax; ny++)
                    {
                        int nRow = ny * width;
                        for (int nx = xMin; nx <= xMax; nx++)
                        {
                            byte v = dstEnhanced[nRow + nx];
                            extreme = darkOnLight ? Math.Min(extreme, v) : Math.Max(extreme, v);
                        }
                    }
                    morphTmp[rowOff + x] = extreme;
                }
            }

            morphTmp.CopyTo(dstEnhanced);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedMorph);
        }
    }

    private static void BuildIntegralImage(
        ReadOnlySpan<byte> gray,
        int width,
        int height,
        int integralStride,
        Span<int> integral)
    {
        integral.Slice(0, integralStride).Clear();
        for (int y = 0; y < height; y++)
        {
            int srcOff = y * width;
            int prevIntOff = y * integralStride;
            int curIntOff = (y + 1) * integralStride;
            integral[curIntOff] = 0;
            int rowSum = 0;
            for (int x = 0; x < width; x++)
            {
                rowSum += gray[srcOff + x];
                integral[curIntOff + x + 1] = integral[prevIntOff + x + 1] + rowSum;
            }
        }
    }

    private static void RenderBinarizedAndCharacterBoxes(
        ReadOnlySpan<byte> enhanced,
        ReadOnlySpan<int> integral,
        int integralStride,
        int dstW,
        int dstH,
        int srcW,
        int srcH,
        float contrastOffset,
        float acceptThreshold,
        PpOcrRegionResult ocrResult,
        InspectionSuiteAlgorithmKind algorithm,
        InspectionSuiteParameters parameters,
        Span<byte> outBgra)
    {
        bool darkOnLight = ocrResult.DarkOnLight;
        int winX = Math.Clamp(dstW / 12, 6, LocalHalfWindowX);
        int winY = Math.Clamp(dstH / 4, 4, LocalHalfWindowY);

        for (int y = 0; y < dstH; y++)
        {
            int y0 = Math.Max(0, y - winY);
            int y1 = Math.Min(dstH - 1, y + winY);
            int intY0 = y0 * integralStride;
            int intY1 = (y1 + 1) * integralStride;
            int rowOff = y * dstW;

            for (int x = 0; x < dstW; x++)
            {
                int x0 = Math.Max(0, x - winX);
                int x1 = Math.Min(dstW - 1, x + winX);
                int area = (y1 - y0 + 1) * (x1 - x0 + 1);
                int sum = integral[intY1 + x1 + 1] - integral[intY0 + x1 + 1] - integral[intY1 + x0] + integral[intY0 + x0];
                float localMean = (float)sum / Math.Max(1, area);

                byte g = enhanced[rowOff + x];
                bool isForeground = darkOnLight
                    ? (localMean - g) >= contrastOffset * 0.65f
                    : (g - localMean) >= contrastOffset * 0.65f;

                int idx = (rowOff + x) * ChannelsBgra;
                if (isForeground)
                {
                    outBgra[idx] = FgPassBlue;
                    outBgra[idx + 1] = FgPassGreen;
                    outBgra[idx + 2] = FgPassRed;
                    outBgra[idx + 3] = MaxByteIntensity;
                }
                else
                {
                    byte dim = (byte)(g / 7);
                    outBgra[idx] = (byte)Math.Min(MaxByteIntensity, BgBlue + dim);
                    outBgra[idx + 1] = (byte)Math.Min(MaxByteIntensity, BgGreen + dim);
                    outBgra[idx + 2] = (byte)Math.Min(MaxByteIntensity, BgRed + dim);
                    outBgra[idx + 3] = MaxByteIntensity;
                }
            }
        }

        // Draw subtle reference ticks along top & bottom edges
        const int DefaultReferenceTickStepSrcPx = 24;
        const float MinReferenceTickStepDstPx = 6.0f;
        _ = algorithm;
        _ = parameters;
        float scaleX = (float)dstW / Math.Max(1, srcW);
        float scaleY = (float)dstH / Math.Max(1, srcH);
        float stepDstPx = Math.Max(MinReferenceTickStepDstPx, DefaultReferenceTickStepSrcPx * scaleX);

        for (float gx = stepDstPx; gx < dstW - 2; gx += stepDstPx)
        {
            int ix = (int)MathF.Round(gx);
            for (int tickY = 0; tickY < Math.Min(4, dstH); tickY++)
            {
                BlendPixel(outBgra, dstW, dstH, ix, tickY, PitchGridBlue, PitchGridGreen, PitchGridRed);
                BlendPixel(outBgra, dstW, dstH, ix, (dstH - 1) - tickY, PitchGridBlue, PitchGridGreen, PitchGridRed);
            }
        }

        // Draw recognized character bounding boxes (emerald for pass confidence, amber for low confidence)
        for (int i = 0; i < ocrResult.Characters.Count; i++)
        {
            PpOcrCharacterBox ch = ocrResult.Characters[i];
            int x0 = Math.Clamp((int)MathF.Floor(ch.StartCol * scaleX), 0, dstW - 1);
            int x1 = Math.Clamp((int)MathF.Ceiling(ch.EndCol * scaleX), x0, dstW - 1);
            int y0 = Math.Clamp((int)MathF.Floor(ch.TopRow * scaleY), 0, dstH - 1);
            int y1 = Math.Clamp((int)MathF.Ceiling(ch.BottomRow * scaleY), y0, dstH - 1);

            bool passChar = ch.Confidence >= acceptThreshold;
            byte b = passChar ? BoxPassBlue : BoxWarnBlue;
            byte g = passChar ? BoxPassGreen : BoxWarnGreen;
            byte r = passChar ? BoxPassRed : BoxWarnRed;

            for (int x = x0; x <= x1; x++)
            {
                SetPixel(outBgra, dstW, dstH, x, y0, b, g, r);
                SetPixel(outBgra, dstW, dstH, x, y1, b, g, r);
            }
            for (int y = y0; y <= y1; y++)
            {
                SetPixel(outBgra, dstW, dstH, x0, y, b, g, r);
                SetPixel(outBgra, dstW, dstH, x1, y, b, g, r);
            }
        }
    }

    private static void SetPixel(Span<byte> bgra, int width, int height, int x, int y, byte b, byte g, byte r)
    {
        if ((uint)x >= (uint)width || (uint)y >= (uint)height)
        {
            return;
        }
        int idx = ((y * width) + x) * ChannelsBgra;
        bgra[idx] = b;
        bgra[idx + 1] = g;
        bgra[idx + 2] = r;
        bgra[idx + 3] = MaxByteIntensity;
    }

    private static void BlendPixel(Span<byte> bgra, int width, int height, int x, int y, byte b, byte g, byte r)
    {
        if ((uint)x >= (uint)width || (uint)y >= (uint)height)
        {
            return;
        }
        int idx = ((y * width) + x) * ChannelsBgra;
        bgra[idx] = (byte)((bgra[idx] + b) >> 1);
        bgra[idx + 1] = (byte)((bgra[idx + 1] + g) >> 1);
        bgra[idx + 2] = (byte)((bgra[idx + 2] + r) >> 1);
        bgra[idx + 3] = MaxByteIntensity;
    }
}
