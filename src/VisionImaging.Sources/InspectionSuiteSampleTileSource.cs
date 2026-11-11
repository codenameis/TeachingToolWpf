using System.Numerics;
using System.Runtime.CompilerServices;
using VisionImaging.Abstractions;

namespace VisionImaging;

/// <summary>
/// 7대 상용 정밀 검사 스위트(Metrology, GoldenVariation, BlobRle, Curvilinear, FewShot, PatchAnomaly, Calibration)
/// 각각의 알고리즘 특성에 맞춘 고대비 서브픽셀 절차적 합성 샘플 타일 소스.
/// 디스크 I/O 없이 메모리에서 즉시 로드되며, 다중 해상도 피라미드 표시(Rgba32)와 원본 정밀 검사(Gray8)를 모두 지원한다.
/// </summary>
public sealed class InspectionSuiteSampleTileSource : ITileSource, IOriginalGrayTileSource
{
    public const string KeyPrefix = "suite-sample:";
    public const int DefaultWidth = 1200;
    public const int DefaultHeight = 800;

    private const byte BackgroundGray = 38;
    private const float BrightPadGray = 216f;
    private const float MediumPadGray = 165f;
    private const float DarkFeatureGray = 52f;
    private const float BrightDefectGray = 246f;

    private readonly string _sceneKey;
    private readonly byte[] _masterGray8;

    public ImageInfo Info { get; }

    public InspectionSuiteSampleTileSource(ImageReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        int width = reference.Width > 0 ? reference.Width : DefaultWidth;
        int height = reference.Height > 0 ? reference.Height : DefaultHeight;
        _sceneKey = reference.Key.StartsWith(KeyPrefix, StringComparison.OrdinalIgnoreCase)
            ? reference.Key[KeyPrefix.Length..].Trim().ToLowerInvariant()
            : "metrology";

        string title = string.IsNullOrWhiteSpace(reference.Name)
            ? $"검사 스위트 샘플 ({_sceneKey})"
            : reference.Name;

        Info = new(
            reference.Key,
            title,
            width,
            height,
            PixelKind.Rgba32,
            ImageInfo.CalculateLevelCount(width, height));

        _masterGray8 = RenderMasterScene(_sceneKey, width, height);
    }

    public static bool TryCreate(ImageReference? reference, out ITileSource source)
    {
        if (reference is not null && reference.Key.StartsWith(KeyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            source = new InspectionSuiteSampleTileSource(reference);
            return true;
        }

        source = null!;
        return false;
    }

    public PixelBuffer Read(TileKey key, CancellationToken cancellationToken)
        => ReadTilePixels(key, PixelKind.Rgba32, cancellationToken);

    public PixelBuffer ReadOriginalGray8(int tileX, int tileY, CancellationToken cancellationToken)
        => ReadTilePixels(new(0, tileX, tileY), PixelKind.Gray8, cancellationToken);

    public void Dispose()
    {
    }

    private PixelBuffer ReadTilePixels(TileKey key, PixelKind kind, CancellationToken token)
    {
        if (key.Level < 0 || key.Level >= Info.Levels || key.X < 0 || key.Y < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(key));
        }

        int factor = 1 << key.Level;
        int levelWidth = (Info.Width + factor - 1) / factor;
        int levelHeight = (Info.Height + factor - 1) / factor;
        int left = checked(key.X * ImageInfo.TileSize);
        int top = checked(key.Y * ImageInfo.TileSize);
        if (left >= levelWidth || top >= levelHeight)
        {
            throw new ArgumentOutOfRangeException(nameof(key));
        }

        int tileW = Math.Min(ImageInfo.TileSize, levelWidth - left);
        int tileH = Math.Min(ImageInfo.TileSize, levelHeight - top);
        var buffer = new PixelBuffer(tileW, tileH, kind);
        try
        {
            int srcW = Info.Width;
            int srcH = Info.Height;
            ReadOnlySpan<byte> master = _masterGray8;

            for (int y = 0; y < tileH; y++)
            {
                token.ThrowIfCancellationRequested();
                int srcY = Math.Min(srcH - 1, (top + y) * factor);
                Span<byte> dstRow = buffer.Span.Slice(y * buffer.Stride, tileW * (kind == PixelKind.Gray8 ? 1 : 4));

                if (kind == PixelKind.Gray8)
                {
                    if (factor == 1)
                    {
                        master.Slice((srcY * srcW) + left, tileW).CopyTo(dstRow);
                    }
                    else
                    {
                        for (int x = 0; x < tileW; x++)
                        {
                            int srcX = Math.Min(srcW - 1, (left + x) * factor);
                            dstRow[x] = master[(srcY * srcW) + srcX];
                        }
                    }
                }
                else
                {
                    for (int x = 0; x < tileW; x++)
                    {
                        int srcX = Math.Min(srcW - 1, (left + x) * factor);
                        byte g = master[(srcY * srcW) + srcX];
                        int idx = x * 4;
                        dstRow[idx] = g;
                        dstRow[idx + 1] = g;
                        dstRow[idx + 2] = g;
                        dstRow[idx + 3] = 255;
                    }
                }
            }

            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    private static byte[] RenderMasterScene(string sceneKey, int width, int height)
    {
        byte[] canvas = new byte[checked(width * height)];
        InitializeBackground(canvas, width, height);

        switch (sceneKey)
        {
            case "metrology":
                RenderMetrologyScene(canvas, width, height);
                break;
            case "golden":
                RenderGoldenVariationScene(canvas, width, height);
                break;
            case "blob":
                RenderBlobRleScene(canvas, width, height);
                break;
            case "curvilinear":
                RenderCurvilinearScene(canvas, width, height);
                break;
            case "fewshot":
                RenderFewShotScene(canvas, width, height);
                break;
            case "padim":
                RenderPadimScene(canvas, width, height);
                break;
            case "calibration":
                RenderCalibrationScene(canvas, width, height);
                break;
            case "corner":
                RenderCornerAlignmentScene(canvas, width, height);
                break;
            case "ocrocv":
                RenderIndustrialOcrOcvScene(canvas, width, height);
                break;
            case "lightocr":
                RenderLightweightOcrScene(canvas, width, height);
                break;
            case "autoteach":
                RenderAutoTeachingScene(canvas, width, height);
                break;
            case "preprocessing":
                RenderPreprocessingScene(canvas, width, height);
                break;
            case "ellipse":
                RenderAdvancedMetrologyEllipseScene(canvas, width, height);
                break;
            case "watershed":
                RenderAdvancedBlobWatershedScene(canvas, width, height);
                break;
            case "datamatrix":
                RenderDataMatrixScene(canvas, width, height);
                break;
            case "fftgabor":
                RenderFftAndGaborScene(canvas, width, height);
                break;
            case "barcode1d":
                RenderBarcode1DScene(canvas, width, height);
                break;
            case "photometric":
                RenderPhotometricStereoScene(canvas, width, height);
                break;
            case "geomtolerance":
                RenderGeometricToleranceScene(canvas, width, height);
                break;
            default:
                RenderMetrologyScene(canvas, width, height);
                break;
        }

        return canvas;
    }

    private static void InitializeBackground(Span<byte> canvas, int width, int height)
    {
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = canvas.Slice(y * width, width);
            uint rowSeed = (uint)((y * 1664525) + 1013904223);
            for (int x = 0; x < width; x++)
            {
                uint hash = rowSeed ^ (uint)(x * 2246822519u);
                int noise = (int)(hash & 3u) - 1;
                row[x] = (byte)(BackgroundGray + noise);
            }
        }
    }

    private static void RenderMetrologyScene(Span<byte> canvas, int width, int height)
    {
        // 1. Left: Precision oriented PCB trace (center = 320, 400, width = 54.4 px, height = 280 px, angle = 10 deg)
        float angleRad = 10f * (MathF.PI / 180f);
        DrawOrientedBox(canvas, width, height, new Vector2(320.35f, 400.25f), 54.4f, 280f, angleRad, BrightPadGray);

        // Reference parallel guard traces outside the main trace
        DrawOrientedBox(canvas, width, height, new Vector2(170.0f, 400.0f), 20.0f, 260f, angleRad, MediumPadGray);
        DrawOrientedBox(canvas, width, height, new Vector2(470.0f, 400.0f), 20.0f, 260f, angleRad, MediumPadGray);

        // 2. Right: Precision circular fiducial/annular pad (center = 820.4, 400.6, outer R = 96.5 px, inner hole R = 36.0 px)
        Vector2 circleCenter = new(820.40f, 400.60f);
        DrawCircle(canvas, width, height, circleCenter, 96.5f, BrightPadGray);
        DrawCircle(canvas, width, height, circleCenter, 32.0f, DarkFeatureGray);
    }

    private static void RenderGoldenVariationScene(Span<byte> canvas, int width, int height)
    {
        // Top row (Y = 230): 4 Golden Normal semiconductor dies with slight subpixel jitter
        ReadOnlySpan<float> trainX = [200.2f, 460.4f, 720.1f, 980.3f];
        for (int i = 0; i < trainX.Length; i++)
        {
            float cy = 230.25f + ((i & 1) * 0.25f);
            DrawSemiconductorDie(canvas, width, height, new Vector2(trainX[i], cy), angleRad: 0.02f, defectKind: 0);
        }

        // Bottom row (Y = 570): 1 Normal Die + 1 Scratch Defective Die + 1 Solder Bridge Defective Die
        DrawSemiconductorDie(canvas, width, height, new Vector2(250.25f, 570.30f), angleRad: 0.02f, defectKind: 0);
        DrawSemiconductorDie(canvas, width, height, new Vector2(600.30f, 570.25f), angleRad: 0.02f, defectKind: 1);
        DrawSemiconductorDie(canvas, width, height, new Vector2(950.20f, 570.35f), angleRad: 0.02f, defectKind: 2);
    }

    private static void RenderBlobRleScene(Span<byte> canvas, int width, int height)
    {
        // 1. Circular pad blob + isolated 1x1 / 2x2 salt noise (removed by RLE Opening R=1)
        DrawCircle(canvas, width, height, new Vector2(260.5f, 400.5f), 68.0f, BrightPadGray);
        InjectSaltNoise(canvas, width, height, 150, 290, 220, 220, seed: 101u);

        // 2. Rotated capsule/elliptical blob + isolated salt noise
        Vector2 c2 = new(600.4f, 400.6f);
        float angle2 = 24f * (MathF.PI / 180f);
        Vector2 dir2 = new(MathF.Cos(angle2), MathF.Sin(angle2));
        DrawCapsule(canvas, width, height, c2 - (dir2 * 48f), c2 + (dir2 * 48f), widthPixels: 76f, BrightPadGray);
        InjectSaltNoise(canvas, width, height, 480, 290, 240, 220, seed: 202u);

        // 3. Oriented rectangular IC pad blob + isolated salt noise
        float angle3 = -15f * (MathF.PI / 180f);
        DrawOrientedBox(canvas, width, height, new Vector2(940.3f, 400.4f), 136f, 88f, angle3, BrightPadGray);
        InjectSaltNoise(canvas, width, height, 820, 290, 240, 220, seed: 303u);
    }

    private static void RenderCurvilinearScene(Span<byte> canvas, int width, int height)
    {
        const int steps = 36;
        const float startX = 220f;
        const float stepX = 21.0f;
        const float beadWidth = 14.0f;

        // Track 1 (Top, Y ~ 250): Continuous normal sealant bead
        for (int i = 0; i < steps - 1; i++)
        {
            float x0 = startX + (i * stepX);
            float x1 = startX + ((i + 1) * stepX);
            float y0 = 250f + (18f * MathF.Sin(i * 0.24f));
            float y1 = 250f + (18f * MathF.Sin((i + 1) * 0.24f));
            DrawCapsule(canvas, width, height, new Vector2(x0, y0), new Vector2(x1, y1), beadWidth, BrightPadGray);
        }

        // Track 2 (Bottom, Y ~ 550): Defective sealant bead with Gap (stations 12..15) and Necking (stations 24..27)
        for (int i = 0; i < steps - 1; i++)
        {
            if (i >= 12 && i <= 15)
            {
                continue; // Open dispensing gap defect
            }

            float segWidth = (i >= 24 && i <= 27) ? beadWidth * 0.38f : beadWidth; // Necking defect
            float x0 = startX + (i * stepX);
            float x1 = startX + ((i + 1) * stepX);
            float y0 = 550f + (18f * MathF.Sin(i * 0.24f));
            float y1 = 550f + (18f * MathF.Sin((i + 1) * 0.24f));
            DrawCapsule(canvas, width, height, new Vector2(x0, y0), new Vector2(x1, y1), segWidth, BrightPadGray);
        }
    }

    private static void RenderFewShotScene(Span<byte> canvas, int width, int height)
    {
        ReadOnlySpan<float> colX = [200f, 460f, 720f, 980f];

        // Row 1 (Y = 180): 4 Normal training chips (Class 0: Good)
        for (int i = 0; i < colX.Length; i++)
        {
            float jitterAngle = (i - 1.5f) * 0.025f;
            DrawChipComponent(canvas, width, height, new Vector2(colX[i], 180f), jitterAngle, mode: 0);
        }

        // Row 2 (Y = 400): 4 Known-Defect training chips (Class 1: Defect with missing/cracked corner pad)
        for (int i = 0; i < colX.Length; i++)
        {
            float jitterAngle = (1.5f - i) * 0.025f;
            DrawChipComponent(canvas, width, height, new Vector2(colX[i], 400f), jitterAngle, mode: 1);
        }

        // Row 3 (Y = 630): 3 Test chips -> Good (250), Known Defect (600), Unseen OOD Foreign Contamination (950)
        DrawChipComponent(canvas, width, height, new Vector2(250f, 630f), 0.03f, mode: 0);
        DrawChipComponent(canvas, width, height, new Vector2(600f, 630f), -0.02f, mode: 1);
        DrawChipComponent(canvas, width, height, new Vector2(950f, 630f), 0.01f, mode: 2);
    }

    private static void RenderPadimScene(Span<byte> canvas, int width, int height)
    {
        ReadOnlySpan<float> trainX = [200.2f, 460.3f, 720.1f, 980.4f];

        // Top row (Y = 230): 4 Normal BGA matrix tiles
        for (int i = 0; i < trainX.Length; i++)
        {
            DrawBgaGridTile(canvas, width, height, new Vector2(trainX[i], 230.2f), defectMode: 0);
        }

        // Bottom row (Y = 570): 1 Normal tile + 1 Missing Ball tile + 1 Foreign Bridge tile
        DrawBgaGridTile(canvas, width, height, new Vector2(250.2f, 570.2f), defectMode: 0);
        DrawBgaGridTile(canvas, width, height, new Vector2(600.2f, 570.2f), defectMode: 1);
        DrawBgaGridTile(canvas, width, height, new Vector2(950.2f, 570.2f), defectMode: 2);
    }

    private static void RenderCalibrationScene(Span<byte> canvas, int width, int height)
    {
        // Outer calibration plate frame
        DrawOrientedBox(canvas, width, height, new Vector2(600f, 400f), 820f, 600f, 0f, 58f);

        // 3x3 9-Point Hand-Eye Calibration fiducial markers at X = 320, 600, 880 and Y = 190, 400, 610
        ReadOnlySpan<float> gx = [320f, 600f, 880f];
        ReadOnlySpan<float> gy = [190f, 400f, 610f];

        for (int r = 0; r < gy.Length; r++)
        {
            for (int c = 0; c < gx.Length; c++)
            {
                Vector2 pt = new(gx[c], gy[r]);
                DrawCircle(canvas, width, height, pt, 28f, BrightPadGray);
                DrawCircle(canvas, width, height, pt, 10f, DarkFeatureGray);
                DrawOrientedBox(canvas, width, height, pt, 72f, 4f, 0f, BrightPadGray);
                DrawOrientedBox(canvas, width, height, pt, 4f, 72f, 0f, BrightPadGray);
            }
        }
    }

    private static void DrawSemiconductorDie(Span<byte> canvas, int width, int height, Vector2 center, float angleRad, int defectKind)
    {
        DrawOrientedBox(canvas, width, height, center, 112f, 112f, angleRad, BrightPadGray);
        DrawOrientedBox(canvas, width, height, center, 82f, 82f, angleRad, DarkFeatureGray);

        // Inner 2x2 circuit pads
        float cos = MathF.Cos(angleRad);
        float sin = MathF.Sin(angleRad);
        for (int sy = -1; sy <= 1; sy += 2)
        {
            for (int sx = -1; sx <= 1; sx += 2)
            {
                float lx = sx * 22f;
                float ly = sy * 22f;
                Vector2 padCenter = new(center.X + (lx * cos) - (ly * sin), center.Y + (lx * sin) + (ly * cos));
                DrawOrientedBox(canvas, width, height, padCenter, 22f, 22f, angleRad, BrightPadGray);
            }
        }

        if (defectKind == 1)
        {
            // Dark diagonal scratch + bright spatter defect
            DrawCapsule(
                canvas,
                width,
                height,
                center + new Vector2(-26f, -20f),
                center + new Vector2(28f, 22f),
                widthPixels: 9.0f,
                intensity: BackgroundGray);
            DrawCircle(canvas, width, height, center + new Vector2(18f, -16f), 11.5f, BrightDefectGray);
        }
        else if (defectKind == 2)
        {
            // Bright solder bridge across the center dark cavity
            DrawOrientedBox(canvas, width, height, center + new Vector2(0f, -4f), 46f, 24f, angleRad + 0.3f, BrightDefectGray);
        }
    }

    private static void DrawChipComponent(Span<byte> canvas, int width, int height, Vector2 center, float angleRad, int mode)
    {
        // Package body + 4 leads
        DrawOrientedBox(canvas, width, height, center, 84f, 64f, angleRad, MediumPadGray);
        DrawOrientedBox(canvas, width, height, center, 56f, 40f, angleRad, DarkFeatureGray);
        DrawCircle(canvas, width, height, center, 12f, BrightPadGray);

        if (mode == 1)
        {
            // Known Defect class: cracked dark trench cutting across upper-right quadrant
            DrawOrientedBox(canvas, width, height, center + new Vector2(16f, -12f), 36f, 28f, angleRad + 0.4f, BackgroundGray);
        }
        else if (mode == 2)
        {
            // OOD Anomaly class: large bright diagonal cross contamination completely unseen in training
            DrawOrientedBox(canvas, width, height, center, 88f, 18f, angleRad + 0.78f, BrightDefectGray);
            DrawOrientedBox(canvas, width, height, center, 88f, 18f, angleRad - 0.78f, BrightDefectGray);
            DrawCircle(canvas, width, height, center, 24f, BrightDefectGray);
        }
    }

    private static void DrawBgaGridTile(Span<byte> canvas, int width, int height, Vector2 center, int defectMode)
    {
        DrawOrientedBox(canvas, width, height, center, 116f, 116f, 0f, 64f);
        for (int r = -2; r <= 2; r++)
        {
            for (int c = -2; c <= 2; c++)
            {
                if (defectMode == 1 && r == 1 && c == 1)
                {
                    continue; // Missing BGA solder ball anomaly
                }

                Vector2 ballPos = center + new Vector2(c * 20f, r * 20f);
                DrawCircle(canvas, width, height, ballPos, 6.5f, BrightPadGray);
            }
        }

        if (defectMode == 2)
        {
            // Localized solder bridge / foreign splat anomaly at (-16, 14)
            DrawCapsule(canvas, width, height, center + new Vector2(-24f, 10f), center + new Vector2(-4f, 22f), 14f, BrightDefectGray);
        }
    }

    private static void InjectSaltNoise(Span<byte> canvas, int width, int height, int left, int top, int boxW, int boxH, uint seed)
    {
        for (int i = 0; i < 6; i++)
        {
            seed = (seed * 1664525u) + 1013904223u;
            int nx = left + (int)(seed % (uint)boxW);
            seed = (seed * 1664525u) + 1013904223u;
            int ny = top + (int)(seed % (uint)boxH);
            if (nx >= 1 && nx < width - 1 && ny >= 1 && ny < height - 1)
            {
                // Only place isolated noise in dark background around the main pad
                if (canvas[(ny * width) + nx] < 80)
                {
                    canvas[(ny * width) + nx] = 235;
                }
            }
        }
    }

    private static void DrawOrientedBox(
        Span<byte> canvas,
        int width,
        int height,
        Vector2 center,
        float boxWidth,
        float boxHeight,
        float angleRad,
        float intensity)
    {
        float cos = MathF.Cos(angleRad);
        float sin = MathF.Sin(angleRad);
        float halfW = boxWidth * 0.5f;
        float halfH = boxHeight * 0.5f;
        float boundRadius = MathF.Sqrt((halfW * halfW) + (halfH * halfH)) + 3f;

        int x0 = Math.Max(0, (int)MathF.Floor(center.X - boundRadius));
        int x1 = Math.Min(width - 1, (int)MathF.Ceiling(center.X + boundRadius));
        int y0 = Math.Max(0, (int)MathF.Floor(center.Y - boundRadius));
        int y1 = Math.Min(height - 1, (int)MathF.Ceiling(center.Y + boundRadius));

        for (int y = y0; y <= y1; y++)
        {
            float dy = y - center.Y;
            int rowOffset = y * width;
            for (int x = x0; x <= x1; x++)
            {
                float dx = x - center.X;
                float lx = MathF.Abs((dx * cos) + (dy * sin));
                float ly = MathF.Abs((-dx * sin) + (dy * cos));
                float signedDist = MathF.Max(lx - halfW, ly - halfH);
                BlendSignedDistance(ref canvas[rowOffset + x], signedDist, intensity);
            }
        }
    }

    private static void DrawCircle(
        Span<byte> canvas,
        int width,
        int height,
        Vector2 center,
        float radius,
        float intensity)
    {
        int x0 = Math.Max(0, (int)MathF.Floor(center.X - radius - 3f));
        int x1 = Math.Min(width - 1, (int)MathF.Ceiling(center.X + radius + 3f));
        int y0 = Math.Max(0, (int)MathF.Floor(center.Y - radius - 3f));
        int y1 = Math.Min(height - 1, (int)MathF.Ceiling(center.Y + radius + 3f));

        for (int y = y0; y <= y1; y++)
        {
            float dy = y - center.Y;
            int rowOffset = y * width;
            for (int x = x0; x <= x1; x++)
            {
                float dx = x - center.X;
                float signedDist = MathF.Sqrt((dx * dx) + (dy * dy)) - radius;
                BlendSignedDistance(ref canvas[rowOffset + x], signedDist, intensity);
            }
        }
    }

    private static void DrawCapsule(
        Span<byte> canvas,
        int width,
        int height,
        Vector2 p0,
        Vector2 p1,
        float widthPixels,
        float intensity)
    {
        float halfW = widthPixels * 0.5f;
        int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(p0.X, p1.X) - halfW - 3f));
        int x1 = Math.Min(width - 1, (int)MathF.Ceiling(MathF.Max(p0.X, p1.X) + halfW + 3f));
        int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(p0.Y, p1.Y) - halfW - 3f));
        int y1 = Math.Min(height - 1, (int)MathF.Ceiling(MathF.Max(p0.Y, p1.Y) + halfW + 3f));

        Vector2 ab = p1 - p0;
        float abLenSq = MathF.Max(1e-6f, Vector2.Dot(ab, ab));

        for (int y = y0; y <= y1; y++)
        {
            int rowOffset = y * width;
            for (int x = x0; x <= x1; x++)
            {
                Vector2 pt = new(x, y);
                float t = Math.Clamp(Vector2.Dot(pt - p0, ab) / abLenSq, 0f, 1f);
                Vector2 closest = p0 + (ab * t);
                float signedDist = Vector2.Distance(pt, closest) - halfW;
                BlendSignedDistance(ref canvas[rowOffset + x], signedDist, intensity);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void BlendSignedDistance(ref byte pixel, float signedDist, float targetIntensity)
    {
        if (signedDist >= 1.2f)
        {
            return;
        }

        float coverage = signedDist <= -1.2f
            ? 1f
            : 0.5f - (signedDist / 2.4f);
        coverage = Math.Clamp(coverage, 0f, 1f);
        float blended = (pixel * (1f - coverage)) + (targetIntensity * coverage);
        pixel = (byte)Math.Clamp((int)MathF.Round(blended), 0, 255);
    }

    private static void RenderCornerAlignmentScene(Span<byte> canvas, int width, int height)
    {
        // Precision chamfered substrate rectangle centered at (600.35, 400.25) with 640x400 size and +2.2 deg rotation,
        // featuring a 24.0 px 45-degree corner chamfer, 4.5 px rounding, side chipping/burr defects, and optical PSF + sensor noise.
        const float centerX = 600.35f;
        const float centerY = 400.25f;
        const float halfW = 320.0f;
        const float halfH = 200.0f;
        const float chamferLen = 24.0f;
        const float cornerRadius = 4.5f;
        const float chippingDepth = 3.2f;
        const float chippingHalfLen = 7.0f;
        const float chippingOffset = 82.0f;
        const float burrHeight = 3.5f;
        const float burrHalfLen = 7.0f;
        const float burrOffset = 86.0f;
        const float invSqrt2 = 0.70710678f;
        const float psfSigma = 0.68f;
        const float noiseSigma = 0.75f;

        float angleRad = 2.2f * (MathF.PI / 180f);
        float cosA = MathF.Cos(angleRad);
        float sinA = MathF.Sin(angleRad);
        float invSqrt2Sigma = 1.0f / (MathF.Sqrt(2.0f) * psfSigma);
        float contrast = BrightPadGray - BackgroundGray;

        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * width;
            float dy = y - centerY;
            for (int x = 0; x < width; x++)
            {
                float dx = x - centerX;
                float lx = (dx * cosA) + (dy * sinA);
                float ly = (-dx * sinA) + (dy * cosA);

                float u = halfW - MathF.Abs(lx);
                float v = halfH - MathF.Abs(ly);

                float shiftedV = v;
                float distChip = MathF.Abs(u - chippingOffset);
                if (distChip < chippingHalfLen)
                {
                    float phase = MathF.Cos((MathF.PI * 0.5f) * (distChip / chippingHalfLen));
                    shiftedV -= chippingDepth * (phase * phase);
                }

                float shiftedU = u;
                float distBurr = MathF.Abs(v - burrOffset);
                if (distBurr < burrHalfLen)
                {
                    float phase = MathF.Cos((MathF.PI * 0.5f) * (distBurr / burrHalfLen));
                    shiftedU += burrHeight * (phase * phase);
                }

                float sdf;
                if (shiftedU < cornerRadius && shiftedV < cornerRadius)
                {
                    float qu = cornerRadius - shiftedU;
                    float qv = cornerRadius - shiftedV;
                    sdf = MathF.Sqrt((qu * qu) + (qv * qv)) - cornerRadius;
                }
                else
                {
                    sdf = MathF.Max(-shiftedU, -shiftedV);
                }

                float chamferSdf = (chamferLen - (shiftedU + shiftedV)) * invSqrt2;
                sdf = MathF.Max(sdf, chamferSdf);

                // Abramowitz & Stegun erf approximation for optical PSF integration
                float z = sdf * invSqrt2Sigma;
                float sign = z < 0.0f ? -1.0f : 1.0f;
                float az = MathF.Abs(z);
                float t = 1.0f / (1.0f + (0.3275911f * az));
                float poly = (((((1.061405429f * t) - 1.453152027f) * t + 1.421413741f) * t - 0.284496736f) * t + 0.254829592f) * t;
                float erfVal = sign * (1.0f - (poly * MathF.Exp(-(az * az))));

                float coverage = 0.5f * (1.0f - erfVal);
                uint h1 = (uint)((x * 374761393) + (y * 668265263) + 2026);
                h1 = (h1 ^ (h1 >> 13)) * 1274126177u;
                float uNoise = ((h1 & 0xFFFFu) / 65535.0f) - 0.5f;
                float val = BackgroundGray + (contrast * coverage) + (uNoise * 2.0f * noiseSigma);
                canvas[rowOffset + x] = (byte)Math.Clamp((int)MathF.Round(val), 0, 255);
            }
        }
    }

    private static void RenderIndustrialOcrOcvScene(Span<byte> canvas, int width, int height)
    {
        canvas.Fill(232);
        Render5x7TextLine(canvas, width, height, "LOT-2026-A09", centerX: 600f, centerY: 260f, continuous: true, fg: 28);
        Render5x7TextLine(canvas, width, height, "EXP.2027.11.24", centerX: 600f, centerY: 520f, continuous: false, fg: 28);
    }

    private static void RenderLightweightOcrScene(Span<byte> canvas, int width, int height)
    {
        canvas.Fill(232);
        Render5x7TextLine(canvas, width, height, "WAFER-094817-A2", centerX: 600f, centerY: 260f, continuous: true, fg: 28);
        Render5x7TextLine(canvas, width, height, "BATCH-7731-X9", centerX: 600f, centerY: 520f, continuous: true, fg: 28);
    }

    private static void RenderAutoTeachingScene(Span<byte> canvas, int width, int height)
    {
        canvas.Fill(232);
        RenderSyntheticPart(canvas, width, height, partType: 1, cx: 280.37f, cy: 319.76f, angleDeg: 23.7f);
        RenderSyntheticPart(canvas, width, height, partType: 2, cx: 660.42f, cy: 340.18f, angleDeg: -41.3f);
        RenderSyntheticPart(canvas, width, height, partType: 3, cx: 979.63f, cy: 540.29f, angleDeg: 112.4f);
    }

    private static void Render5x7TextLine(
        Span<byte> canvas,
        int width,
        int height,
        string text,
        float centerX,
        float centerY,
        bool continuous,
        byte fg)
    {
        const float glyphW = 16f;
        const float glyphH = 26f;
        const float pitchX = 24f;
        float totalW = ((text.Length - 1) * pitchX) + glyphW;
        float startX = centerX - (totalW * 0.5f);
        float startY = centerY - (glyphH * 0.5f);

        for (int i = 0; i < text.Length; i++)
        {
            RenderSingle5x7Glyph(canvas, width, height, text[i], startX + (i * pitchX), startY, glyphW, glyphH, continuous, fg);
        }
    }

    private static void RenderSingle5x7Glyph(
        Span<byte> canvas,
        int width,
        int height,
        char ch,
        float x0,
        float y0,
        float glyphW,
        float glyphH,
        bool continuous,
        byte fg)
    {
        ReadOnlySpan<byte> rows = Get5x7Rows(char.ToUpperInvariant(ch));
        if (rows.IsEmpty)
        {
            return;
        }

        float cellW = glyphW / 5f;
        float cellH = glyphH / 7f;
        float dotR = MathF.Min(cellW, cellH) * (continuous ? 0.58f : 0.44f);

        int minX = Math.Max(0, (int)MathF.Floor(x0 - 3f));
        int maxX = Math.Min(width - 1, (int)MathF.Ceiling(x0 + glyphW + 3f));
        int minY = Math.Max(0, (int)MathF.Floor(y0 - 3f));
        int maxY = Math.Min(height - 1, (int)MathF.Ceiling(y0 + glyphH + 3f));

        for (int py = minY; py <= maxY; py++)
        {
            for (int px = minX; px <= maxX; px++)
            {
                float minDist = 1e6f;
                for (int r = 0; r < 7; r++)
                {
                    byte mask = rows[r];
                    for (int c = 0; c < 5; c++)
                    {
                        if (((mask >> (4 - c)) & 1) == 0)
                        {
                            continue;
                        }

                        float cx = x0 + ((c + 0.5f) * cellW);
                        float cy = y0 + ((r + 0.5f) * cellH);
                        float d = MathF.Sqrt(((px - cx) * (px - cx)) + ((py - cy) * (py - cy))) - dotR;
                        if (d < minDist) minDist = d;

                        if (continuous)
                        {
                            if (c + 1 < 5 && ((mask >> (4 - (c + 1))) & 1) != 0)
                            {
                                float nx = x0 + ((c + 1.5f) * cellW);
                                float sd = MathF.Sqrt((MathF.Max(0f, MathF.Abs(px - (0.5f * (cx + nx))) - (0.5f * cellW)) * MathF.Max(0f, MathF.Abs(px - (0.5f * (cx + nx))) - (0.5f * cellW))) + ((py - cy) * (py - cy))) - (dotR * 0.92f);
                                if (sd < minDist) minDist = sd;
                            }
                            if (r + 1 < 7 && ((rows[r + 1] >> (4 - c)) & 1) != 0)
                            {
                                float ny = y0 + ((r + 1.5f) * cellH);
                                float sd = MathF.Sqrt(((px - cx) * (px - cx)) + (MathF.Max(0f, MathF.Abs(py - (0.5f * (cy + ny))) - (0.5f * cellH)) * MathF.Max(0f, MathF.Abs(py - (0.5f * (cy + ny))) - (0.5f * cellH)))) - (dotR * 0.92f);
                                if (sd < minDist) minDist = sd;
                            }
                        }
                    }
                }

                BlendSignedDistance(ref canvas[(py * width) + px], minDist, fg);
            }
        }
    }

    private static ReadOnlySpan<byte> Get5x7Rows(char ch) => ch switch
    {
        '0' => [0b01110, 0b10001, 0b10011, 0b10101, 0b11001, 0b10001, 0b01110],
        '1' => [0b00100, 0b01100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110],
        '2' => [0b01110, 0b10001, 0b00001, 0b00110, 0b01000, 0b10000, 0b11111],
        '3' => [0b11110, 0b00001, 0b00001, 0b01110, 0b00001, 0b00001, 0b11110],
        '4' => [0b00010, 0b00110, 0b01010, 0b10010, 0b11111, 0b00010, 0b00010],
        '6' => [0b00110, 0b01000, 0b10000, 0b11110, 0b10001, 0b10001, 0b01110],
        '7' => [0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b01000, 0b01000],
        '8' => [0b01110, 0b10001, 0b10001, 0b01110, 0b10001, 0b10001, 0b01110],
        '9' => [0b01110, 0b10001, 0b10001, 0b01111, 0b00001, 0b00010, 0b01100],
        'A' => [0b01110, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001],
        'B' => [0b11110, 0b10001, 0b10001, 0b11110, 0b10001, 0b10001, 0b11110],
        'C' => [0b01110, 0b10001, 0b10000, 0b10000, 0b10000, 0b10001, 0b01110],
        'E' => [0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b11111],
        'F' => [0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b10000],
        'H' => [0b10001, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001],
        'L' => [0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b11111],
        'O' => [0b01110, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110],
        'P' => [0b11110, 0b10001, 0b10001, 0b11110, 0b10000, 0b10000, 0b10000],
        'R' => [0b11110, 0b10001, 0b10001, 0b11110, 0b10100, 0b10010, 0b10001],
        'T' => [0b11111, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100],
        'W' => [0b10001, 0b10001, 0b10001, 0b10101, 0b10101, 0b10101, 0b01010],
        'X' => [0b10001, 0b10001, 0b01010, 0b00100, 0b01010, 0b10001, 0b10001],
        '.' => [0b00000, 0b00000, 0b00000, 0b00000, 0b00000, 0b01100, 0b01100],
        '-' => [0b00000, 0b00000, 0b00000, 0b11111, 0b00000, 0b00000, 0b00000],
        _ => []
    };

    private static void RenderSyntheticPart(Span<byte> canvas, int width, int height, int partType, float cx, float cy, float angleDeg)
    {
        float rad = angleDeg * (MathF.PI / 180f);
        float cos = MathF.Cos(rad);
        float sin = MathF.Sin(rad);
        int minX = Math.Max(1, (int)(cx - 120f));
        int maxX = Math.Min(width - 2, (int)(cx + 120f));
        int minY = Math.Max(1, (int)(cy - 120f));
        int maxY = Math.Min(height - 2, (int)(cy + 120f));

        static float SdfBox(float x, float y, float halfW, float halfH)
        {
            float dx = MathF.Abs(x) - halfW;
            float dy = MathF.Abs(y) - halfH;
            float outside = MathF.Sqrt((MathF.Max(dx, 0f) * MathF.Max(dx, 0f)) + (MathF.Max(dy, 0f) * MathF.Max(dy, 0f)));
            float inside = MathF.Min(MathF.Max(dx, dy), 0f);
            return outside + inside;
        }

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                float dx = (x + 0.5f) - cx;
                float dy = (y + 0.5f) - cy;
                float lx = (dx * cos) + (dy * sin);
                float ly = (-dx * sin) + (dy * cos);

                float sdf;
                if (partType == 1)
                {
                    float r = MathF.Sqrt((lx * lx) + (ly * ly));
                    float outerRing = MathF.Abs(r - 58f) - 6f;
                    float innerRing = MathF.Abs(r - 26f) - 5f;
                    float keywayTab = SdfBox(lx - 42f, ly, 18f, 8f);
                    float lCornerH = SdfBox(lx + 45f, ly + 74f, 28f, 4.5f);
                    float lCornerV = SdfBox(lx + 70f, ly + 50f, 4.5f, 28f);
                    sdf = MathF.Min(MathF.Min(outerRing, innerRing), MathF.Min(keywayTab, MathF.Min(lCornerH, lCornerV)));
                }
                else if (partType == 2)
                {
                    float boxFrame = MathF.Abs(SdfBox(lx, ly, 62f, 42f)) - 4.5f;
                    float pin1Dot = MathF.Sqrt(((lx + 38f) * (lx + 38f)) + ((ly + 20f) * (ly + 20f))) - 8.5f;
                    float pins = 1e6f;
                    for (int i = -2; i <= 2; i++)
                    {
                        if (i == 0) continue;
                        float px = i * 22f;
                        pins = MathF.Min(pins, SdfBox(lx - px, ly + 56f, 4.5f, 12f));
                        pins = MathF.Min(pins, SdfBox(lx - px, ly - 56f, 4.5f, 12f));
                    }
                    sdf = MathF.Min(MathF.Min(boxFrame, pin1Dot), pins);
                }
                else
                {
                    float cross = MathF.Min(SdfBox(lx, ly, 52f, 11f), SdfBox(lx, ly, 11f, 52f));
                    float orientDot = MathF.Sqrt(((lx - 36f) * (lx - 36f)) + ((ly + 36f) * (ly + 36f))) - 7f;
                    float outerOct = MathF.Abs(SdfBox(lx, ly, 76f, 76f)) - 3.5f;
                    sdf = MathF.Min(MathF.Min(cross, orientDot), outerOct);
                }

                BlendSignedDistance(ref canvas[(y * width) + x], sdf, 28f);
            }
        }
    }

    private static void RenderPreprocessingScene(Span<byte> canvas, int width, int height)
    {
        // Apply strong 2D biquadratic illumination shading + vignetting across the canvas
        float halfW = MathF.Max(1.0f, width * 0.5f);
        float halfH = MathF.Max(1.0f, height * 0.5f);

        for (int y = 0; y < height; y++)
        {
            float v = (y - halfH) / halfH;
            int rowOffset = y * width;
            for (int x = 0; x < width; x++)
            {
                float u = (x - halfW) / halfW;
                float bg = 112.0f + (46.0f * u) - (34.0f * v) - (28.0f * ((u * u) + (v * v)));
                uint hash = (uint)((x * 374761393) + (y * 668265263));
                int grain = (int)(hash & 7u) - 3;
                canvas[rowOffset + x] = (byte)Math.Clamp((int)MathF.Round(bg + grain), 10, 245);
            }
        }

        // Zone 1 (Left, X=250): Precision pads under severe uneven lighting gradient
        DrawOrientedBox(canvas, width, height, new Vector2(250f, 320f), 96f, 64f, 0.08f, 218f);
        DrawCircle(canvas, width, height, new Vector2(250f, 480f), 42f, 224f);

        // Zone 2 (Center, X=600): Pad with heavy surface speckle grain for Perona-Malik Anisotropic Diffusion
        DrawOrientedBox(canvas, width, height, new Vector2(600f, 400f), 140f, 140f, 0.0f, 212f);
        InjectSaltNoise(canvas, width, height, 535, 335, 130, 130, seed: 707u);

        // Zone 3 (Right, X=950): Bright pad with bright spatter/pinholes for Vincent Morphological Reconstruction
        DrawCircle(canvas, width, height, new Vector2(950f, 400f), 64f, 225f);
        InjectSaltNoise(canvas, width, height, 830, 280, 240, 240, seed: 909u);
    }

    private static void RenderAdvancedMetrologyEllipseScene(Span<byte> canvas, int width, int height)
    {
        // 1. Left (X=300, Y=400): Multi-edge trace with outer distractor edges for Composite Caliper (First/Last/Span)
        DrawOrientedBox(canvas, width, height, new Vector2(190f, 400f), 14f, 280f, 0.0f, BrightDefectGray);
        DrawOrientedBox(canvas, width, height, new Vector2(300.35f, 400.25f), 82.6f, 280f, 0.0f, BrightPadGray);
        DrawOrientedBox(canvas, width, height, new Vector2(410f, 400f), 14f, 280f, 0.0f, BrightDefectGray);

        // 2. Center (X=640, Y=400): Rotated precision ellipse (A = 96.4 px, B = 58.2 px, theta = 22.5 deg)
        DrawRotatedEllipse(canvas, width, height, new Vector2(640.40f, 400.30f), 96.4f, 58.2f, 22.5f * (MathF.PI / 180f), BrightPadGray);

        // 3. Right (X=980, Y=400): Tilted elliptical via / connector hole (A = 78.5 px, B = 48.0 px, theta = -31.0 deg)
        DrawRotatedEllipse(canvas, width, height, new Vector2(980.25f, 400.60f), 78.5f, 48.0f, -31.0f * (MathF.PI / 180f), BrightPadGray);
    }

    private static void DrawRotatedEllipse(
        Span<byte> canvas,
        int width,
        int height,
        Vector2 center,
        float semiMajor,
        float semiMinor,
        float angleRad,
        float intensity)
    {
        (float sinA, float cosA) = MathF.SinCos(angleRad);
        float maxR = MathF.Max(semiMajor, semiMinor) + 4.0f;
        int x0 = Math.Max(0, (int)MathF.Floor(center.X - maxR));
        int x1 = Math.Min(width - 1, (int)MathF.Ceiling(center.X + maxR));
        int y0 = Math.Max(0, (int)MathF.Floor(center.Y - maxR));
        int y1 = Math.Min(height - 1, (int)MathF.Ceiling(center.Y + maxR));

        for (int y = y0; y <= y1; y++)
        {
            float dy = y - center.Y;
            int rowOffset = y * width;
            for (int x = x0; x <= x1; x++)
            {
                float dx = x - center.X;
                float u = (dx * cosA) + (dy * sinA);
                float v = (-dx * sinA) + (dy * cosA);
                float ua = u / semiMajor;
                float vb = v / semiMinor;
                float f = (ua * ua) + (vb * vb) - 1.0f;
                float gu = u / (semiMajor * semiMajor);
                float gv = v / (semiMinor * semiMinor);
                float grad = MathF.Max(1e-4f, 2.0f * MathF.Sqrt((gu * gu) + (gv * gv)));
                float signedDist = f / grad;
                BlendSignedDistance(ref canvas[rowOffset + x], signedDist, intensity);
            }
        }
    }

    private static void RenderAdvancedBlobWatershedScene(Span<byte> canvas, int width, int height)
    {
        // 1. Left (X=260, Y=400): Annular bracket pad with 2 internal holes for Euler HoleCount / Solidity / MinAreaBox
        DrawOrientedBox(canvas, width, height, new Vector2(260f, 400f), 156f, 112f, 18.0f * (MathF.PI / 180f), BrightPadGray);
        DrawCircle(canvas, width, height, new Vector2(228f, 390f), 20.0f, BackgroundGray);
        DrawCircle(canvas, width, height, new Vector2(292f, 410f), 20.0f, BackgroundGray);

        // 2. Center (X=610, Y=400): Two touching circular solder balls (radius = 48 px, center distance = 78 px) for EDT Watershed Separation
        DrawCircle(canvas, width, height, new Vector2(572f, 388f), 46.0f, BrightPadGray);
        DrawCircle(canvas, width, height, new Vector2(648f, 412f), 46.0f, BrightPadGray);

        // 3. Right (X=950, Y=400): Three touching pill/circular particles in a cluster for EDT Watershed Separation
        DrawCircle(canvas, width, height, new Vector2(918f, 372f), 38.0f, BrightPadGray);
        DrawCircle(canvas, width, height, new Vector2(982f, 372f), 38.0f, BrightPadGray);
        DrawCircle(canvas, width, height, new Vector2(950f, 430f), 38.0f, BrightPadGray);
    }

    private static void RenderDataMatrixScene(Span<byte> canvas, int width, int height)
    {
        canvas.Fill(230);

        // Render exact ISO/IEC 16022 ECC 200 12x12 DataMatrix symbols (Data=5, ECC=7)
        // Symbol 1 (Left, origin=240, 300, moduleSize=14 -> 168x168 px, 0.0 deg): Payload = "LOT26A"
        ReadOnlySpan<byte> symbol1Data = [77, 80, 85, 156, 66];
        RenderPrecomputedEcc200Matrix12x12(canvas, width, height, originX: 240, originY: 300, moduleSize: 14, symbol1Data, darkGray: 24, lightGray: 230);

        // Symbol 2 (Right, origin=760, 300, moduleSize=14 -> 168x168 px, 0.0 deg): Payload = "OK9912"
        ReadOnlySpan<byte> symbol2Data = [80, 76, 229, 142, 129];
        RenderPrecomputedEcc200Matrix12x12(canvas, width, height, originX: 760, originY: 300, moduleSize: 14, symbol2Data, darkGray: 28, lightGray: 230);

        // Symbol 3 (Bottom-Center, center=584, 656, moduleSize=10 -> 120x120 px, +22.5 deg): Payload = "ROT23" (R=83, O=80, T=85, 23=153, PAD=129)
        ReadOnlySpan<byte> symbol3Data = [83, 80, 85, 153, 129];
        RenderRotatedPrecomputedEcc200Matrix12x12(
            canvas,
            width,
            height,
            centerX: 584.0f,
            centerY: 656.0f,
            moduleSize: 10.0f,
            angleRad: 22.5f * (MathF.PI / 180.0f),
            symbol3Data,
            darkGray: 26,
            lightGray: 230);
    }

    private static void RenderPrecomputedEcc200Matrix12x12(
        Span<byte> canvas,
        int width,
        int height,
        int originX,
        int originY,
        int moduleSize,
        ReadOnlySpan<byte> dataCodewords5,
        byte darkGray,
        byte lightGray)
    {
        const int symbolSize = 12;
        const int mapSize = 10;
        const int dataCount = 5;
        const int eccCount = 7;

        Span<byte> codewords12 = stackalloc byte[dataCount + eccCount];
        dataCodewords5.Slice(0, dataCount).CopyTo(codewords12);
        ComputeEcc200ReedSolomon7(dataCodewords5.Slice(0, dataCount), codewords12.Slice(dataCount, eccCount));

        Span<int> bitMap = stackalloc int[mapSize * mapSize];
        BuildUtahMap10x10(bitMap);

        for (int r = 0; r < symbolSize; r++)
        {
            for (int c = 0; c < symbolSize; c++)
            {
                bool isDark = IsEcc200DarkModule12x12(r, c, bitMap, codewords12);
                byte val = isDark ? darkGray : lightGray;
                int py0 = originY + (r * moduleSize);
                int px0 = originX + (c * moduleSize);
                for (int dy = 0; dy < moduleSize; dy++)
                {
                    int y = py0 + dy;
                    if ((uint)y >= (uint)height) continue;
                    Span<byte> row = canvas.Slice(y * width, width);
                    for (int dx = 0; dx < moduleSize; dx++)
                    {
                        int x = px0 + dx;
                        if ((uint)x < (uint)width) row[x] = val;
                    }
                }
            }
        }
    }

    private static void RenderRotatedPrecomputedEcc200Matrix12x12(
        Span<byte> canvas,
        int width,
        int height,
        float centerX,
        float centerY,
        float moduleSize,
        float angleRad,
        ReadOnlySpan<byte> dataCodewords5,
        byte darkGray,
        byte lightGray)
    {
        const int symbolSize = 12;
        const int mapSize = 10;
        const int dataCount = 5;
        const int eccCount = 7;

        Span<byte> codewords12 = stackalloc byte[dataCount + eccCount];
        dataCodewords5.Slice(0, dataCount).CopyTo(codewords12);
        ComputeEcc200ReedSolomon7(dataCodewords5.Slice(0, dataCount), codewords12.Slice(dataCount, eccCount));

        Span<int> bitMap = stackalloc int[mapSize * mapSize];
        BuildUtahMap10x10(bitMap);

        Span<bool> darkGrid = stackalloc bool[symbolSize * symbolSize];
        for (int r = 0; r < symbolSize; r++)
        {
            for (int c = 0; c < symbolSize; c++)
            {
                darkGrid[(r * symbolSize) + c] = IsEcc200DarkModule12x12(r, c, bitMap, codewords12);
            }
        }

        float symSpan = symbolSize * moduleSize;
        float halfSpan = symSpan * 0.5f;
        float quiet = moduleSize * 1.4f;
        float radius = MathF.Sqrt(2.0f * (halfSpan + quiet) * (halfSpan + quiet)) + 2.0f;
        int minX = Math.Max(0, (int)MathF.Floor(centerX - radius));
        int maxX = Math.Min(width - 1, (int)MathF.Ceiling(centerX + radius));
        int minY = Math.Max(0, (int)MathF.Floor(centerY - radius));
        int maxY = Math.Min(height - 1, (int)MathF.Ceiling(centerY + radius));

        (float sinA, float cosA) = MathF.SinCos(angleRad);
        const int subDiv = 4;
        const float subStep = 1.0f / subDiv;
        const float invSamples = 1.0f / (subDiv * subDiv);

        for (int py = minY; py <= maxY; py++)
        {
            Span<byte> row = canvas.Slice(py * width, width);
            for (int px = minX; px <= maxX; px++)
            {
                byte bg = row[px];
                float sum = 0.0f;
                bool touched = false;
                for (int sy = 0; sy < subDiv; sy++)
                {
                    float wy = py + ((sy + 0.5f) * subStep);
                    float dy = wy - centerY;
                    for (int sx = 0; sx < subDiv; sx++)
                    {
                        float wx = px + ((sx + 0.5f) * subStep);
                        float dx = wx - centerX;
                        float lu = (dx * cosA) + (dy * sinA) + halfSpan;
                        float lv = (-dx * sinA) + (dy * cosA) + halfSpan;
                        if (lu >= 0.0f && lu < symSpan && lv >= 0.0f && lv < symSpan)
                        {
                            touched = true;
                            int col = Math.Clamp((int)(lu / moduleSize), 0, symbolSize - 1);
                            int rIdx = Math.Clamp((int)(lv / moduleSize), 0, symbolSize - 1);
                            sum += darkGrid[(rIdx * symbolSize) + col] ? darkGray : lightGray;
                        }
                        else if (lu >= -quiet && lu <= symSpan + quiet && lv >= -quiet && lv <= symSpan + quiet)
                        {
                            touched = true;
                            sum += lightGray;
                        }
                        else
                        {
                            sum += bg;
                        }
                    }
                }

                if (touched)
                {
                    row[px] = (byte)Math.Clamp((int)MathF.Round(sum * invSamples), 0, 255);
                }
            }
        }
    }

    private static bool IsEcc200DarkModule12x12(int r, int c, ReadOnlySpan<int> bitMap, ReadOnlySpan<byte> codewords12)
    {
        const int symbolSize = 12;
        const int mapSize = 10;
        if (r == symbolSize - 1 || c == 0)
        {
            return true;
        }
        if (r == 0)
        {
            return (c & 1) == 0;
        }
        if (c == symbolSize - 1)
        {
            return ((symbolSize - 1 - r) & 1) == 0;
        }

        int bitIdx = bitMap[((r - 1) * mapSize) + (c - 1)];
        if (bitIdx <= 0)
        {
            return false;
        }

        int cwIdx = (bitIdx - 1) / 8;
        int bitPos = 7 - ((bitIdx - 1) % 8);
        return cwIdx < codewords12.Length && ((codewords12[cwIdx] >> bitPos) & 1) != 0;
    }

    private static void ComputeEcc200ReedSolomon7(ReadOnlySpan<byte> data, Span<byte> eccOut)
    {
        const int gfPoly = 301;
        Span<byte> exp = stackalloc byte[512];
        Span<byte> log = stackalloc byte[256];
        int val = 1;
        for (int i = 0; i < 255; i++)
        {
            exp[i] = (byte)val;
            exp[i + 255] = (byte)val;
            log[val] = (byte)i;
            val <<= 1;
            if (val >= 256)
            {
                val ^= gfPoly;
            }
        }
        log[0] = 0;

        static byte GfMul(byte a, byte b, ReadOnlySpan<byte> expTbl, ReadOnlySpan<byte> logTbl)
            => (a == 0 || b == 0) ? (byte)0 : expTbl[logTbl[a] + logTbl[b]];

        int eccCount = eccOut.Length;
        Span<byte> gen = stackalloc byte[eccCount + 1];
        gen.Clear();
        gen[0] = 1;
        for (int i = 1; i <= eccCount; i++)
        {
            byte root = exp[i];
            for (int j = i; j >= 1; j--)
            {
                gen[j] = (byte)(gen[j - 1] ^ GfMul(gen[j], root, exp, log));
            }
            gen[0] = GfMul(gen[0], root, exp, log);
        }

        eccOut.Clear();
        for (int i = 0; i < data.Length; i++)
        {
            byte feedback = (byte)(data[i] ^ eccOut[0]);
            for (int j = 0; j < eccCount - 1; j++)
            {
                eccOut[j] = (byte)(eccOut[j + 1] ^ GfMul(feedback, gen[eccCount - 1 - j], exp, log));
            }
            eccOut[eccCount - 1] = GfMul(feedback, gen[0], exp, log);
        }
    }

    private static void BuildUtahMap10x10(Span<int> array)
    {
        const int nrow = 10;
        const int ncol = 10;
        array.Fill(-1);
        int bitIndex = 1;
        int row = 4;
        int col = 0;

        static void PlaceBit(Span<int> arr, int r, int c, ref int idx)
        {
            if (r < 0)
            {
                r += nrow;
                c += 4 - ((nrow + 4) % 8);
            }
            if (c < 0)
            {
                c += ncol;
                r += 4 - ((ncol + 4) % 8);
            }
            arr[(r * ncol) + c] = idx++;
        }

        static void PlaceUtah(Span<int> arr, int r, int c, ref int idx)
        {
            PlaceBit(arr, r - 2, c - 2, ref idx);
            PlaceBit(arr, r - 2, c - 1, ref idx);
            PlaceBit(arr, r - 1, c - 2, ref idx);
            PlaceBit(arr, r - 1, c - 1, ref idx);
            PlaceBit(arr, r - 1, c, ref idx);
            PlaceBit(arr, r, c - 2, ref idx);
            PlaceBit(arr, r, c - 1, ref idx);
            PlaceBit(arr, r, c, ref idx);
        }

        do
        {
            if (row == nrow && col == 0)
            {
                PlaceBit(array, nrow - 1, 0, ref bitIndex);
                PlaceBit(array, nrow - 1, 1, ref bitIndex);
                PlaceBit(array, nrow - 1, 2, ref bitIndex);
                PlaceBit(array, 0, ncol - 2, ref bitIndex);
                PlaceBit(array, 0, ncol - 1, ref bitIndex);
                PlaceBit(array, 1, ncol - 1, ref bitIndex);
                PlaceBit(array, 2, ncol - 1, ref bitIndex);
                PlaceBit(array, 3, ncol - 1, ref bitIndex);
            }
            if (row == nrow - 2 && col == 0 && (ncol % 4) != 0)
            {
                PlaceBit(array, nrow - 3, 0, ref bitIndex);
                PlaceBit(array, nrow - 2, 0, ref bitIndex);
                PlaceBit(array, nrow - 1, 0, ref bitIndex);
                PlaceBit(array, 0, ncol - 4, ref bitIndex);
                PlaceBit(array, 0, ncol - 3, ref bitIndex);
                PlaceBit(array, 0, ncol - 2, ref bitIndex);
                PlaceBit(array, 0, ncol - 1, ref bitIndex);
                PlaceBit(array, 1, ncol - 1, ref bitIndex);
            }

            do
            {
                if (row < nrow && col >= 0 && array[(row * ncol) + col] < 0)
                {
                    PlaceUtah(array, row, col, ref bitIndex);
                }
                row -= 2;
                col += 2;
            }
            while (row >= 0 && col < ncol);

            row += 1;
            col += 3;

            do
            {
                if (row >= 0 && col < ncol && array[(row * ncol) + col] < 0)
                {
                    PlaceUtah(array, row, col, ref bitIndex);
                }
                row += 2;
                col -= 2;
            }
            while (row < nrow && col >= 0);

            row += 3;
            col += 1;
        }
        while (row < nrow || col < ncol);

        if (array[(nrow * ncol) - 1] < 0)
        {
            array[(nrow * ncol) - 1] = 0;
            array[(nrow * ncol) - ncol - 2] = 0;
        }
    }

    private static void RenderFftAndGaborScene(Span<byte> canvas, int width, int height)
    {
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = canvas.Slice(y * width, width);
            for (int x = 0; x < width; x++)
            {
                if (x < 420)
                {
                    // Zone 1: Periodic 2D Moiré mesh interference + bright fiducial pads
                    float moire = 42f * MathF.Cos(2f * MathF.PI * x / 8.0f) * MathF.Cos(2f * MathF.PI * y / 8.0f);
                    row[x] = (byte)Math.Clamp((int)MathF.Round(105f + moire), 0, 255);
                }
                else if (x < 800)
                {
                    // Zone 2: Severe exponential illumination gradient for Homomorphic separation
                    float nx = (x - 420f) / 380f;
                    float ny = y / (float)height;
                    float illum = 0.25f + (1.35f * MathF.Exp(-2.2f * ((nx - 0.8f) * (nx - 0.8f) + (ny - 0.2f) * (ny - 0.2f))));
                    row[x] = (byte)Math.Clamp((int)MathF.Round(96f * illum), 12, 248);
                }
                else
                {
                    // Zone 3: Oriented directional brushed texture + diagonal scratch for Gabor
                    float stripe = 22f * MathF.Sin(((x * 0.866f) + (y * 0.5f)) * (2f * MathF.PI / 7.0f));
                    row[x] = (byte)Math.Clamp((int)MathF.Round(110f + stripe), 0, 255);
                }
            }
        }

        DrawCircle(canvas, width, height, new Vector2(210f, 340f), 46f, 224f);
        DrawCircle(canvas, width, height, new Vector2(210f, 470f), 34f, 218f);
        DrawOrientedBox(canvas, width, height, new Vector2(610f, 400f), 96f, 64f, 0.15f, 212f);
        DrawCapsule(canvas, width, height, new Vector2(900f, 320f), new Vector2(1060f, 480f), 9.0f, 242f);
    }

    private static void RenderBarcode1DScene(Span<byte> canvas, int width, int height)
    {
        canvas.Fill(236);
        RenderCode128SymbolInScene(canvas, width, height, "LOT26A", barLeftX: 110, barTopY: 290, barHeight: 190, moduleWidthPx: 3);
        RenderCode128SymbolInScene(canvas, width, height, "OK99", barLeftX: 690, barTopY: 290, barHeight: 190, moduleWidthPx: 3);
        RenderRotatedCode128SymbolInScene(
            canvas,
            width,
            height,
            payload: "ANG18",
            centerX: 600.0f,
            centerY: 648.0f,
            barHeight: 108.0f,
            moduleWidthPx: 3.0f,
            angleRad: -18.0f * (MathF.PI / 180.0f));
    }

    private static void RenderCode128SymbolInScene(
        Span<byte> canvas,
        int width,
        int height,
        string payload,
        int barLeftX,
        int barTopY,
        int barHeight,
        int moduleWidthPx)
    {
        ReadOnlySpan<int> patterns =
        [
            212222, 222122, 222221, 121223, 121322, 131222, 122213, 122312, 132212, 221213,
            221312, 231212, 112232, 122132, 122231, 113222, 123122, 123221, 223211, 221132,
            221231, 213212, 223112, 312131, 311222, 321122, 321221, 312212, 322112, 322211,
            212123, 212321, 232121, 111323, 131123, 131321, 112313, 132113, 132311, 211313,
            231113, 231311, 112133, 112331, 132131, 113123, 113321, 133121, 313121, 211331,
            231131, 213113, 213311, 213131, 311123, 311321, 331121, 312113, 312311, 332111,
            314111, 221411, 431111, 111224, 111422, 121124, 121421, 141122, 141221, 112214,
            112412, 122114, 122411, 142112, 142211, 241211, 221114, 413111, 241112, 134111,
            111242, 121142, 121241, 114212, 124112, 124211, 411212, 421112, 421211, 212141,
            214121, 412121, 111143, 111341, 131141, 114113, 114311, 411113, 411311, 113141,
            114131, 311141, 411131, 211412, 211214, 211232, 233111
        ];

        Span<int> codes = stackalloc int[payload.Length + 3];
        codes[0] = 104;
        int checksum = 104;
        for (int i = 0; i < payload.Length; i++)
        {
            int val = Math.Clamp(payload[i] - 32, 0, 94);
            codes[i + 1] = val;
            checksum += val * (i + 1);
        }
        codes[payload.Length + 1] = checksum % 103;
        codes[payload.Length + 2] = 106;

        int cursorX = barLeftX;
        Span<int> widths = stackalloc int[7];
        for (int c = 0; c < codes.Length; c++)
        {
            int pat = patterns[codes[c]];
            for (int e = 5; e >= 0; e--)
            {
                widths[e] = pat % 10;
                pat /= 10;
            }
            int elemCount = c == codes.Length - 1 ? 7 : 6;
            if (elemCount == 7) widths[6] = 2;

            for (int e = 0; e < elemCount; e++)
            {
                bool isBar = (e & 1) == 0;
                int pxW = widths[e] * moduleWidthPx;
                byte color = isBar ? (byte)22 : (byte)236;
                for (int y = barTopY; y < barTopY + barHeight && y < height; y++)
                {
                    Span<byte> row = canvas.Slice(y * width, width);
                    for (int dx = 0; dx < pxW; dx++)
                    {
                        int x = cursorX + dx;
                        if ((uint)x < (uint)width) row[x] = color;
                    }
                }
                cursorX += pxW;
            }
        }
    }

    private static void RenderRotatedCode128SymbolInScene(
        Span<byte> canvas,
        int width,
        int height,
        string payload,
        float centerX,
        float centerY,
        float barHeight,
        float moduleWidthPx,
        float angleRad)
    {
        ReadOnlySpan<int> patterns =
        [
            212222, 222122, 222221, 121223, 121322, 131222, 122213, 122312, 132212, 221213,
            221312, 231212, 112232, 122132, 122231, 113222, 123122, 123221, 223211, 221132,
            221231, 213212, 223112, 312131, 311222, 321122, 321221, 312212, 322112, 322211,
            212123, 212321, 232121, 111323, 131123, 131321, 112313, 132113, 132311, 211313,
            231113, 231311, 112133, 112331, 132131, 113123, 113321, 133121, 313121, 211331,
            231131, 213113, 213311, 213131, 311123, 311321, 331121, 312113, 312311, 332111,
            314111, 221411, 431111, 111224, 111422, 121124, 121421, 141122, 141221, 112214,
            112412, 122114, 122411, 142112, 142211, 241211, 221114, 413111, 241112, 134111,
            111242, 121142, 121241, 114212, 124112, 124211, 411212, 421112, 421211, 212141,
            214121, 412121, 111143, 111341, 131141, 114113, 114311, 411113, 411311, 113141,
            114131, 311141, 411131, 211412, 211214, 211232, 233111
        ];

        Span<int> codes = stackalloc int[payload.Length + 3];
        codes[0] = 104;
        int checksum = 104;
        for (int i = 0; i < payload.Length; i++)
        {
            int val = Math.Clamp(payload[i] - 32, 0, 94);
            codes[i + 1] = val;
            checksum += val * (i + 1);
        }
        codes[payload.Length + 1] = checksum % 103;
        codes[payload.Length + 2] = 106;

        int totalModules = ((codes.Length - 1) * 11) + 13;
        Span<bool> moduleDark = stackalloc bool[totalModules];
        Span<int> widths = stackalloc int[7];
        int modCursor = 0;
        for (int c = 0; c < codes.Length; c++)
        {
            int pat = patterns[codes[c]];
            for (int e = 5; e >= 0; e--)
            {
                widths[e] = pat % 10;
                pat /= 10;
            }
            int elemCount = c == codes.Length - 1 ? 7 : 6;
            if (elemCount == 7) widths[6] = 2;

            for (int e = 0; e < elemCount; e++)
            {
                bool isBar = (e & 1) == 0;
                for (int m = 0; m < widths[e]; m++)
                {
                    moduleDark[modCursor++] = isBar;
                }
            }
        }

        float symW = totalModules * moduleWidthPx;
        float halfW = symW * 0.5f;
        float halfH = barHeight * 0.5f;
        float quietU = moduleWidthPx * 11.0f;
        float quietV = 8.0f;
        float radius = MathF.Sqrt(((halfW + quietU) * (halfW + quietU)) + ((halfH + quietV) * (halfH + quietV))) + 2.0f;

        int minX = Math.Max(0, (int)MathF.Floor(centerX - radius));
        int maxX = Math.Min(width - 1, (int)MathF.Ceiling(centerX + radius));
        int minY = Math.Max(0, (int)MathF.Floor(centerY - radius));
        int maxY = Math.Min(height - 1, (int)MathF.Ceiling(centerY + radius));

        (float sinA, float cosA) = MathF.SinCos(angleRad);
        const int subDiv = 4;
        const float subStep = 1.0f / subDiv;
        const float invSamples = 1.0f / (subDiv * subDiv);

        for (int py = minY; py <= maxY; py++)
        {
            Span<byte> row = canvas.Slice(py * width, width);
            for (int px = minX; px <= maxX; px++)
            {
                byte bg = row[px];
                float sum = 0.0f;
                bool touched = false;
                for (int sy = 0; sy < subDiv; sy++)
                {
                    float wy = py + ((sy + 0.5f) * subStep);
                    float dy = wy - centerY;
                    for (int sx = 0; sx < subDiv; sx++)
                    {
                        float wx = px + ((sx + 0.5f) * subStep);
                        float dx = wx - centerX;
                        float lu = (dx * cosA) + (dy * sinA) + halfW;
                        float lv = (-dx * sinA) + (dy * cosA) + halfH;
                        if (lu >= 0.0f && lu < symW && lv >= 0.0f && lv < barHeight)
                        {
                            touched = true;
                            int mIdx = Math.Clamp((int)(lu / moduleWidthPx), 0, totalModules - 1);
                            sum += moduleDark[mIdx] ? 22.0f : 236.0f;
                        }
                        else if (lu >= -quietU && lu <= symW + quietU && lv >= -quietV && lv <= barHeight + quietV)
                        {
                            touched = true;
                            sum += 236.0f;
                        }
                        else
                        {
                            sum += bg;
                        }
                    }
                }

                if (touched)
                {
                    row[px] = (byte)Math.Clamp((int)MathF.Round(sum * invSamples), 0, 255);
                }
            }
        }
    }

    private static void RenderPhotometricStereoScene(Span<byte> canvas, int width, int height)
    {
        canvas.Fill(48);
        // Left ROI (center 330, 400, 320x320): 4-quadrant Lambertian normal surface (no 3D dent, only 2D dark stain)
        RenderFourQuadrantPhotometricPatch(canvas, width, height, left: 170, top: 240, quadSize: 160, has3dDent: false);
        // Right ROI (center 870, 400, 320x320): 4-quadrant Lambertian surface with 3D micro-dent & scratch
        RenderFourQuadrantPhotometricPatch(canvas, width, height, left: 710, top: 240, quadSize: 160, has3dDent: true);
    }

    private static void RenderFourQuadrantPhotometricPatch(
        Span<byte> canvas,
        int width,
        int height,
        int left,
        int top,
        int quadSize,
        bool has3dDent)
    {
        const float sinSlant = 0.70710678f;
        const float cosSlant = 0.70710678f;

        for (int qy = 0; qy < quadSize; qy++)
        {
            for (int qx = 0; qx < quadSize; qx++)
            {
                float cx = quadSize * 0.5f;
                float cy = quadSize * 0.5f;
                float dx = qx - cx;
                float dy = qy - cy;

                // 2D albedo stain (flat in 3D, dark in 2D)
                float stainDistSq = ((dx + 28f) * (dx + 28f)) + ((dy - 24f) * (dy - 24f));
                float albedo = stainDistSq < 196f ? 115f : 195f;

                float dzdx = 0f;
                float dzdy = 0f;

                if (has3dDent)
                {
                    float dentDistSq = ((dx - 16f) * (dx - 16f)) + ((dy + 12f) * (dy + 12f));
                    float sigmaSq = 72.0f;
                    float depth = -6.5f * MathF.Exp(-dentDistSq / (2f * sigmaSq));
                    dzdx = -depth * (dx - 16f) / sigmaSq;
                    dzdy = -depth * (dy + 12f) / sigmaSq;
                }

                float invNorm = 1.0f / MathF.Sqrt((dzdx * dzdx) + (dzdy * dzdy) + 1.0f);
                float nx = -dzdx * invNorm;
                float ny = -dzdy * invNorm;
                float nz = invNorm;

                byte iEast = (byte)Math.Clamp((int)MathF.Round(albedo * MathF.Max(0f, (nx * sinSlant) + (nz * cosSlant))), 0, 255);
                byte iNorth = (byte)Math.Clamp((int)MathF.Round(albedo * MathF.Max(0f, (-ny * sinSlant) + (nz * cosSlant))), 0, 255);
                byte iWest = (byte)Math.Clamp((int)MathF.Round(albedo * MathF.Max(0f, (-nx * sinSlant) + (nz * cosSlant))), 0, 255);
                byte iSouth = (byte)Math.Clamp((int)MathF.Round(albedo * MathF.Max(0f, (ny * sinSlant) + (nz * cosSlant))), 0, 255);

                int yTop = top + qy;
                int yBot = top + quadSize + qy;
                int xLeft = left + qx;
                int xRight = left + quadSize + qx;

                if ((uint)yTop < (uint)height && (uint)xLeft < (uint)width) canvas[(yTop * width) + xLeft] = iEast;
                if ((uint)yTop < (uint)height && (uint)xRight < (uint)width) canvas[(yTop * width) + xRight] = iNorth;
                if ((uint)yBot < (uint)height && (uint)xLeft < (uint)width) canvas[(yBot * width) + xLeft] = iWest;
                if ((uint)yBot < (uint)height && (uint)xRight < (uint)width) canvas[(yBot * width) + xRight] = iSouth;
            }
        }
    }

    private static void RenderGeometricToleranceScene(Span<byte> canvas, int width, int height)
    {
        // Left Part (OK): Exact 90.0 deg orthogonal datum bracket + concentric bore (offset = 0.0 px)
        RenderPrecisionBracketPart(canvas, width, height, originX: 160f, originY: 220f, partW: 320f, partH: 320f, skewDeg: 0.0f, boreOffsetX: 0.0f, boreOffsetY: 0.0f);

        // Right Part (NG): Skewed datum (+2.6 deg) + eccentric inner bore (offset = 4.2 px)
        RenderPrecisionBracketPart(canvas, width, height, originX: 680f, originY: 220f, partW: 320f, partH: 320f, skewDeg: 2.6f, boreOffsetX: 3.8f, boreOffsetY: 2.2f);
    }

    private static void RenderPrecisionBracketPart(
        Span<byte> canvas,
        int width,
        int height,
        float originX,
        float originY,
        float partW,
        float partH,
        float skewDeg,
        float boreOffsetX,
        float boreOffsetY)
    {
        float skewRad = skewDeg * (MathF.PI / 180f);
        float tanSkew = MathF.Tan(skewRad);

        int x0 = Math.Max(0, (int)(originX - 20));
        int x1 = Math.Min(width - 1, (int)(originX + partW + 20));
        int y0 = Math.Max(0, (int)(originY - 20));
        int y1 = Math.Min(height - 1, (int)(originY + partH + 20));

        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                float topEdgeY = originY + ((x - originX) * tanSkew);
                float leftEdgeX = originX;
                float rightEdgeX = originX + partW;
                float botEdgeY = originY + partH;

                float dTop = y - topEdgeY;
                float dLeft = x - leftEdgeX;
                float dRight = rightEdgeX - x;
                float dBot = botEdgeY - y;
                float minEdgeDist = MathF.Min(MathF.Min(dTop, dLeft), MathF.Min(dRight, dBot));

                if (minEdgeDist > -1.5f)
                {
                    BlendSignedDistance(ref canvas[(y * width) + x], -minEdgeDist, BrightPadGray);
                }
            }
        }

        Vector2 outerBoreCenter = new(originX + (partW * 0.52f), originY + (partH * 0.52f));
        Vector2 innerBoreCenter = new(outerBoreCenter.X + boreOffsetX, outerBoreCenter.Y + boreOffsetY);
        DrawCircle(canvas, width, height, outerBoreCenter, 74.0f, DarkFeatureGray);
        DrawCircle(canvas, width, height, innerBoreCenter, 36.0f, BrightPadGray);
    }
}
