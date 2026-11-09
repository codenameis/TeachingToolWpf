using System.Collections.Immutable;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Models;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionGeometry;

namespace TeachingToolWpf.Services;

/// <summary>
/// 등록된 패턴의 8비트 그레이스케일 패치(또는 특징점 기하 정보)로부터
/// 백그라운드 스레드에서도 안전하게 바인딩 가능한 Frozen <see cref="BitmapSource"/> 썸네일을 생성한다.
/// </summary>
public static class PatternThumbnailRenderer
{
    public const int DefaultSyntheticSize = 96;
    public const int MinSyntheticDimension = 32;
    public const int MaxSyntheticDimension = 192;
    private const double StandardDpi = 96.0;
    private const double HalfScale = 0.5;
    private const byte BackgroundDarkGray = 28;
    private const byte GridLineGray = 44;
    private const byte OutlineGray = 132;
    private const byte FeatureHighlightGray = 240;
    private const byte CenterCrossGray = 180;
    private const int GridSpacingPixels = 16;
    private const int CrosshairArmRadius = 4;
    private const int BorderMarginDivisor = 12;
    private const int InnerBoxInsetPixels = 3;
    private const int GradientBaseIntensity = 70;
    private const int GradientRangeIntensity = 90;

    /// <summary>
    /// 서비스가 반환한 실제 ROI 그레이스케일 크롭이 있으면 그대로 사용하고,
    /// 단위 테스트 목(Mock) 서비스처럼 원본 픽셀이 없는 경우 학습된 모델의 윤곽 특징점으로부터
    /// 시각적 패턴 프리뷰 패치를 합성하여 항상 유효한 패턴 이미지를 보장한다.
    /// </summary>
    public static RectifiedPatternPatch ResolveOrSynthesizePatch(
        byte[]? rawGray8,
        int rawWidth,
        int rawHeight,
        RoiGeometry teachingRegion,
        ShapeModel? shapeModel = null,
        PatMaxTrainedTemplate? patMaxTemplate = null,
        CpuTemplate? cpuTemplate = null,
        CorrelationTemplate? templateModel = null)
    {
        ArgumentNullException.ThrowIfNull(teachingRegion);

        if (rawGray8 is not null && rawWidth > 0 && rawHeight > 0 && rawGray8.Length >= checked(rawWidth * rawHeight))
        {
            return RectifiedPatternPatch.Create(rawGray8, rawWidth, rawHeight);
        }

        if (patMaxTemplate?.RawPreviewGray8 is { } pmRaw &&
            patMaxTemplate.RawPreviewWidth > 0 &&
            patMaxTemplate.RawPreviewHeight > 0 &&
            pmRaw.Length >= checked(patMaxTemplate.RawPreviewWidth * patMaxTemplate.RawPreviewHeight))
        {
            return RectifiedPatternPatch.Create(pmRaw, patMaxTemplate.RawPreviewWidth, patMaxTemplate.RawPreviewHeight);
        }

        if (patMaxTemplate?.FilteredPreviewGray8 is { } pmGray &&
            patMaxTemplate.PreviewWidth > 0 &&
            patMaxTemplate.PreviewHeight > 0 &&
            pmGray.Length >= checked(patMaxTemplate.PreviewWidth * patMaxTemplate.PreviewHeight))
        {
            return RectifiedPatternPatch.Create(pmGray, patMaxTemplate.PreviewWidth, patMaxTemplate.PreviewHeight);
        }

        if (cpuTemplate?.PreviewGray8 is { } cpuGray &&
            cpuTemplate.PreviewWidth > 0 &&
            cpuTemplate.PreviewHeight > 0 &&
            cpuGray.Length >= checked(cpuTemplate.PreviewWidth * cpuTemplate.PreviewHeight))
        {
            return RectifiedPatternPatch.Create(cpuGray, cpuTemplate.PreviewWidth, cpuTemplate.PreviewHeight);
        }

        return SynthesizeFeaturePatch(teachingRegion, shapeModel, patMaxTemplate, cpuTemplate, templateModel);
    }

    /// <summary>
    /// 8비트 그레이스케일 패치로부터 Frozen <see cref="BitmapSource"/>를 생성한다.
    /// <see cref="Freezable.Freeze()"/>가 호출되므로 병렬 백그라운드 워커 스레드에서 생성 후 UI에 즉시 바인딩할 수 있다.
    /// </summary>
    public static BitmapSource CreateFrozenThumbnail(RectifiedPatternPatch patch)
    {
        if (patch.IsEmpty)
        {
            throw new ArgumentException("Pattern patch must be initialized and non-empty.", nameof(patch));
        }

        var bitmap = BitmapSource.Create(
            patch.Width,
            patch.Height,
            StandardDpi,
            StandardDpi,
            PixelFormats.Gray8,
            palette: null,
            pixels: patch.Pixels,
            stride: patch.Width);
        bitmap.Freeze();
        return bitmap;
    }

    private static RectifiedPatternPatch SynthesizeFeaturePatch(
        RoiGeometry teachingRegion,
        ShapeModel? shapeModel,
        PatMaxTrainedTemplate? patMaxTemplate,
        CpuTemplate? cpuTemplate,
        CorrelationTemplate? templateModel)
    {
        double roiW = Math.Max(1.0, teachingRegion.Width);
        double roiH = Math.Max(1.0, teachingRegion.Height);
        double maxDim = Math.Max(roiW, roiH);
        double scale = Math.Clamp(DefaultSyntheticSize / maxDim, MinSyntheticDimension / maxDim, 2.0);
        int width = Math.Clamp((int)Math.Round(roiW * scale), MinSyntheticDimension, MaxSyntheticDimension);
        int height = Math.Clamp((int)Math.Round(roiH * scale), MinSyntheticDimension, MaxSyntheticDimension);

        var pixels = new byte[checked(width * height)];
        Array.Fill(pixels, BackgroundDarkGray);

        // Draw subtle inspection grid
        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * width;
            bool isHorizontalGrid = (y % GridSpacingPixels) == 0;
            for (int x = 0; x < width; x++)
            {
                if (isHorizontalGrid || (x % GridSpacingPixels) == 0)
                {
                    pixels[rowOffset + x] = GridLineGray;
                }
            }
        }

        // Draw inner ROI frame border
        int marginX = Math.Max(2, width / BorderMarginDivisor);
        int marginY = Math.Max(2, height / BorderMarginDivisor);
        for (int x = marginX; x < width - marginX; x++)
        {
            pixels[(marginY * width) + x] = OutlineGray;
            pixels[((height - 1 - marginY) * width) + x] = OutlineGray;
        }
        for (int y = marginY; y < height - marginY; y++)
        {
            pixels[(y * width) + marginX] = OutlineGray;
            pixels[(y * width) + (width - 1 - marginX)] = OutlineGray;
        }

        double centerX = width * HalfScale;
        double centerY = height * HalfScale;
        double scaleX = (width - (2 * marginX)) / roiW;
        double scaleY = (height - (2 * marginY)) / roiH;

        // Plot learned features if available
        if (shapeModel is not null && shapeModel.Levels.Length > 0)
        {
            foreach (var f in shapeModel.Levels[0].Features)
            {
                PlotFeatureDot(pixels, width, height, (int)Math.Round(centerX + (f.X * scaleX)), (int)Math.Round(centerY + (f.Y * scaleY)), FeatureHighlightGray);
            }
        }
        else if (patMaxTemplate is not null && !patMaxTemplate.PreviewPoints.IsDefaultOrEmpty)
        {
            foreach (var p in patMaxTemplate.PreviewPoints)
            {
                PlotFeatureDot(pixels, width, height, (int)Math.Round(centerX + (p.X * scaleX)), (int)Math.Round(centerY + (p.Y * scaleY)), FeatureHighlightGray);
            }
        }
        else if (cpuTemplate is not null && cpuTemplate.Matcher.PreviewPoints.Count > 0)
        {
            foreach (var p in cpuTemplate.Matcher.PreviewPoints)
            {
                PlotFeatureDot(pixels, width, height, (int)Math.Round(centerX + (p.X * scaleX)), (int)Math.Round(centerY + (p.Y * scaleY)), FeatureHighlightGray);
            }
        }
        else if (templateModel is not null)
        {
            // Draw a distinctive template inner gradient box
            for (int y = marginY + InnerBoxInsetPixels; y < height - marginY - InnerBoxInsetPixels; y++)
            {
                int rowOffset = y * width;
                for (int x = marginX + InnerBoxInsetPixels; x < width - marginX - InnerBoxInsetPixels; x++)
                {
                    int grad = GradientBaseIntensity + (((x + y) * GradientRangeIntensity) / Math.Max(1, width + height));
                    pixels[rowOffset + x] = (byte)Math.Clamp(grad, byte.MinValue, byte.MaxValue);
                }
            }
        }

        // Draw center fiducial crosshair
        int cx = width / 2;
        int cy = height / 2;
        for (int d = -CrosshairArmRadius; d <= CrosshairArmRadius; d++)
        {
            int px = Math.Clamp(cx + d, 0, width - 1);
            int py = Math.Clamp(cy + d, 0, height - 1);
            pixels[(cy * width) + px] = CenterCrossGray;
            pixels[(py * width) + cx] = CenterCrossGray;
        }

        return RectifiedPatternPatch.Create(pixels, width, height);
    }

    private static void PlotFeatureDot(byte[] pixels, int width, int height, int x, int y, byte intensity)
    {
        for (int dy = -1; dy <= 1; dy++)
        {
            int py = y + dy;
            if ((uint)py >= (uint)height)
            {
                continue;
            }
            int rowOffset = py * width;
            for (int dx = -1; dx <= 1; dx++)
            {
                int px = x + dx;
                if ((uint)px < (uint)width)
                {
                    pixels[rowOffset + px] = intensity;
                }
            }
        }
    }
}
