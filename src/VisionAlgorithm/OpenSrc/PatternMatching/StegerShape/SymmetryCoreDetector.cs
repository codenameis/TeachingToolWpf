namespace VisionAlgorithm.OpenSrc.PatternMatching;

/// <summary>
/// Detects a dominant closed symmetric core (ring/square/pad) and verifies that non-radial exterior context edges exist
/// outside the core before classifying mirror/rotational asymmetry keys.
/// </summary>
internal static class SymmetryCoreDetector
{
    internal readonly record struct DetectedSymmetryCore(
        bool IsValid,
        ImagePoint Center,
        double CoreDiameter,
        double TotalMinX,
        double TotalMaxX,
        double TotalMinY,
        double TotalMaxY);

    public static DetectedSymmetryCore DetectCore(
        GradientImage gradients,
        ModelFeature[] denseModel,
        List<ComponentStatistics> components,
        int imageWidth,
        int imageHeight,
        double referenceX,
        double referenceY,
        double cos,
        double sin,
        SymmetryHeuristicLimits limits)
    {
        var count = denseModel.Length;
        var symCx = 0d;
        var symCy = 0d;
        var bestClosedSize = 0;
        var bestClosedSpan = 0d;

        var totalMinX = denseModel.Min(static m => m.X);
        var totalMaxX = denseModel.Max(static m => m.X);
        var totalMinY = denseModel.Min(static m => m.Y);
        var totalMaxY = denseModel.Max(static m => m.Y);
        var totalSpanX = Math.Max(1.0, totalMaxX - totalMinX);
        var totalSpanY = Math.Max(1.0, totalMaxY - totalMinY);
        var totalSpan = Math.Max(totalSpanX, totalSpanY);
        var minBoxSpan = Math.Min(totalSpanX, totalSpanY);

        foreach (var comp in components)
        {
            if (comp.Size < limits.ClosedComponentMinSize || comp.QuadrantMask != SymmetryHeuristicLimits.DefaultFullQuadrantMask)
            {
                continue;
            }

            var cx = comp.BoxCenterX;
            var cy = comp.BoxCenterY;
            var mx = comp.MeanX;
            var my = comp.MeanY;
            var spanX = comp.SpanX;
            var spanY = comp.SpanY;

            if (Math.Abs(mx - cx) <= spanX * limits.ClosedCentroidOffsetRatio &&
                Math.Abs(my - cy) <= spanY * limits.ClosedCentroidOffsetRatio &&
                spanX <= spanY * limits.ClosedMaxAspectRatio &&
                spanY <= spanX * limits.ClosedMaxAspectRatio &&
                comp.Size > bestClosedSize)
            {
                bestClosedSize = comp.Size;
                bestClosedSpan = Math.Max(spanX, spanY);
                symCx = cx;
                symCy = cy;
            }
        }

        RefineByOctantRadialBalance(
            denseModel,
            minBoxSpan,
            totalSpan,
            ref bestClosedSize,
            ref bestClosedSpan,
            ref symCx,
            ref symCy);

        PromoteConcentricOuterRingIfPresent(
            gradients,
            totalSpan,
            imageWidth,
            imageHeight,
            referenceX,
            referenceY,
            cos,
            sin,
            bestClosedSize,
            ref bestClosedSpan,
            ref symCx,
            ref symCy);

        if (!HasSufficientExteriorContext(denseModel, count, symCx, symCy, bestClosedSize, bestClosedSpan, minBoxSpan, totalSpan))
        {
            return default;
        }

        return new DetectedSymmetryCore(
            IsValid: true,
            Center: new ImagePoint(symCx, symCy),
            CoreDiameter: bestClosedSpan,
            TotalMinX: totalMinX,
            TotalMaxX: totalMaxX,
            TotalMinY: totalMinY,
            TotalMaxY: totalMaxY);
    }

    public static SymmetrySignature BuildAsymmetryAndContextSignature(
        GradientImage gradients,
        ModelFeature[] denseModel,
        List<ShapeEdgePoint> orderedSelection,
        in DetectedSymmetryCore core,
        int imageWidth,
        int imageHeight,
        double referenceX,
        double referenceY,
        double cos,
        double sin,
        SymmetryHeuristicLimits limits)
    {
        var symCx = core.Center.X;
        var symCy = core.Center.Y;
        var coreDiameter = core.CoreDiameter;
        var coreExcludeRadius = coreDiameter * limits.CoreExcludeRadiusRatio;
        var minAxisOffset = coreDiameter * limits.MinAxisOffsetRatio;
        var mirrorRatio = coreDiameter * SymmetryHeuristicLimits.DefaultMirrorToleranceCoreRatio;
        var rotRatio = coreDiameter * SymmetryHeuristicLimits.DefaultRotationToleranceCoreRatio;
        var pairTolSq = Math.Max(SymmetryHeuristicLimits.DefaultMinMirrorToleranceSquared, mirrorRatio * mirrorRatio);
        var rotTolSq = Math.Max(SymmetryHeuristicLimits.DefaultMinRotationToleranceSquared, rotRatio * rotRatio);
        var cornerMargin = coreDiameter * SymmetryHeuristicLimits.DefaultCornerMarginCoreRatio;

        var flags = new byte[orderedSelection.Count];
        var asymXCount = 0;
        var asymYCount = 0;
        var asymR90Count = 0;
        var asymR270Count = 0;

        for (var s = 0; s < orderedSelection.Count; s++)
        {
            var edge = orderedSelection[s];
            var rx = edge.X - referenceX;
            var ry = edge.Y - referenceY;
            var px = (cos * rx) + (sin * ry);
            var py = (-sin * rx) + (cos * ry);
            var nx = (cos * edge.NormalX) + (sin * edge.NormalY);
            var ny = (-sin * edge.NormalX) + (cos * edge.NormalY);
            var distX = Math.Abs(px - symCx);
            var distY = Math.Abs(py - symCy);
            var distR = Math.Sqrt((distX * distX) + (distY * distY));

            if (distR < coreExcludeRadius)
            {
                continue;
            }

            var counterparts = EvaluateCounterparts(
                denseModel,
                px,
                py,
                nx,
                ny,
                symCx,
                symCy,
                pairTolSq,
                rotTolSq,
                limits.CounterpartMinNormalDot);

            byte flag = 0;
            if (!counterparts.HasSymmetryX && distX >= minAxisOffset && Math.Abs(nx) >= limits.AxisKeyMinNormalComponent &&
                (distY < minAxisOffset - cornerMargin || distX >= distY - cornerMargin))
            {
                flag |= 1;
                asymXCount++;
            }

            if (!counterparts.HasSymmetryY && distY >= minAxisOffset && Math.Abs(ny) >= limits.AxisKeyMinNormalComponent &&
                (distX < minAxisOffset - cornerMargin || distY >= distX - cornerMargin))
            {
                flag |= 2;
                asymYCount++;
            }

            if (!counterparts.HasRotation90 && (flag & 3) != 0 && Math.Max(distX, distY) >= coreDiameter * limits.RotationKeyMinRadiusRatio)
            {
                flag |= 4;
                asymR90Count++;
            }

            if (!counterparts.HasRotation270 && (flag & 3) != 0 && Math.Max(distX, distY) >= coreDiameter * limits.RotationKeyMinRadiusRatio)
            {
                flag |= 8;
                asymR270Count++;
            }

            flags[s] = flag;
        }

        var maxKeyCount = (int)Math.Floor(orderedSelection.Count * limits.MaxKeySelectionFraction);
        var validX = asymXCount >= limits.MinAxisKeyCount && asymXCount <= maxKeyCount;
        var validY = asymYCount >= limits.MinAxisKeyCount && asymYCount <= maxKeyCount;
        var validR90 = asymR90Count >= limits.MinRotationKeyCount && asymR90Count <= maxKeyCount;
        var validR270 = asymR270Count >= limits.MinRotationKeyCount && asymR270Count <= maxKeyCount;
        if (validX ^ validY)
        {
            validR90 = false;
            validR270 = false;
        }

        if (!validX && !validY && !validR90 && !validR270)
        {
            return SymmetrySignature.Empty;
        }

        MaskInvalidKeyBits(flags, validX, validY, validR90, validR270);
        var (sectorRadii, occupiedMask, emptyMask) = ContextSectorClassifier.ClassifySectors(
            gradients,
            denseModel,
            orderedSelection,
            flags,
            in core,
            imageWidth,
            imageHeight,
            referenceX,
            referenceY,
            cos,
            sin);
        return new SymmetrySignature([.. flags], core.Center, core.CoreDiameter, sectorRadii, occupiedMask, emptyMask);
    }

    private static void RefineByOctantRadialBalance(
        ModelFeature[] denseModel,
        double minBoxSpan,
        double totalSpan,
        ref int bestClosedSize,
        ref double bestClosedSpan,
        ref double symCx,
        ref double symCy)
    {
        var count = denseModel.Length;
        var rMin = minBoxSpan * SymmetryHeuristicLimits.DefaultRadialBandMinRatio;
        var rMax = Math.Max(
            minBoxSpan * SymmetryHeuristicLimits.DefaultRadialBandMaxMinSpanRatio,
            totalSpan * SymmetryHeuristicLimits.DefaultRadialBandMaxTotalSpanRatio);
        var radialCount = 0;
        Span<double> octMaxR = stackalloc double[8];

        for (var i = 0; i < count; i++)
        {
            var dm = denseModel[i];
            var dr = Math.Sqrt((dm.X * dm.X) + (dm.Y * dm.Y));
            if (dr < rMin || dr > rMax)
            {
                continue;
            }

            var radialDot = Math.Abs(((dm.X * dm.NormalX) + (dm.Y * dm.NormalY)) / dr);
            if (radialDot < SymmetryHeuristicLimits.DefaultRadialNormalMinDot)
            {
                continue;
            }

            if (IsCollinearExteriorBorderPoint(denseModel, i, dr, minBoxSpan))
            {
                continue;
            }

            radialCount++;
            var oct = ((int)Math.Floor(((Math.Atan2(dm.Y, dm.X) + Math.PI) / (2 * Math.PI)) * 8.0)) & 7;
            if (dr > octMaxR[oct])
            {
                octMaxR[oct] = dr;
            }
        }

        Span<double> validOcts = stackalloc double[8];
        var nonZeroOcts = 0;
        for (var o = 0; o < 8; o++)
        {
            if (octMaxR[o] > 0)
            {
                validOcts[nonZeroOcts++] = octMaxR[o];
            }
        }

        if (radialCount < SymmetryHeuristicLimits.DefaultRadialCoreMinPoints ||
            nonZeroOcts < SymmetryHeuristicLimits.DefaultRadialCoreMinOctants)
        {
            return;
        }

        var pairSumX = 0d;
        var pairSumY = 0d;
        var pairCount = 0;
        for (var o = 0; o < 4; o++)
        {
            if (octMaxR[o] > 0 &&
                octMaxR[o + 4] > 0 &&
                Math.Abs(octMaxR[o] - octMaxR[o + 4]) <= Math.Min(octMaxR[o], octMaxR[o + 4]) * SymmetryHeuristicLimits.DefaultOppositeOctantBalanceRatio)
            {
                var ang = ((o + 0.5) * Math.PI / 4.0) - Math.PI;
                var diff = 0.5 * (octMaxR[o] - octMaxR[o + 4]);
                pairSumX += diff * Math.Cos(ang);
                pairSumY += diff * Math.Sin(ang);
                pairCount++;
            }
        }

        validOcts[..nonZeroOcts].Sort();
        var mid = nonZeroOcts / 2;
        var radialCoreDiameter = nonZeroOcts % 2 == 0
            ? validOcts[mid - 1] + validOcts[mid]
            : 2.0 * validOcts[mid];
        var radialCx = pairCount >= SymmetryHeuristicLimits.DefaultMinBalancedOctantPairs &&
            Math.Abs(pairSumX / pairCount) <= radialCoreDiameter * SymmetryHeuristicLimits.DefaultMaxRadialCenterDriftRatio
            ? pairSumX / pairCount
            : 0;
        var radialCy = pairCount >= SymmetryHeuristicLimits.DefaultMinBalancedOctantPairs &&
            Math.Abs(pairSumY / pairCount) <= radialCoreDiameter * SymmetryHeuristicLimits.DefaultMaxRadialCenterDriftRatio
            ? pairSumY / pairCount
            : 0;

        if (bestClosedSize < SymmetryHeuristicLimits.DefaultClosedComponentMinSize ||
            bestClosedSpan < radialCoreDiameter * SymmetryHeuristicLimits.DefaultClosedToRadialMinSpanRatio ||
            bestClosedSpan > radialCoreDiameter * SymmetryHeuristicLimits.DefaultClosedToRadialMaxSpanRatio ||
            Math.Abs(symCx - radialCx) > radialCoreDiameter * SymmetryHeuristicLimits.DefaultClosedToRadialMaxCenterDeltaRatio ||
            Math.Abs(symCy - radialCy) > radialCoreDiameter * SymmetryHeuristicLimits.DefaultClosedToRadialMaxCenterDeltaRatio)
        {
            bestClosedSize = Math.Max(bestClosedSize, radialCount);
            bestClosedSpan = radialCoreDiameter;
            symCx = radialCx;
            symCy = radialCy;
        }
    }

    private static bool IsCollinearExteriorBorderPoint(ModelFeature[] denseModel, int index, double dr, double minBoxSpan)
    {
        var dm = denseModel[index];
        var gateSpan = minBoxSpan * SymmetryHeuristicLimits.DefaultRadialBandMaxMinSpanRatio;
        if (dr <= gateSpan)
        {
            return false;
        }

        var isVertical = Math.Abs(dm.X) >= gateSpan && Math.Abs(dm.NormalX) >= SymmetryHeuristicLimits.DefaultAxisNormalMinComponent;
        var isHorizontal = Math.Abs(dm.Y) >= gateSpan && Math.Abs(dm.NormalY) >= SymmetryHeuristicLimits.DefaultAxisNormalMinComponent;
        if (!isVertical && !isHorizontal)
        {
            return false;
        }

        var collinearLines = 0;
        for (var j = 0; j < denseModel.Length; j++)
        {
            if (index == j)
            {
                continue;
            }

            var dm2 = denseModel[j];
            if (Math.Abs(dm.NormalX) >= SymmetryHeuristicLimits.DefaultAxisNormalMinComponent &&
                Math.Abs(dm2.NormalX) >= SymmetryHeuristicLimits.DefaultAxisNormalMinComponent &&
                Math.Abs(dm2.X - dm.X) <= SymmetryHeuristicLimits.DefaultCollinearMaxCrossOffset &&
                Math.Abs(dm2.Y - dm.Y) >= SymmetryHeuristicLimits.DefaultCollinearMinAlongOffset &&
                Math.Abs(dm2.Y - dm.Y) <= SymmetryHeuristicLimits.DefaultCollinearMaxAlongOffset)
            {
                collinearLines++;
            }
            else if (Math.Abs(dm.NormalY) >= SymmetryHeuristicLimits.DefaultAxisNormalMinComponent &&
                     Math.Abs(dm2.NormalY) >= SymmetryHeuristicLimits.DefaultAxisNormalMinComponent &&
                     Math.Abs(dm2.Y - dm.Y) <= SymmetryHeuristicLimits.DefaultCollinearMaxCrossOffset &&
                     Math.Abs(dm2.X - dm.X) >= SymmetryHeuristicLimits.DefaultCollinearMinAlongOffset &&
                     Math.Abs(dm2.X - dm.X) <= SymmetryHeuristicLimits.DefaultCollinearMaxAlongOffset)
            {
                collinearLines++;
            }
        }

        return collinearLines >= SymmetryHeuristicLimits.DefaultCollinearMinNeighbors;
    }

    private static void PromoteConcentricOuterRingIfPresent(
        GradientImage gradients,
        double totalSpan,
        int imageWidth,
        int imageHeight,
        double referenceX,
        double referenceY,
        double cos,
        double sin,
        int bestClosedSize,
        ref double bestClosedSpan,
        ref double symCx,
        ref double symCy)
    {
        if (bestClosedSize < SymmetryHeuristicLimits.DefaultClosedComponentMinSize ||
            bestClosedSpan <= 0 ||
            bestClosedSpan * SymmetryHeuristicLimits.DefaultOuterRingGateSpanScale > totalSpan * SymmetryHeuristicLimits.DefaultOuterRingGateTotalRatio)
        {
            return;
        }

        Span<double> outerRingR = stackalloc double[4];
        var outerRingMinR = bestClosedSpan * SymmetryHeuristicLimits.DefaultOuterRingMinRadiusRatio;
        var outerRingMaxR = Math.Min(
            totalSpan * SymmetryHeuristicLimits.DefaultOuterRingTotalMaxRadiusRatio,
            bestClosedSpan * SymmetryHeuristicLimits.DefaultOuterRingMaxRadiusRatio);

        for (var d = 0; d < 4; d++)
        {
            var ang = (d * Math.PI / 2.0) - Math.PI;
            var dirX = Math.Cos(ang);
            var dirY = Math.Sin(ang);
            var expectedNx = (cos * dirX) - (sin * dirY);
            var expectedNy = (sin * dirX) + (cos * dirY);
            for (var r = outerRingMinR; r <= outerRingMaxR; r += SymmetryHeuristicLimits.DefaultOuterRingRadialStep)
            {
                var mx = symCx + (r * dirX);
                var my = symCy + (r * dirY);
                var sx = referenceX + (cos * mx) - (sin * my) - 0.5;
                var sy = referenceY + (sin * mx) + (cos * my) - 0.5;
                if (sx >= 1 && sx < imageWidth - 2 && sy >= 1 && sy < imageHeight - 2)
                {
                    gradients.SampleDirection(sx, sy, out var gx, out var gy);
                    if (Math.Abs((expectedNx * gx) + (expectedNy * gy)) >= SymmetryHeuristicLimits.DefaultOuterRingMinGradientDot)
                    {
                        outerRingR[d] = r;
                    }
                }
            }
        }

        Span<double> validOuter = stackalloc double[4];
        var outerOctCount = 0;
        for (var d = 0; d < 4; d++)
        {
            if (outerRingR[d] > 0)
            {
                validOuter[outerOctCount++] = outerRingR[d];
            }
        }

        if (outerOctCount < SymmetryHeuristicLimits.DefaultOuterRingMinQuadrants)
        {
            return;
        }

        validOuter[..outerOctCount].Sort();
        var oMid = outerOctCount / 2;
        var outerDiam = outerOctCount % 2 == 0
            ? validOuter[oMid - 1] + validOuter[oMid]
            : 2.0 * validOuter[oMid];
        if (outerDiam >= bestClosedSpan * SymmetryHeuristicLimits.DefaultOuterRingPromotionRatio)
        {
            bestClosedSpan = outerDiam;
            symCx = 0;
            symCy = 0;
        }
    }

    private static bool HasSufficientExteriorContext(
        ModelFeature[] denseModel,
        int count,
        double symCx,
        double symCy,
        int bestClosedSize,
        double bestClosedSpan,
        double minBoxSpan,
        double totalSpan)
    {
        var exteriorLinearEdges = 0;
        var nonCoreCount = 0;
        var nonRadialExteriorEdges = 0;
        var exteriorThreshold = bestClosedSpan * SymmetryHeuristicLimits.DefaultExteriorCoreRadiusRatio;

        for (var i = 0; i < count; i++)
        {
            var dm = denseModel[i];
            var dx = dm.X - symCx;
            var dy = dm.Y - symCy;
            var dr = Math.Sqrt((dx * dx) + (dy * dy));
            if (dr <= exteriorThreshold)
            {
                continue;
            }

            nonCoreCount++;
            var radialDot = Math.Abs(((dx * dm.NormalX) + (dy * dm.NormalY)) / Math.Max(1e-6, dr));
            if (radialDot <= SymmetryHeuristicLimits.DefaultNonRadialMaxDot)
            {
                nonRadialExteriorEdges++;
            }

            var isAxisAlignedBorder =
                (Math.Abs(dx) >= exteriorThreshold && Math.Abs(dm.NormalX) >= SymmetryHeuristicLimits.DefaultAxisAlignedBorderMinNormal) ||
                (Math.Abs(dy) >= exteriorThreshold && Math.Abs(dm.NormalY) >= SymmetryHeuristicLimits.DefaultAxisAlignedBorderMinNormal);
            if (radialDot <= SymmetryHeuristicLimits.DefaultNonRadialMaxDot || isAxisAlignedBorder)
            {
                exteriorLinearEdges++;
            }
        }

        return bestClosedSize >= SymmetryHeuristicLimits.DefaultClosedComponentMinSize &&
               bestClosedSpan >= minBoxSpan * SymmetryHeuristicLimits.DefaultMinCoreToMinBoxRatio &&
               totalSpan >= bestClosedSpan * SymmetryHeuristicLimits.DefaultMinTotalToCoreSpanRatio &&
               exteriorLinearEdges >= SymmetryHeuristicLimits.DefaultMinExteriorLinearEdges &&
               nonCoreCount >= count * SymmetryHeuristicLimits.DefaultMinNonCoreFraction &&
               (nonRadialExteriorEdges >= SymmetryHeuristicLimits.DefaultMinNonRadialExteriorEdges ||
                totalSpan > minBoxSpan * SymmetryHeuristicLimits.DefaultSquareAspectRatioGate);
    }

    private readonly record struct CounterpartMatchFlags(
        bool HasSymmetryX,
        bool HasSymmetryY,
        bool HasRotation90,
        bool HasRotation270);

    private static CounterpartMatchFlags EvaluateCounterparts(
        ModelFeature[] denseModel,
        double px,
        double py,
        double nx,
        double ny,
        double symCx,
        double symCy,
        double pairTolSq,
        double rotTolSq,
        double minNormalDot)
    {
        var mirrorXx = (2 * symCx) - px;
        var mirrorXy = py;
        var mirrorXnx = -nx;
        var mirrorXny = ny;
        var hasSymX = false;

        var mirrorYx = px;
        var mirrorYy = (2 * symCy) - py;
        var mirrorYnx = nx;
        var mirrorYny = -ny;
        var hasSymY = false;

        var rot90Px = symCx - (py - symCy);
        var rot90Py = symCy + (px - symCx);
        var rot90Nx = -ny;
        var rot90Ny = nx;
        var hasRot90 = false;

        var rot270Px = symCx + (py - symCy);
        var rot270Py = symCy - (px - symCx);
        var rot270Nx = ny;
        var rot270Ny = -nx;
        var hasRot270 = false;

        for (var i = 0; i < denseModel.Length; i++)
        {
            var dm = denseModel[i];
            if (!hasSymX)
            {
                var d2x = ((dm.X - mirrorXx) * (dm.X - mirrorXx)) + ((dm.Y - mirrorXy) * (dm.Y - mirrorXy));
                if (d2x <= pairTolSq && Math.Abs((dm.NormalX * mirrorXnx) + (dm.NormalY * mirrorXny)) >= minNormalDot)
                {
                    hasSymX = true;
                }
            }

            if (!hasSymY)
            {
                var d2y = ((dm.X - mirrorYx) * (dm.X - mirrorYx)) + ((dm.Y - mirrorYy) * (dm.Y - mirrorYy));
                if (d2y <= pairTolSq && Math.Abs((dm.NormalX * mirrorYnx) + (dm.NormalY * mirrorYny)) >= minNormalDot)
                {
                    hasSymY = true;
                }
            }

            if (!hasRot90)
            {
                var d2r90 = ((dm.X - rot90Px) * (dm.X - rot90Px)) + ((dm.Y - rot90Py) * (dm.Y - rot90Py));
                if (d2r90 <= rotTolSq && Math.Abs((dm.NormalX * rot90Nx) + (dm.NormalY * rot90Ny)) >= minNormalDot)
                {
                    hasRot90 = true;
                }
            }

            if (!hasRot270)
            {
                var d2r270 = ((dm.X - rot270Px) * (dm.X - rot270Px)) + ((dm.Y - rot270Py) * (dm.Y - rot270Py));
                if (d2r270 <= rotTolSq && Math.Abs((dm.NormalX * rot270Nx) + (dm.NormalY * rot270Ny)) >= minNormalDot)
                {
                    hasRot270 = true;
                }
            }

            if (hasSymX && hasSymY && hasRot90 && hasRot270)
            {
                break;
            }
        }

        return new CounterpartMatchFlags(hasSymX, hasSymY, hasRot90, hasRot270);
    }

    private static void MaskInvalidKeyBits(byte[] flags, bool validX, bool validY, bool validR90, bool validR270)
    {
        if (!validX)
        {
            for (var s = 0; s < flags.Length; s++)
            {
                flags[s] &= unchecked((byte)~1);
            }
        }

        if (!validY)
        {
            for (var s = 0; s < flags.Length; s++)
            {
                flags[s] &= unchecked((byte)~2);
            }
        }

        if (!validR90)
        {
            for (var s = 0; s < flags.Length; s++)
            {
                flags[s] &= unchecked((byte)~4);
            }
        }

        if (!validR270)
        {
            for (var s = 0; s < flags.Length; s++)
            {
                flags[s] &= unchecked((byte)~8);
            }
        }
    }
}
