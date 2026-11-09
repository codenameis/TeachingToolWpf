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
}
