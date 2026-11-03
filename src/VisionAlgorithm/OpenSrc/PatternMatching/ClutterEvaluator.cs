namespace VisionAlgorithm.OpenSrc.PatternMatching;

/// <summary>최종 자세에서 전체 학습 윤곽으로 설명되지 않는 장면 에지를 측정한다.</summary>
internal static class ClutterEvaluator
{
    public static ClutterMeasurement EvaluateClutter(GradientImage image, ShapeModel model, MatchPose pose,
        double tolerance, CancellationToken token)
    {
        var contour = model.ContourModel ?? throw new ArgumentException("추가 윤곽 점수에는 전체 윤곽과 마스크를 보관한 원본 모델이 필요합니다.", nameof(model));
        var relativeAngle = pose.Angle - model.TrainingReferenceAngle;
        var inverseCosine = Math.Cos(relativeAngle);
        var inverseSine = Math.Sin(relativeAngle);
        var sceneEdges = 0;
        var unexplained = 0;
        var totalEnergy = 0d;
        var unexplainedEnergy = 0d;
        // 정답 윤곽 대응의 중앙값으로 조명 gain을 보정한다. 추가 결함의 최대 강도를 조명 변화로 흡수하지 않는다.
        var energyStrengthScale = .5 / EstimateContrastGain(image, model, contour, pose, tolerance, token);
        var maxRadius = 0d;
        foreach (var corner in model.Bounds)
        {
            var r = Math.Sqrt((corner.X * corner.X) + (corner.Y * corner.Y));
            if (r > maxRadius)
            {
                maxRadius = r;
            }
        }
        var searchRadius = (maxRadius * pose.Scale) + 2;
        var minRow = Math.Max(0, (int)Math.Floor(pose.Y - searchRadius));
        var maxRow = Math.Min(image.Height - 1, (int)Math.Ceiling(pose.Y + searchRadius));
        var minCol = Math.Max(0, (int)Math.Floor(pose.X - searchRadius));
        var maxCol = Math.Min(image.Width - 1, (int)Math.Ceiling(pose.X + searchRadius));
        var features = model.Levels[0].Features;
        var partitions = !model.FeaturePartitions.IsDefault && model.FeaturePartitions.Length == features.Length ? model.FeaturePartitions : default;
        byte occludedPartitionMask = 0;
        Span<double> pMinX = stackalloc double[8];
        Span<double> pMaxX = stackalloc double[8];
        Span<double> pMinY = stackalloc double[8];
        Span<double> pMaxY = stackalloc double[8];
        if (!partitions.IsEmpty && model.FineDetail is null && model.Options.BuildClutterModel && model.Options.UseFineDetailValidation)
        {
            for (var p = 0; p < 8; p++)
            {
                pMinX[p] = double.PositiveInfinity;
                pMaxX[p] = double.NegativeInfinity;
                pMinY[p] = double.PositiveInfinity;
                pMaxY[p] = double.NegativeInfinity;
            }
            var cos = Math.Cos(pose.Angle);
            var sin = Math.Sin(pose.Angle);
            Span<double> pSum = stackalloc double[8];
            Span<int> pTotal = stackalloc int[8];
            for (var i = 0; i < features.Length; i++)
            {
                var f = features[i];
                var p = partitions[i];
                if (f.X < pMinX[p]) pMinX[p] = f.X;
                if (f.X > pMaxX[p]) pMaxX[p] = f.X;
                if (f.Y < pMinY[p]) pMinY[p] = f.Y;
                if (f.Y > pMaxY[p]) pMaxY[p] = f.Y;
                var sx = pose.X + (pose.Scale * ((cos * f.X) - (sin * f.Y))) - 0.5;
                var sy = pose.Y + (pose.Scale * ((sin * f.X) + (cos * f.Y))) - 0.5;
                var snx = (cos * f.NormalX) - (sin * f.NormalY);
                var sny = (sin * f.NormalX) + (cos * f.NormalY);
                image.SampleDirection(sx, sy, out var gx, out var gy);
                var dot = (snx * gx) + (sny * gy);
                var sc = model.Options.Polarity == ContrastPolarity.IgnoreLocal ? Math.Abs(dot) : Math.Max(0, dot);
                pSum[p] += sc;
                pTotal[p]++;
            }
            byte occMask = 0;
            var occBlocks = 0;
            var inlierBlocks = 0;
            var inlierSum = 0d;
            var inlierCount = 0;
            for (var p = 0; p < 8; p++)
            {
                if (pTotal[p] == 0)
                {
                    continue;
                }
                if (pSum[p] / pTotal[p] < 0.48)
                {
                    occMask |= (byte)(1 << p);
                    occBlocks++;
                }
                else
                {
                    inlierBlocks++;
                    inlierSum += pSum[p];
                    inlierCount += pTotal[p];
                }
            }
            if (occBlocks is >= 1 and <= 3 && inlierBlocks >= 5 && inlierCount >= features.Length * 0.60 &&
                (inlierSum / inlierCount) >= 0.84 &&
                ContinuousVerifier.IsContiguousPartitionMask(occMask, model.PartitionAdjacency))
            {
                occludedPartitionMask = occMask;
            }
        }
        var refCos = Math.Cos(model.TrainingReferenceAngle);
        var refSin = Math.Sin(model.TrainingReferenceAngle);
        for (var row = minRow; row <= maxRow; row++)
        {
            token.ThrowIfCancellationRequested();
            for (var column = minCol; column <= maxCol; column++)
            {
                if (image.EdgeAt(column, row) is not { } edge)
                {
                    continue;
                }
                var dx = (edge.X - pose.X) / pose.Scale;
                var dy = (edge.Y - pose.Y) / pose.Scale;
                var x = model.TrainingReferencePoint.X + (inverseCosine * dx) + (inverseSine * dy);
                var y = model.TrainingReferencePoint.Y - (inverseSine * dx) + (inverseCosine * dy);
                if (!contour.Contains(x, y))
                {
                    continue;
                }
                if (occludedPartitionMask != 0)
                {
                    var rx = x - model.TrainingReferencePoint.X;
                    var ry = y - model.TrainingReferencePoint.Y;
                    var mx = (refCos * rx) + (refSin * ry);
                    var my = (-refSin * rx) + (refCos * ry);
                    var inOccludedPartition = false;
                    for (var p = 0; p < 8; p++)
                    {
                        if ((occludedPartitionMask & (1 << p)) != 0 &&
                            mx >= pMinX[p] - 6.0 && mx <= pMaxX[p] + 6.0 &&
                            my >= pMinY[p] - 6.0 && my <= pMaxY[p] + 6.0)
                        {
                            inOccludedPartition = true;
                            break;
                        }
                    }
                    if (inOccludedPartition)
                    {
                        continue;
                    }
                }
                sceneEdges++;
                var energy = (double)edge.Strength * edge.Strength;
                totalEnergy += energy;
                var normalX = (inverseCosine * edge.NormalX) + (inverseSine * edge.NormalY);
                var normalY = (-inverseSine * edge.NormalX) + (inverseCosine * edge.NormalY);
                var explanation = contour.Explain(x, y, normalX, normalY, tolerance / pose.Scale,
                    model.Options.Polarity, edge.Strength * energyStrengthScale, token);
                if (!explanation.AnyContour)
                {
                    unexplained++;
                }
                if (!explanation.EnergyContour)
                {
                    unexplainedEnergy += energy;
                }
            }
        }
        return new(sceneEdges == 0 ? 0 : unexplained / (double)sceneEdges, sceneEdges,
            totalEnergy == 0 ? 0 : unexplainedEnergy / totalEnergy);
    }

    private static double EstimateContrastGain(GradientImage image, ShapeModel model, ClutterModel contour,
        MatchPose pose, double tolerance, CancellationToken token)
    {
        Span<double> ratios = stackalloc double[ClutterModel.MaximumContrastReferences];
        var count = 0;
        var relativeAngle = pose.Angle - model.TrainingReferenceAngle;
        var cosine = Math.Cos(relativeAngle);
        var sine = Math.Sin(relativeAngle);
        bool InsideTrainingMask(EdgePoint edge)
        {
            var dx = (edge.X - pose.X) / pose.Scale;
            var dy = (edge.Y - pose.Y) / pose.Scale;
            return contour.Contains(model.TrainingReferencePoint.X + (cosine * dx) + (sine * dy),
                model.TrainingReferencePoint.Y - (sine * dx) + (cosine * dy));
        }
        foreach (var reference in contour.ContrastReferences)
        {
            token.ThrowIfCancellationRequested();
            var dx = reference.X - model.TrainingReferencePoint.X;
            var dy = reference.Y - model.TrainingReferencePoint.Y;
            var x = pose.X + (pose.Scale * ((cosine * dx) - (sine * dy)));
            var y = pose.Y + (pose.Scale * ((sine * dx) + (cosine * dy)));
            if (x < 0 || y < 0 || x >= image.Width || y >= image.Height)
            {
                continue;
            }
            var normalX = (cosine * reference.NormalX) - (sine * reference.NormalY);
            var normalY = (sine * reference.NormalX) + (cosine * reference.NormalY);
            if (FindCorrespondence(image, x, y, normalX, normalY, tolerance, model.Options.Polarity, InsideTrainingMask) is { } edge)
            {
                ratios[count++] = edge.Strength / reference.Strength;
            }
        }
        if (count == 0)
        {
            return 1;
        }
        var observed = ratios[..count];
        observed.Sort();
        return count % 2 == 0 ? (observed[(count / 2) - 1] + observed[count / 2]) / 2 : observed[count / 2];
    }

    internal static EdgePoint? FindCorrespondence(GradientImage image, double x, double y, double normalX,
        double normalY, double radius, ContrastPolarity polarity, Func<EdgePoint, bool>? allowed = null)
    {
        var bestDistance = radius * radius;
        EdgePoint? best = null;
        // 에지는 픽셀 중심에서 최대 0.5 이동하므로 대응 후보의 원래 정수 픽셀까지 포함한다.
        var left = (int)Math.Max(0, Math.Floor(x - radius - 1));
        var right = (int)Math.Min(image.Width - 1, Math.Ceiling(x + radius));
        var top = (int)Math.Max(0, Math.Floor(y - radius - 1));
        var bottom = (int)Math.Min(image.Height - 1, Math.Ceiling(y + radius));
        for (var row = top; row <= bottom; row++)
        {
            for (var column = left; column <= right; column++)
            {
                if (image.EdgeAt(column, row) is not { } edge || !Compatible(normalX, normalY, edge, polarity) ||
                    (allowed is not null && !allowed(edge)))
                {
                    continue;
                }
                var dx = edge.X - x;
                var dy = edge.Y - y;
                var distance = (dx * dx) + (dy * dy);
                if (distance <= bestDistance)
                {
                    bestDistance = distance;
                    best = edge;
                }
            }
        }
        return best;
    }

    internal static bool Compatible(double normalX, double normalY, EdgePoint edge, ContrastPolarity polarity)
    {
        var dot = (normalX * edge.NormalX) + (normalY * edge.NormalY);
        return (polarity == ContrastPolarity.IgnoreLocal ? Math.Abs(dot) : dot) >= 0.8;
    }
}

internal readonly record struct ClutterMeasurement(double Ratio, int SceneEdges, double EnergyRatio);
