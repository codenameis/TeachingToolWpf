namespace VisionAlgorithm.OpenSrc.PatternMatching;

/// <summary>최종 자세의 선택 특징 대응과 전체 학습 윤곽으로 설명되지 않는 장면 에지를 별도로 측정한다.</summary>
internal static class GeometricQualityEvaluator
{
    public static GeometricMatchQuality Evaluate(GradientImage image, ShapeModel model, MatchPose pose,
        double tolerance, CancellationToken token)
    {
        var contour = model.GeometryModel ?? throw new ArgumentException("축소 후보 모델에는 원본 기하 품질 데이터가 없습니다.", nameof(model));
        var cosine = Math.Cos(pose.Angle);
        var sine = Math.Sin(pose.Angle);
        var relativeAngle = pose.Angle - model.TrainingReferenceAngle;
        var inverseCosine = Math.Cos(relativeAngle);
        var inverseSine = Math.Sin(relativeAngle);
        bool InsideTrainingMask(EdgePoint edge)
        {
            var dx = (edge.X - pose.X) / pose.Scale;
            var dy = (edge.Y - pose.Y) / pose.Scale;
            return contour.Contains(model.TrainingReferencePoint.X + (inverseCosine * dx) + (inverseSine * dy),
                model.TrainingReferencePoint.Y - (inverseSine * dx) + (inverseCosine * dy));
        }
        var matched = 0;
        var squaredError = 0d;
        foreach (var feature in model.Levels[0].Features)
        {
            token.ThrowIfCancellationRequested();
            var x = pose.X + (pose.Scale * ((cosine * feature.X) - (sine * feature.Y)));
            var y = pose.Y + (pose.Scale * ((sine * feature.X) + (cosine * feature.Y)));
            var normalX = (cosine * feature.NormalX) - (sine * feature.NormalY);
            var normalY = (sine * feature.NormalX) + (cosine * feature.NormalY);
            // 화면 밖 특징도 분모에는 남는다. 인접 영상 에지로 밖의 특징을 구제하지 않는다.
            if (x < 0 || y < 0 || x >= image.Width || y >= image.Height)
            {
                continue;
            }
            if (FindCorrespondence(image, x, y, normalX, normalY, tolerance, model.Options.Polarity, InsideTrainingMask) is { } edge)
            {
                var dx = edge.X - x;
                var dy = edge.Y - y;
                squaredError += (dx * dx) + (dy * dy);
                matched++;
            }
        }

        var clutter = EvaluateClutter(image, model, pose, tolerance, token);
        var count = model.Levels[0].Features.Length;
        return new(matched / (double)count, matched == 0 ? double.PositiveInfinity : Math.Sqrt(squaredError / matched),
            clutter.Ratio, matched, count, clutter.SceneEdges)
        {
            ClutterEnergyRatio = clutter.EnergyRatio
        };
    }

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
        foreach (var edge in image.EdgePoints)
        {
            token.ThrowIfCancellationRequested();
            var dx = (edge.X - pose.X) / pose.Scale;
            var dy = (edge.Y - pose.Y) / pose.Scale;
            var x = model.TrainingReferencePoint.X + (inverseCosine * dx) + (inverseSine * dy);
            var y = model.TrainingReferencePoint.Y - (inverseSine * dx) + (inverseCosine * dy);
            if (!contour.Contains(x, y))
            {
                continue;
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
        return new(sceneEdges == 0 ? 0 : unexplained / (double)sceneEdges, sceneEdges,
            totalEnergy == 0 ? 0 : unexplainedEnergy / totalEnergy);
    }

    private static double EstimateContrastGain(GradientImage image, ShapeModel model, GeometricQualityModel contour,
        MatchPose pose, double tolerance, CancellationToken token)
    {
        Span<double> ratios = stackalloc double[GeometricQualityModel.MaximumContrastReferences];
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
