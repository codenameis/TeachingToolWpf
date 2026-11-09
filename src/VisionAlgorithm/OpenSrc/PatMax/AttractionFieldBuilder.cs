// File: src/VisionAlgorithm/OpenSrc/PatMax/AttractionFieldBuilder.cs
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace VisionAlgorithm.OpenSrc.PatMax;

/// <summary>
/// Builds a 2D vector attraction distance field (US 7,065,262) mapping each pattern grid cell to the nearest model boundary dipole.
/// </summary>
public static class AttractionFieldBuilder
{
    public const int DefaultPaddingCells = 2;
    public const float DefaultCellSize = 0.5f;
    // [Math Why]: Guarantees at least 2.5 px capture basin in the Voronoi grid even when the user sets a tight Elasticity (e.g. 0.5 ~ 1.0 px),
    // so Coarse quantization offsets (+-2 px at 1/4 scale) are always captured during initial Huber WLS steps.
    public const float MinCaptureBasinPixels = 2.5f;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static AttractionField Build(
        EdgeDipoleSoA centeredDipoles,
        int patternWidth,
        int patternHeight,
        float originX,
        float originY,
        float elasticityPixels,
        float cellSize = DefaultCellSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(centeredDipoles);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(patternWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(patternHeight);
        ArgumentOutOfRangeException.ThrowIfNegative(elasticityPixels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cellSize);

        float minDx;
        float maxDx;
        float minDy;
        float maxDy;
        if (!centeredDipoles.IsEmpty)
        {
            ComputeDipoleBounds(centeredDipoles, out minDx, out maxDx, out minDy, out maxDy);
        }
        else
        {
            minDx = -originX;
            maxDx = patternWidth - originX;
            minDy = -originY;
            maxDy = patternHeight - originY;
        }

        var effectiveBasinPixels = MathF.Max(elasticityPixels, MinCaptureBasinPixels);
        var padCells = (int)MathF.Ceiling(effectiveBasinPixels / cellSize) + DefaultPaddingCells;
        var spanX = MathF.Max(1.0f, maxDx - minDx);
        var spanY = MathF.Max(1.0f, maxDy - minDy);
        var gridWidth = (int)MathF.Ceiling(spanX / cellSize) + (2 * padCells) + 1;
        var gridHeight = (int)MathF.Ceiling(spanY / cellSize) + (2 * padCells) + 1;
        var fieldOriginX = minDx - (padCells * cellSize);
        var fieldOriginY = minDy - (padCells * cellSize);

        var field = AttractionField.Rent(gridWidth, gridHeight, fieldOriginX, fieldOriginY, cellSize);
        try
        {
            PopulateCells(field, centeredDipoles, cancellationToken);
            return field;
        }
        catch
        {
            field.Dispose();
            throw;
        }
    }

    internal static void ComputeDipoleBounds(
        EdgeDipoleSoA dipoles,
        out float minDx,
        out float maxDx,
        out float minDy,
        out float maxDy)
    {
        var count = dipoles.Count;
        if (count == 0)
        {
            minDx = 0f;
            maxDx = 0f;
            minDy = 0f;
            maxDy = 0f;
            return;
        }

        var xSpan = dipoles.X;
        var ySpan = dipoles.Y;
        minDx = xSpan[0];
        maxDx = xSpan[0];
        minDy = ySpan[0];
        maxDy = ySpan[0];

        for (var i = 1; i < count; i++)
        {
            var x = xSpan[i];
            var y = ySpan[i];
            if (x < minDx) minDx = x;
            if (x > maxDx) maxDx = x;
            if (y < minDy) minDy = y;
            if (y > maxDy) maxDy = y;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void PopulateCells(AttractionField field, EdgeDipoleSoA dipoles, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(dipoles);

        var cells = field.Cells;
        if (dipoles.IsEmpty)
        {
            cells.Fill(AttractionCell.Unassigned);
            return;
        }

        var count = dipoles.Count;
        var dipX = dipoles.X;
        var dipY = dipoles.Y;
        var dipCos = dipoles.CosTheta;
        var dipSin = dipoles.SinTheta;

        ref var xRef = ref MemoryMarshal.GetReference(dipX);
        ref var yRef = ref MemoryMarshal.GetReference(dipY);

        var width = field.Width;
        var height = field.Height;
        var stride = field.Stride;
        var originX = field.OriginX;
        var originY = field.OriginY;
        var cellSize = field.CellSize;

        for (var gy = 0; gy < height; gy++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cellY = originY + (gy * cellSize);
            var rowOffset = gy * stride;
            for (var gx = 0; gx < width; gx++)
            {
                var cellX = originX + (gx * cellSize);
                FindNearestDipole(ref xRef, ref yRef, count, cellX, cellY, out var bestIdx, out var bestDistSq);

                if (bestIdx >= 0)
                {
                    var targetX = dipX[bestIdx];
                    var targetY = dipY[bestIdx];
                    cells[rowOffset + gx] = new AttractionCell(
                        DeltaX: targetX - cellX,
                        DeltaY: targetY - cellY,
                        NormalX: dipCos[bestIdx],
                        NormalY: dipSin[bestIdx],
                        Distance: MathF.Sqrt(bestDistSq),
                        NearestDipoleIndex: bestIdx);
                }
                else
                {
                    cells[rowOffset + gx] = AttractionCell.Unassigned;
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FindNearestDipole(
        ref float xRef,
        ref float yRef,
        int count,
        float cellX,
        float cellY,
        out int bestIndex,
        out float bestDistSq)
    {
        bestIndex = AttractionCell.UnassignedIndex;
        bestDistSq = float.PositiveInfinity;
        var i = 0;

        // [Perf Why]: Evaluate 8 dipole squared Euclidean distances per cycle in AVX2 with FMA, reducing horizontally only when a lane improves the current minimum.
        if (Avx2.IsSupported && count >= Vector256<float>.Count)
        {
            var vCellX = Vector256.Create(cellX);
            var vCellY = Vector256.Create(cellY);
            var vMinDistSq = Vector256.Create(float.PositiveInfinity);
            var simdLimit = count & ~(Vector256<float>.Count - 1);

            for (; i < simdLimit; i += Vector256<float>.Count)
            {
                var vx = Vector256.LoadUnsafe(ref Unsafe.Add(ref xRef, i));
                var vy = Vector256.LoadUnsafe(ref Unsafe.Add(ref yRef, i));
                var dx = vx - vCellX;
                var dy = vy - vCellY;
                var distSq = Fma.IsSupported
                    ? Fma.MultiplyAdd(dx, dx, dy * dy)
                    : (dx * dx) + (dy * dy);

                var cmp = Avx.Compare(distSq, vMinDistSq, FloatComparisonMode.OrderedLessThanNonSignaling);
                if (Avx.MoveMask(cmp) != 0)
                {
                    vMinDistSq = Avx.Min(vMinDistSq, distSq);
                    for (var lane = 0; lane < Vector256<float>.Count; lane++)
                    {
                        var d = distSq.GetElement(lane);
                        if (d < bestDistSq)
                        {
                            bestDistSq = d;
                            bestIndex = i + lane;
                        }
                    }

                    vMinDistSq = Vector256.Create(bestDistSq);
                }
            }
        }

        for (; i < count; i++)
        {
            var dx = Unsafe.Add(ref xRef, i) - cellX;
            var dy = Unsafe.Add(ref yRef, i) - cellY;
            var distSq = (dx * dx) + (dy * dy);
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                bestIndex = i;
            }
        }
    }
}
