using System.Buffers;
using System.Numerics;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.Synthetic8kTestGen;

/// <summary>
/// Procedurally synthesizes a complete $8192 \times 8192$ (67.1 Mpx) industrial multi-zone inspection canvas in memory
/// using <see cref="Parallel.For(int, int, Action{int})"/> and <see cref="AnalyticSubpixelRenderer"/>.
/// </summary>
public static class Synthetic8kDatasetFactory
{
    // [Math Why]: Background baseline intensity (40) and foreground pad intensity (215) provide 175 DN contrast.
    private const byte BackgroundGrayLevel = 40;
    private const float ForegroundBrightLevel = 215f;
    private const float DefectDarkScratchLevel = 55f;
    private const float DefectBrightSpatterLevel = 240f;

    /// <summary>
    /// Generates the $8192 \times 8192$ master test scene containing dedicated zones for all 7 inspection algorithms.
    /// </summary>
    public static Synthetic8kScene CreateMaster8kScene()
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Synthetic8kScene.TotalPixels8k);
        try
        {
            int width = Synthetic8kScene.Resolution8k;
            int height = Synthetic8kScene.Resolution8k;

        // 1. Parallel fast background initialization with deterministic subtle sensor micro-texture (+/- 1 DN)
        Parallel.For(0, height, r =>
        {
            Span<byte> row = buffer.AsSpan(r * width, width);
            uint rowSeed = (uint)((r * 1664525) + 1013904223);
            for (int c = 0; c < width; c++)
            {
                uint hash = rowSeed ^ (uint)(c * 2246822519u);
                int noise = (int)(hash & 3u) - 1;
                row[c] = (byte)(BackgroundGrayLevel + noise);
            }
        });

        Span<byte> canvas = buffer.AsSpan(0, Synthetic8kScene.TotalPixels8k);

        // =========================================================================================
        // Zone 1 (Top-Left: around (1200, 1200)): Subpixel Metrology (1D Edge Pair, Line, Circle)
        // =========================================================================================
        FixturePose2D tracePose = new(centerX: 1200.37f, centerY: 1150.62f, angleRad: 0.18f, scale: 1.0f);
        const float traceWidth = 46.42f;
        AnalyticSubpixelRenderer.RenderOrientedBox(
            canvas,
            width,
            height,
            width,
            new Vector2(tracePose.CenterX, tracePose.CenterY),
            widthPixels: traceWidth,
            heightPixels: 180.0f,
            angleRad: tracePose.AngleRad,
            foregroundIntensity: ForegroundBrightLevel);

        Vector2 circleCenter = new(1650.28f, 1220.74f);
        const float circleRadius = 84.65f;
        AnalyticSubpixelRenderer.RenderCircle(
            canvas,
            width,
            height,
            width,
            circleCenter,
            circleRadius,
            ForegroundBrightLevel);

        // =========================================================================================
        // Zone 2 (Center: around (3600..4600, 2600)): 8 Normal Semiconductor Dies + 1 Defective Die
        // =========================================================================================
        const int dieWidth = 128;
        const int dieHeight = 128;
        FixturePose2D[] normalPoses = new FixturePose2D[8];
        for (int i = 0; i < normalPoses.Length; i++)
        {
            // Include slight subpixel translation offsets to verify subpixel warping & edge-tolerance robustness
            float cx = 3200.25f + (i * 220.15f);
            float cy = 2600.40f + ((i & 1) * 0.30f);
            normalPoses[i] = new FixturePose2D(cx, cy, angleRad: 0.04f, scale: 1.0f);
            RenderSemiconductorDiePattern(canvas, width, height, normalPoses[i], hasDefect: false);
        }

        FixturePose2D defectivePose = new(centerX: 5000.35f, centerY: 2600.40f, angleRad: 0.04f, scale: 1.0f);
        RenderSemiconductorDiePattern(canvas, width, height, defectivePose, hasDefect: true);
        Vector2 defectLocalOffset = new(18.0f, -14.0f);

        // =========================================================================================
        // Zone 3 (Bottom-Left: around (2200, 5600)): RLE Blob & Morphological Pinhole/Spatter Filtering
        // =========================================================================================
        const int blobWinLeft = 2000;
        const int blobWinTop = 5400;
        const int blobWinWidth = 512;
        const int blobWinHeight = 512;
        Vector2 blobCenterLocal = new(256.5f, 256.5f);
        const float blobRadius = 72.0f;

        AnalyticSubpixelRenderer.RenderCircle(
            canvas,
            width,
            height,
            width,
            new Vector2(blobWinLeft + blobCenterLocal.X, blobWinTop + blobCenterLocal.Y),
            blobRadius,
            ForegroundBrightLevel);

        // Inject 1x1 and 2x2 isolated salt noise outside the blob that morphological opening must eliminate
        canvas[((blobWinTop + 40) * width) + blobWinLeft + 40] = 240;
        canvas[((blobWinTop + 40) * width) + blobWinLeft + 41] = 240;
        canvas[((blobWinTop + 440) * width) + blobWinLeft + 450] = 240;

        // =========================================================================================
        // Zone 4 (Bottom-Right: around (5800..6600, 6200)): Curvilinear Sealant Bead & Gap/Necking
        // =========================================================================================
        const float beadNominalWidth = 14.0f;
        const int waypointCount = 24;
        Vector2[] continuousWaypoints = new Vector2[waypointCount];
        Vector2[] gappedWaypoints = new Vector2[waypointCount];

        for (int i = 0; i < waypointCount; i++)
        {
            float x = 5800f + (i * 22f);
            float yCont = 6100f + (18f * MathF.Sin(i * 0.25f));
            float yGap = 6400f + (18f * MathF.Sin(i * 0.25f));
            continuousWaypoints[i] = new Vector2(x, yCont);
            gappedWaypoints[i] = new Vector2(x, yGap);
        }

        for (int i = 0; i < waypointCount - 1; i++)
        {
            AnalyticSubpixelRenderer.RenderCapsuleSegment(
                canvas,
                width,
                height,
                width,
                continuousWaypoints[i],
                continuousWaypoints[i + 1],
                beadNominalWidth,
                ForegroundBrightLevel);

            // Create an open gap between stations 10 and 13 on the defective bead track
            if (i >= 10 && i <= 12)
            {
                continue;
            }

            AnalyticSubpixelRenderer.RenderCapsuleSegment(
                canvas,
                width,
                height,
                width,
                gappedWaypoints[i],
                gappedWaypoints[i + 1],
                beadNominalWidth,
                ForegroundBrightLevel);
        }

            return new Synthetic8kScene(buffer)
            {
                MetrologyTracePose = tracePose,
                GroundTruthTraceWidthPixels = traceWidth,
                GroundTruthCircleCenter = circleCenter,
                GroundTruthCircleRadiusPixels = circleRadius,
                GroundTruthLineAngleRad = tracePose.AngleRad,
                NormalDiePoses = normalPoses,
                DefectiveDiePose = defectivePose,
                DefectLocalOffsetInDie = defectLocalOffset,
                DieRoiWidth = dieWidth,
                DieRoiHeight = dieHeight,
                BlobRegionWindow = (blobWinLeft, blobWinTop, blobWinWidth, blobWinHeight),
                GroundTruthBlobCentroidLocal = blobCenterLocal,
                GroundTruthBlobRadius = blobRadius,
                ContinuousBeadWaypoints = continuousWaypoints,
                GappedBeadWaypoints = gappedWaypoints,
                GroundTruthBeadWidthPixels = beadNominalWidth
            };
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }

    private static void RenderSemiconductorDiePattern(
        Span<byte> canvas,
        int width,
        int height,
        FixturePose2D diePose,
        bool hasDefect)
    {
        // Outer die frame pad
        AnalyticSubpixelRenderer.RenderOrientedBox(
            canvas,
            width,
            height,
            width,
            new Vector2(diePose.CenterX, diePose.CenterY),
            widthPixels: 92f,
            heightPixels: 92f,
            angleRad: diePose.AngleRad,
            foregroundIntensity: 185f);

        // Inner dark cavity
        AnalyticSubpixelRenderer.RenderOrientedBox(
            canvas,
            width,
            height,
            width,
            new Vector2(diePose.CenterX, diePose.CenterY),
            widthPixels: 56f,
            heightPixels: 56f,
            angleRad: diePose.AngleRad,
            foregroundIntensity: 70f);

        // Central bright contact pad
        AnalyticSubpixelRenderer.RenderCircle(
            canvas,
            width,
            height,
            width,
            new Vector2(diePose.CenterX, diePose.CenterY),
            radiusPixels: 16f,
            foregroundIntensity: ForegroundBrightLevel);

        if (hasDefect)
        {
            // Render a localized bright spatter + dark scratch bridge at local offset (+18, -14)
            Vector2 defectWorld = diePose.LocalToWorld(18.0f, -14.0f);
            AnalyticSubpixelRenderer.RenderCircle(
                canvas,
                width,
                height,
                width,
                defectWorld,
                radiusPixels: 6.5f,
                foregroundIntensity: DefectBrightSpatterLevel);

            Vector2 scratchWorld = diePose.LocalToWorld(-22.0f, 16.0f);
            AnalyticSubpixelRenderer.RenderOrientedBox(
                canvas,
                width,
                height,
                width,
                scratchWorld,
                widthPixels: 14f,
                heightPixels: 5f,
                angleRad: diePose.AngleRad + 0.5f,
                foregroundIntensity: DefectDarkScratchLevel);
        }
    }
}
