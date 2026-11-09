using VisionGeometry;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 원본 이미지 크롭 버퍼에서 회전된 ROI 영역을 양선형 보간(Bilinear Interpolation)으로 정규화(Rectified)하여
/// 등록 패턴 썸네일 및 병렬 파일 캐시용 8비트 그레이스케일 패치를 생성한다.
/// </summary>
public static class RoiPreviewPatchExtractor
{
    public const int MaxPreviewDimension = 256;
    private const double PixelCenterOffset = 0.5;
    private const double DegreesToRadians = Math.PI / 180.0;
    private const byte MaskedBackgroundIntensity = 24;

    public static RectifiedPatternPatch ExtractRectifiedPatch(
        ReadOnlySpan<byte> cropPixels,
        int cropWidth,
        int cropHeight,
        int cropStride,
        int cropLeft,
        int cropTop,
        RoiGeometry roi,
        RoiExclusion? exclusion = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cropWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cropHeight);
        ArgumentOutOfRangeException.ThrowIfLessThan(cropStride, cropWidth);
        ArgumentNullException.ThrowIfNull(roi);

        int requiredLength = checked(((cropHeight - 1) * cropStride) + cropWidth);
        if (cropPixels.Length < requiredLength)
        {
            throw new ArgumentException("cropPixels length is smaller than required stride * height.", nameof(cropPixels));
        }

        double maxDim = Math.Max(roi.Width, roi.Height);
        double scale = Math.Min(1.0, MaxPreviewDimension / Math.Max(1.0, maxDim));
        int dstWidth = Math.Clamp((int)Math.Round(roi.Width * scale), 1, MaxPreviewDimension);
        int dstHeight = Math.Clamp((int)Math.Round(roi.Height * scale), 1, MaxPreviewDimension);

        var output = new byte[checked(dstWidth * dstHeight)];
        double localCenterX = roi.Center.X - cropLeft;
        double localCenterY = roi.Center.Y - cropTop;
        double angleRad = roi.Angle * DegreesToRadians;
        var (sin, cos) = Math.SinCos(angleRad);

        double stepX = roi.Width / dstWidth;
        double stepY = roi.Height / dstHeight;
        double startLocalX = (-PixelCenterOffset * roi.Width) + (PixelCenterOffset * stepX);
        double startLocalY = (-PixelCenterOffset * roi.Height) + (PixelCenterOffset * stepY);
        bool isNonRectangular = roi.Shape.Kind != RoiShapeKind.Rectangle;
        bool hasExclusion = exclusion is not null && !exclusion.Regions.IsDefaultOrEmpty;

        for (int y = 0; y < dstHeight; y++)
        {
            double ly = startLocalY + (y * stepY);
            int dstRowOffset = y * dstWidth;

            for (int x = 0; x < dstWidth; x++)
            {
                double lx = startLocalX + (x * stepX);
                if (isNonRectangular && !roi.Shape.ContainsLocal(new PointD(lx, ly), roi.Width, roi.Height, 0))
                {
                    output[dstRowOffset + x] = MaskedBackgroundIntensity;
                    continue;
                }

                double sampleX = localCenterX + (lx * cos) - (ly * sin) - PixelCenterOffset;
                double sampleY = localCenterY + (lx * sin) + (ly * cos) - PixelCenterOffset;

                if (hasExclusion)
                {
                    double worldX = cropLeft + sampleX + PixelCenterOffset;
                    double worldY = cropTop + sampleY + PixelCenterOffset;
                    if (exclusion!.Contains(new PointD(worldX, worldY)))
                    {
                        output[dstRowOffset + x] = MaskedBackgroundIntensity;
                        continue;
                    }
                }

                int ix0 = Math.Clamp((int)Math.Floor(sampleX), 0, cropWidth - 1);
                int iy0 = Math.Clamp((int)Math.Floor(sampleY), 0, cropHeight - 1);
                int ix1 = Math.Min(cropWidth - 1, ix0 + 1);
                int iy1 = Math.Min(cropHeight - 1, iy0 + 1);
                double fx = Math.Clamp(sampleX - ix0, 0.0, 1.0);
                double fy = Math.Clamp(sampleY - iy0, 0.0, 1.0);

                // [Perf Why]: Index rows strictly via cropStride to prevent Stride != Width corruption.
                int row0 = iy0 * cropStride;
                int row1 = iy1 * cropStride;
                byte p00 = cropPixels[row0 + ix0];
                byte p10 = cropPixels[row0 + ix1];
                byte p01 = cropPixels[row1 + ix0];
                byte p11 = cropPixels[row1 + ix1];

                double topVal = p00 + ((p10 - p00) * fx);
                double bottomVal = p01 + ((p11 - p01) * fx);
                int interpolated = (int)Math.Round(topVal + ((bottomVal - topVal) * fy));
                output[dstRowOffset + x] = (byte)Math.Clamp(interpolated, byte.MinValue, byte.MaxValue);
            }
        }

        return RectifiedPatternPatch.Create(output, dstWidth, dstHeight);
    }
}
