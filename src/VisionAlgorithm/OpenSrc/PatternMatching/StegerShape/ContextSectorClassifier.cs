namespace VisionAlgorithm.OpenSrc.PatternMatching;

/// <summary>
/// Classifies the 4 cardinal context sectors (-X, +X, -Y, +Y) around the detected symmetry core into
/// occupied tangential borders and verified free-space mirror sectors, measuring first-border radii.
/// </summary>
internal static class ContextSectorClassifier
{
    public static (ImagePoint SectorRadii, byte OccupiedMask, byte EmptyMask) ClassifySectors(
        GradientImage gradients,
        ModelFeature[] denseModel,
        List<ShapeEdgePoint> orderedSelection,
        byte[] flags,
        in SymmetryCoreDetector.DetectedSymmetryCore core,
        int imageWidth,
        int imageHeight,
        double referenceX,
        double referenceY,
        double cos,
        double sin)
    {
        var symCx = core.Center.X;
        var symCy = core.Center.Y;
        var coreDiameter = core.CoreDiameter;
        var tangentStep = Math.Max(1.0, coreDiameter * SymmetryHeuristicLimits.DefaultTangentStepCoreRatio);
        var radialStep = Math.Max(0.8, coreDiameter * SymmetryHeuristicLimits.DefaultRadialStepCoreRatio);
        var minTangentRadius = coreDiameter * SymmetryHeuristicLimits.DefaultMinTangentRadiusCoreRatio;
        var halfRoiX = Math.Max(minTangentRadius + radialStep, (0.5 * (core.TotalMaxX - core.TotalMinX)) + 1.5);
        var halfRoiY = Math.Max(minTangentRadius + radialStep, (0.5 * (core.TotalMaxY - core.TotalMinY)) + 1.5);

        var avgRx = coreDiameter * SymmetryHeuristicLimits.DefaultDefaultSectorRadiusCoreRatio;
        var avgRy = avgRx;
        byte occupiedMask = 0;
        byte emptyMask = 0;

        for (var axis = 0; axis < 2; axis++)
        {
            EvaluatePrimaryAxis(
                gradients,
                denseModel,
                orderedSelection,
                flags,
                axis,
                symCx,
                symCy,
                coreDiameter,
                tangentStep,
                radialStep,
                minTangentRadius,
                halfRoiX,
                halfRoiY,
                imageWidth,
                imageHeight,
                referenceX,
                referenceY,
                cos,
                sin,
                ref avgRx,
                ref avgRy,
                ref occupiedMask,
                ref emptyMask);
        }

        for (var checkAxis = 0; checkAxis < 2; checkAxis++)
        {
            ExtendOuterBoundaryScanIfNeeded(
                gradients,
                denseModel,
                checkAxis,
                symCx,
                symCy,
                coreDiameter,
                tangentStep,
                radialStep,
                minTangentRadius,
                halfRoiX,
                halfRoiY,
                imageWidth,
                imageHeight,
                referenceX,
                referenceY,
                cos,
                sin,
                ref avgRx,
                ref avgRy,
                ref occupiedMask,
                ref emptyMask);
        }

        if ((occupiedMask & 3) == 0 && (occupiedMask & 12) != 0)
        {
            avgRx = avgRy;
        }
        else if ((occupiedMask & 12) == 0 && (occupiedMask & 3) != 0)
        {
            avgRy = avgRx;
        }

        return (new ImagePoint(avgRx, avgRy), occupiedMask, emptyMask);
    }

    private static void EvaluatePrimaryAxis(
        GradientImage gradients,
        ModelFeature[] denseModel,
        List<ShapeEdgePoint> orderedSelection,
        byte[] flags,
        int axis,
        double symCx,
        double symCy,
        double coreDiameter,
        double tangentStep,
        double radialStep,
        double minTangentRadius,
        double halfRoiX,
        double halfRoiY,
        int imageWidth,
        int imageHeight,
        double referenceX,
        double referenceY,
        double cos,
        double sin,
        ref double avgRx,
        ref double avgRy,
        ref byte occupiedMask,
        ref byte emptyMask)
    {
        var dirNeg = axis * 2;
        var dirPos = dirNeg + 1;
        var isXDir = axis == 0;
        var expectedNx = isXDir ? cos : -sin;
        var expectedNy = isXDir ? sin : cos;
        var maxScanR = isXDir ? halfRoiX : halfRoiY;

        CountSelectedAsymmetricLineFeatures(
            orderedSelection,
            flags,
            isXDir,
            symCx,
            symCy,
            coreDiameter,
            minTangentRadius,
            referenceX,
            referenceY,
            cos,
            sin,
            out var negAsymCount,
            out var posAsymCount,
            out var negFirstLineR,
            out var posFirstLineR);

        CountDenseOrthogonalBorders(
            denseModel,
            isXDir,
            symCx,
            symCy,
            coreDiameter,
            minTangentRadius,
            out var denseNegCount,
            out var densePosCount,
            out var denseNegMinR,
            out var densePosMinR,
            out var hasLongNegBorder,
            out var hasLongPosBorder);

        if (hasLongNegBorder || (denseNegCount >= 3 && (densePosCount <= 1 || denseNegMinR + (0.45 * radialStep) < densePosMinR)))
        {
            negAsymCount = Math.Max(negAsymCount, denseNegCount);
            negFirstLineR = Math.Min(negFirstLineR, denseNegMinR);
            if (hasLongNegBorder)
            {
                posAsymCount = 0;
            }
        }
        else if (hasLongPosBorder || (densePosCount >= 3 && (denseNegCount <= 1 || densePosMinR + (0.45 * radialStep) < denseNegMinR)))
        {
            posAsymCount = Math.Max(posAsymCount, densePosCount);
            posFirstLineR = Math.Min(posFirstLineR, densePosMinR);
            if (hasLongPosBorder)
            {
                negAsymCount = 0;
            }
        }

        var negDominant = negAsymCount >= 2 && (negAsymCount > posAsymCount || negFirstLineR + (0.65 * radialStep) < posFirstLineR);
        var posDominant = posAsymCount >= 2 && (posAsymCount > negAsymCount || posFirstLineR + (0.65 * radialStep) < negFirstLineR);
        var foundNegR = negDominant ? negFirstLineR : double.PositiveInfinity;
        var foundPosR = posDominant ? posFirstLineR : double.PositiveInfinity;
        var anyNegHits = 0;
        var anyPosHits = 0;

        for (var r = minTangentRadius; r <= maxScanR; r += 0.5 * radialStep)
        {
            ProbeMirrorBandHits(
                gradients,
                isXDir,
                symCx,
                symCy,
                r,
                tangentStep,
                expectedNx,
                expectedNy,
                imageWidth,
                imageHeight,
                referenceX,
                referenceY,
                cos,
                sin,
                out var negHits,
                out var posHits);

            if (negHits > anyNegHits)
            {
                anyNegHits = negHits;
            }

            if (posHits > anyPosHits)
            {
                anyPosHits = posHits;
            }

            var minReqNegHits = negAsymCount > posAsymCount ? 3 : 4;
            var minReqPosHits = posAsymCount > negAsymCount ? 3 : 4;
            if (!double.IsFinite(foundNegR) && negHits >= minReqNegHits && posHits <= 1 &&
                (negAsymCount > posAsymCount || (negAsymCount == 0 && posAsymCount == 0) ||
                 (negAsymCount == posAsymCount && negFirstLineR + (0.65 * radialStep) < posFirstLineR)))
            {
                foundNegR = r;
            }

            if (!double.IsFinite(foundPosR) && posHits >= minReqPosHits && negHits <= 1 &&
                (posAsymCount > negAsymCount || (negAsymCount == 0 && posAsymCount == 0) ||
                 (posAsymCount == negAsymCount && posFirstLineR + (0.65 * radialStep) < negFirstLineR)))
            {
                foundPosR = r;
            }
        }

        if (double.IsFinite(foundNegR) && double.IsFinite(foundPosR) && Math.Abs(foundNegR - foundPosR) <= 0.8 * radialStep)
        {
            if (hasLongNegBorder || (denseNegCount >= 3 && (densePosCount <= 1 || denseNegMinR + (0.45 * radialStep) < densePosMinR)))
            {
                foundNegR = denseNegMinR;
                foundPosR = double.PositiveInfinity;
            }
            else if (hasLongPosBorder || (densePosCount >= 3 && (denseNegCount <= 1 || densePosMinR + (0.45 * radialStep) < denseNegMinR)))
            {
                foundPosR = densePosMinR;
                foundNegR = double.PositiveInfinity;
            }
        }

        if (double.IsFinite(foundNegR) && (!double.IsFinite(foundPosR) || foundNegR + (0.8 * radialStep) < foundPosR) &&
            (hasLongNegBorder || negAsymCount >= 3 || anyNegHits >= 4))
        {
            occupiedMask |= (byte)(1 << dirNeg);
            emptyMask |= (byte)(1 << dirPos);
            if (isXDir)
            {
                avgRx = foundNegR;
            }
            else
            {
                avgRy = foundNegR;
            }
        }
        else if (double.IsFinite(foundPosR) && (!double.IsFinite(foundNegR) || foundPosR + (0.8 * radialStep) < foundNegR) &&
                 (hasLongPosBorder || posAsymCount >= 3 || anyPosHits >= 4))
        {
            occupiedMask |= (byte)(1 << dirPos);
            emptyMask |= (byte)(1 << dirNeg);
            if (isXDir)
            {
                avgRx = foundPosR;
            }
            else
            {
                avgRy = foundPosR;
            }
        }
        else if (!double.IsFinite(foundNegR) && !double.IsFinite(foundPosR))
        {
            if (anyNegHits <= 1)
            {
                emptyMask |= (byte)(1 << dirNeg);
            }

            if (anyPosHits <= 1)
            {
                emptyMask |= (byte)(1 << dirPos);
            }
        }
    }

    private static void ExtendOuterBoundaryScanIfNeeded(
        GradientImage gradients,
        ModelFeature[] denseModel,
        int checkAxis,
        double symCx,
        double symCy,
        double coreDiameter,
        double tangentStep,
        double radialStep,
        double minTangentRadius,
        double halfRoiX,
        double halfRoiY,
        int imageWidth,
        int imageHeight,
        double referenceX,
        double referenceY,
        double cos,
        double sin,
        ref double avgRx,
        ref double avgRy,
        ref byte occupiedMask,
        ref byte emptyMask)
    {
        var isXDir = checkAxis == 0;
        var curR = isXDir ? avgRx : avgRy;

        CountDenseOrthogonalBorders(
            denseModel,
            isXDir,
            symCx,
            symCy,
            coreDiameter,
            minTangentRadius,
            out var axisNegCount,
            out var axisPosCount,
            out _,
            out _,
            out _,
            out _,
            out var axisNegOrthoSpan,
            out var axisPosOrthoSpan);

        var hasAxisDenseBorder =
            (axisNegCount >= 3 && axisNegOrthoSpan >= coreDiameter * SymmetryHeuristicLimits.DefaultLongBorderMinCoreRatio) ||
            (axisPosCount >= 3 && axisPosOrthoSpan >= coreDiameter * SymmetryHeuristicLimits.DefaultLongBorderMinCoreRatio);

        var isOccupied = (occupiedMask & (isXDir ? 3 : 12)) != 0;
        if (isOccupied && (hasAxisDenseBorder || curR >= coreDiameter * SymmetryHeuristicLimits.DefaultExtendedScanMinCoreRatio))
        {
            return;
        }

        var dirNeg = checkAxis * 2;
        var dirPos = dirNeg + 1;
        var expectedNx = isXDir ? cos : -sin;
        var expectedNy = isXDir ? sin : cos;
        var extMinR = coreDiameter * SymmetryHeuristicLimits.DefaultExtendedScanMinCoreRatio;
        var extMaxR = Math.Min(
            coreDiameter * SymmetryHeuristicLimits.DefaultExtendedScanMaxCoreRatio,
            (isXDir ? halfRoiX : halfRoiY) + (1.2 * radialStep));

        for (var r = extMinR; r <= extMaxR; r += 0.5 * radialStep)
        {
            ProbeMirrorBandHits(
                gradients,
                isXDir,
                symCx,
                symCy,
                r,
                tangentStep,
                expectedNx,
                expectedNy,
                imageWidth,
                imageHeight,
                referenceX,
                referenceY,
                cos,
                sin,
                out var negHits,
                out var posHits);

            if (negHits >= 4 && posHits <= 1)
            {
                occupiedMask &= (byte)~(1 << dirPos);
                occupiedMask |= (byte)(1 << dirNeg);
                emptyMask |= (byte)(1 << dirPos);
                emptyMask &= (byte)~(1 << dirNeg);
                if (isXDir)
                {
                    avgRx = r;
                }
                else
                {
                    avgRy = r;
                }

                break;
            }

            if (posHits >= 4 && negHits <= 1)
            {
                occupiedMask &= (byte)~(1 << dirNeg);
                occupiedMask |= (byte)(1 << dirPos);
                emptyMask |= (byte)(1 << dirNeg);
                emptyMask &= (byte)~(1 << dirPos);
                if (isXDir)
                {
                    avgRx = r;
                }
                else
                {
                    avgRy = r;
                }

                break;
            }
        }
    }

    private static void CountSelectedAsymmetricLineFeatures(
        List<ShapeEdgePoint> orderedSelection,
        byte[] flags,
        bool isXDir,
        double symCx,
        double symCy,
        double coreDiameter,
        double minTangentRadius,
        double referenceX,
        double referenceY,
        double cos,
        double sin,
        out int negAsymCount,
        out int posAsymCount,
        out double negFirstLineR,
        out double posFirstLineR)
    {
        negAsymCount = 0;
        posAsymCount = 0;
        negFirstLineR = double.PositiveInfinity;
        posFirstLineR = double.PositiveInfinity;
        var requiredBit = isXDir ? 1 : 2;
        var maxOrtho = coreDiameter * SymmetryHeuristicLimits.DefaultMinAxisOffsetRatio;
        var minCoord = minTangentRadius * SymmetryHeuristicLimits.DefaultAsymCoordGateScale;

        for (var s = 0; s < orderedSelection.Count; s++)
        {
            if ((flags[s] & requiredBit) == 0)
            {
                continue;
            }

            var e = orderedSelection[s];
            var rx = e.X - referenceX;
            var ry = e.Y - referenceY;
            var coord = isXDir ? ((cos * rx) + (sin * ry) - symCx) : ((-sin * rx) + (cos * ry) - symCy);
            var ortho = isXDir ? ((-sin * rx) + (cos * ry) - symCy) : ((cos * rx) + (sin * ry) - symCx);
            var nx = (cos * e.NormalX) + (sin * e.NormalY);
            var ny = (-sin * e.NormalX) + (cos * e.NormalY);
            var isOrthoLine = isXDir
                ? Math.Abs(nx) >= SymmetryHeuristicLimits.DefaultAxisAlignedBorderMinNormal
                : Math.Abs(ny) >= SymmetryHeuristicLimits.DefaultAxisAlignedBorderMinNormal;
            if (Math.Abs(ortho) <= maxOrtho && isOrthoLine)
            {
                if (coord < -minCoord)
                {
                    negAsymCount++;
                    if (-coord < negFirstLineR)
                    {
                        negFirstLineR = -coord;
                    }
                }
                else if (coord > minCoord)
                {
                    posAsymCount++;
                    if (coord < posFirstLineR)
                    {
                        posFirstLineR = coord;
                    }
                }
            }
        }
    }

    private static void CountDenseOrthogonalBorders(
        ModelFeature[] denseModel,
        bool isXDir,
        double symCx,
        double symCy,
        double coreDiameter,
        double minTangentRadius,
        out int denseNegCount,
        out int densePosCount,
        out double denseNegMinR,
        out double densePosMinR,
        out bool hasLongNegBorder,
        out bool hasLongPosBorder)
    {
        CountDenseOrthogonalBorders(
            denseModel,
            isXDir,
            symCx,
            symCy,
            coreDiameter,
            minTangentRadius,
            out denseNegCount,
            out densePosCount,
            out denseNegMinR,
            out densePosMinR,
            out hasLongNegBorder,
            out hasLongPosBorder,
            out _,
            out _);
    }

    private static void CountDenseOrthogonalBorders(
        ModelFeature[] denseModel,
        bool isXDir,
        double symCx,
        double symCy,
        double coreDiameter,
        double minTangentRadius,
        out int denseNegCount,
        out int densePosCount,
        out double denseNegMinR,
        out double densePosMinR,
        out bool hasLongNegBorder,
        out bool hasLongPosBorder,
        out double negOrthoSpan,
        out double posOrthoSpan)
    {
        denseNegCount = 0;
        densePosCount = 0;
        denseNegMinR = double.PositiveInfinity;
        densePosMinR = double.PositiveInfinity;
        var denseNegMinOrtho = double.PositiveInfinity;
        var denseNegMaxOrtho = double.NegativeInfinity;
        var densePosMinOrtho = double.PositiveInfinity;
        var densePosMaxOrtho = double.NegativeInfinity;
        var maxOrtho = coreDiameter * SymmetryHeuristicLimits.DefaultDenseOrthoMaxCoreRatio;

        for (var i = 0; i < denseModel.Length; i++)
        {
            var dm = denseModel[i];
            var coord = isXDir ? (dm.X - symCx) : (dm.Y - symCy);
            var ortho = isXDir ? (dm.Y - symCy) : (dm.X - symCx);
            var isOrthoLine = isXDir
                ? Math.Abs(dm.NormalX) >= SymmetryHeuristicLimits.DefaultAxisAlignedBorderMinNormal
                : Math.Abs(dm.NormalY) >= SymmetryHeuristicLimits.DefaultAxisAlignedBorderMinNormal;
            if (Math.Abs(ortho) <= maxOrtho && isOrthoLine)
            {
                if (coord < -minTangentRadius)
                {
                    denseNegCount++;
                    if (-coord < denseNegMinR)
                    {
                        denseNegMinR = -coord;
                    }

                    if (ortho < denseNegMinOrtho)
                    {
                        denseNegMinOrtho = ortho;
                    }

                    if (ortho > denseNegMaxOrtho)
                    {
                        denseNegMaxOrtho = ortho;
                    }
                }
                else if (coord > minTangentRadius)
                {
                    densePosCount++;
                    if (coord < densePosMinR)
                    {
                        densePosMinR = coord;
                    }

                    if (ortho < densePosMinOrtho)
                    {
                        densePosMinOrtho = ortho;
                    }

                    if (ortho > densePosMaxOrtho)
                    {
                        densePosMaxOrtho = ortho;
                    }
                }
            }
        }

        negOrthoSpan = denseNegCount >= 2 ? denseNegMaxOrtho - denseNegMinOrtho : 0.0;
        posOrthoSpan = densePosCount >= 2 ? densePosMaxOrtho - densePosMinOrtho : 0.0;
        var minLongSpan = coreDiameter * SymmetryHeuristicLimits.DefaultLongBorderMinCoreRatio;
        hasLongNegBorder = denseNegCount >= 3 && negOrthoSpan >= minLongSpan && negOrthoSpan > posOrthoSpan * SymmetryHeuristicLimits.DefaultLongBorderDominanceRatio;
        hasLongPosBorder = densePosCount >= 3 && posOrthoSpan >= minLongSpan && posOrthoSpan > negOrthoSpan * SymmetryHeuristicLimits.DefaultLongBorderDominanceRatio;
    }

    private static void ProbeMirrorBandHits(
        GradientImage gradients,
        bool isXDir,
        double symCx,
        double symCy,
        double radius,
        double tangentStep,
        double expectedNx,
        double expectedNy,
        int imageWidth,
        int imageHeight,
        double referenceX,
        double referenceY,
        double cos,
        double sin,
        out int negHits,
        out int posHits)
    {
        negHits = 0;
        posHits = 0;
        for (var tStep = -2; tStep <= 2; tStep++)
        {
            var tangentOffset = tStep * tangentStep;
            var hitNegT = false;
            var hitPosT = false;
            for (var drIdx = -1; drIdx <= 1; drIdx++)
            {
                var rSample = radius + (drIdx * 0.40);
                if (!hitNegT)
                {
                    var mxNeg = isXDir ? symCx - rSample : symCx + tangentOffset;
                    var myNeg = isXDir ? symCy + tangentOffset : symCy - rSample;
                    var sxNeg = referenceX + (cos * mxNeg) - (sin * myNeg) - 0.5;
                    var syNeg = referenceY + (sin * mxNeg) + (cos * myNeg) - 0.5;
                    if (sxNeg >= 1 && sxNeg < imageWidth - 2 && syNeg >= 1 && syNeg < imageHeight - 2)
                    {
                        gradients.SampleDirection(sxNeg, syNeg, out var gxNeg, out var gyNeg);
                        if (Math.Abs((expectedNx * gxNeg) + (expectedNy * gyNeg)) >= SymmetryHeuristicLimits.DefaultProbeGradientMinDot)
                        {
                            hitNegT = true;
                        }
                    }
                }

                if (!hitPosT)
                {
                    var mxPos = isXDir ? symCx + rSample : symCx + tangentOffset;
                    var myPos = isXDir ? symCy + tangentOffset : symCy + rSample;
                    var sxPos = referenceX + (cos * mxPos) - (sin * myPos) - 0.5;
                    var syPos = referenceY + (sin * mxPos) + (cos * myPos) - 0.5;
                    if (sxPos >= 1 && sxPos < imageWidth - 2 && syPos >= 1 && syPos < imageHeight - 2)
                    {
                        gradients.SampleDirection(sxPos, syPos, out var gxPos, out var gyPos);
                        if (Math.Abs((expectedNx * gxPos) + (expectedNy * gyPos)) >= SymmetryHeuristicLimits.DefaultProbeGradientMinDot)
                        {
                            hitPosT = true;
                        }
                    }
                }
            }

            if (hitNegT)
            {
                negHits++;
            }

            if (hitPosT)
            {
                posHits++;
            }
        }
    }
}
