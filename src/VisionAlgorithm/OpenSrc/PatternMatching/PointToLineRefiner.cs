namespace VisionAlgorithm.OpenSrc.PatternMatching;

public static class PointToLineRefiner
{
    private readonly record struct Correspondence(ModelFeature Feature, EdgePoint Edge);
    internal readonly record struct PoseJacobian(double X, double Y, double Angle, double LogScale)
    {
        public double this[int index] => index switch { 0 => X, 1 => Y, 2 => Angle, 3 => LogScale, _ => throw new ArgumentOutOfRangeException(nameof(index)) };
    }

    public static ShapeMatch Refine(PreparedSearchImage image, ShapeModel model, ShapeMatch match,
        SearchOptions options, CancellationToken cancellationToken = default)
    {
        var initial = match.Pose;
        var pose = initial;
        var status = RefinementStatus.IterationLimit;
        var initialResidual = double.NaN;
        var finalResidual = double.NaN;
        var count = 0;
        var iterations = 0;
        var observable = true;
        var active = new List<int> { 0, 1 };
        if (model.Options.MaximumAngleDegrees > model.Options.MinimumAngleDegrees)
        {
            active.Add(2);
        }
        if (model.Options.MaximumScale > model.Options.MinimumScale)
        {
            active.Add(3);
        }
        var dimensions = active.Count;
        for (var iteration = 0; iteration < 20; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            iterations = iteration + 1;
            var pairs = Correspond(image.Gradients[0], model, pose);
            count = pairs.Count;
            if (count < Math.Max(12, model.Levels[0].Features.Length / 4))
            {
                status = RefinementStatus.InsufficientSupport;
                break;
            }
            var normal = new double[dimensions, dimensions];
            var right = new double[dimensions];
            var residual = 0d;
            foreach (var pair in pairs)
            {
                var (r, jacobian) = ResidualAndJacobian(pair.Feature, pair.Edge, pose);
                var weight = Math.Abs(r) <= 1 ? 1 : 1 / Math.Abs(r);
                residual += r * r;
                for (var row = 0; row < dimensions; row++)
                {
                    right[row] -= weight * jacobian[active[row]] * r;
                    for (var column = 0; column < dimensions; column++)
                    {
                        normal[row, column] += weight * jacobian[active[row]] * jacobian[active[column]];
                    }
                }
            }
            var rms = Math.Sqrt(residual / count);
            if (iteration == 0)
            {
                initialResidual = rms;
            }
            finalResidual = rms;
            var radiusSquared = model.Levels[0].Features.Average(f => (f.X * f.X) + (f.Y * f.Y)) * pose.Scale * pose.Scale;
            var angularEnergy = pairs.Sum(p =>
            {
                var (_, j) = ResidualAndJacobian(p.Feature, p.Edge, pose);
                return j.Angle * j.Angle;
            });
            observable = angularEnergy > 0.002 * radiusSquared * count;
            if ((!observable && active.Contains(2)) || !TrySolve(normal, right, out var solved))
            {
                status = RefinementStatus.IllConditioned;
                break;
            }
            var delta = new double[4];
            for (var i = 0; i < dimensions; i++)
            {
                delta[active[i]] = solved[i];
            }
            if (Math.Abs(delta[0]) > 3 || Math.Abs(delta[1]) > 3 || Math.Abs(delta[2]) > Math.PI / 18 ||
                Math.Abs(delta[3]) > 0.1)
            {
                status = RefinementStatus.Rejected;
                break;
            }
            var accepted = false;
            for (var trial = 0; trial < 8; trial++)
            {
                var fraction = 1d / (1 << trial);
                var proposal = new MatchPose(pose.X + (delta[0] * fraction), pose.Y + (delta[1] * fraction),
                    pose.Angle + (delta[2] * fraction), pose.Scale * Math.Exp(delta[3] * fraction));
                if (!ContinuousVerifier.Allowed(proposal, model, options))
                {
                    continue;
                }
                if (RobustCost(pairs, proposal) <= RobustCost(pairs, pose) + 1e-10)
                {
                    pose = proposal;
                    accepted = true;
                    break;
                }
            }
            if (!accepted)
            {
                status = RefinementStatus.Rejected;
                break;
            }
            if (Math.Abs(delta[0]) + Math.Abs(delta[1]) < 0.002 && Math.Abs(delta[2]) < 0.00005 &&
                Math.Abs(delta[3]) < 0.00005)
            {
                status = RefinementStatus.Converged;
                break;
            }
        }
        var score = ContinuousVerifier.Score(image.Gradients[0], model, pose);
        var finalPairs = Correspond(image.Gradients[0], model, pose);
        finalResidual = ResidualRms(finalPairs, pose);
        if (status is RefinementStatus.Converged or RefinementStatus.IterationLimit &&
            (score.Score < options.MinimumScore || score.Support < options.MinimumSupport ||
            finalResidual > initialResidual + 0.01 || Math.Abs(pose.X - initial.X) > 6 || Math.Abs(pose.Y - initial.Y) > 6))
        {
            status = RefinementStatus.Rejected;
        }
        if (status is RefinementStatus.InsufficientSupport or RefinementStatus.IllConditioned or RefinementStatus.Rejected)
        {
            pose = initial;
            score = new(match.DirectionScore, match.Support, match.SupportedQuadrants);
            finalPairs = Correspond(image.Gradients[0], model, pose);
            finalResidual = ResidualRms(finalPairs, pose);
        }
        return match with
        {
            // 공개 정밀화 API는 새 영상이나 자세에 이전 품질값을 재사용하지 않는다.
            ClutterRatio = null,
            ClutterPenaltyRatio = null,
            Pose = pose,
            Score = score.Score,
            DirectionScore = score.Score,
            Support = score.Support,
            SupportedQuadrants = score.Quadrants,
            Refinement = new(status, initial, initialResidual, finalResidual, iterations, finalPairs.Count, observable)
        };
    }

    internal static (double Residual, PoseJacobian Jacobian) ResidualAndJacobian(ModelFeature feature, EdgePoint edge, MatchPose pose)
    {
        var cos = Math.Cos(pose.Angle);
        var sin = Math.Sin(pose.Angle);
        var x = pose.Scale * ((cos * feature.X) - (sin * feature.Y));
        var y = pose.Scale * ((sin * feature.X) + (cos * feature.Y));
        var residual = (edge.NormalX * (pose.X + x - edge.X)) + (edge.NormalY * (pose.Y + y - edge.Y));
        return (residual, new(edge.NormalX, edge.NormalY, (-edge.NormalX * y) + (edge.NormalY * x),
            (edge.NormalX * x) + (edge.NormalY * y)));
    }

    private static List<Correspondence> Correspond(GradientImage image, ShapeModel model, MatchPose pose)
    {
        var pairs = new List<Correspondence>(model.Levels[0].Features.Length);
        var cos = Math.Cos(pose.Angle);
        var sin = Math.Sin(pose.Angle);
        foreach (var feature in model.Levels[0].Features)
        {
            var x = pose.X + (pose.Scale * ((cos * feature.X) - (sin * feature.Y)));
            var y = pose.Y + (pose.Scale * ((sin * feature.X) + (cos * feature.Y)));
            var nx = (cos * feature.NormalX) - (sin * feature.NormalY);
            var ny = (sin * feature.NormalX) + (cos * feature.NormalY);
            var bestDistance = 9d;
            EdgePoint? best = null;
            for (var iy = Math.Max(0, (int)y - 3); iy <= Math.Min(image.Height - 1, (int)y + 3); iy++)
            {
                for (var ix = Math.Max(0, (int)x - 3); ix <= Math.Min(image.Width - 1, (int)x + 3); ix++)
                {
                    if (image.EdgeAt(ix, iy) is not { } edge)
                    {
                        continue;
                    }
                    var dot = (nx * edge.NormalX) + (ny * edge.NormalY);
                    if (model.Options.Polarity == ContrastPolarity.IgnoreLocal)
                    {
                        dot = Math.Abs(dot);
                    }
                    var distance = ((edge.X - x) * (edge.X - x)) + ((edge.Y - y) * (edge.Y - y));
                    if (dot >= 0.8 && distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = edge;
                    }
                }
            }
            if (best is { } found)
            {
                pairs.Add(new(feature, found));
            }
        }
        return pairs;
    }

    private static double RobustCost(List<Correspondence> pairs, MatchPose pose)
    {
        var sum = 0d;
        foreach (var pair in pairs)
        {
            var (r, _) = ResidualAndJacobian(pair.Feature, pair.Edge, pose);
            sum += Math.Abs(r) <= 1 ? r * r / 2 : Math.Abs(r) - 0.5;
        }
        return sum;
    }

    private static double ResidualRms(List<Correspondence> pairs, MatchPose pose)
        => pairs.Count > 0 ? Math.Sqrt(pairs.Average(pair =>
        {
            var (residual, _) = ResidualAndJacobian(pair.Feature, pair.Edge, pose);
            return residual * residual;
        })) : double.NaN;

    private static bool TrySolve(double[,] matrix, double[] right, out double[] solution)
    {
        var n = right.Length;
        solution = new double[n];
        var scales = new double[n];
        for (var i = 0; i < n; i++)
        {
            scales[i] = Math.Sqrt(matrix[i, i]);
            if (scales[i] < 1e-8)
            {
                return false;
            }
        }
        for (var i = 0; i < n; i++)
        {
            right[i] /= scales[i];
            for (var j = 0; j < n; j++)
            {
                matrix[i, j] /= scales[i] * scales[j];
            }
        }
        for (var k = 0; k < n; k++)
        {
            var pivot = k;
            for (var i = k + 1; i < n; i++)
            {
                if (Math.Abs(matrix[i, k]) > Math.Abs(matrix[pivot, k]))
                {
                    pivot = i;
                }
            }
            if (Math.Abs(matrix[pivot, k]) < 1e-5)
            {
                return false;
            }
            for (var j = k; j < n; j++)
            {
                (matrix[k, j], matrix[pivot, j]) = (matrix[pivot, j], matrix[k, j]);
            }
            (right[k], right[pivot]) = (right[pivot], right[k]);
            for (var i = k + 1; i < n; i++)
            {
                var ratio = matrix[i, k] / matrix[k, k];
                for (var j = k; j < n; j++)
                {
                    matrix[i, j] -= ratio * matrix[k, j];
                }
                right[i] -= ratio * right[k];
            }
        }
        for (var i = n - 1; i >= 0; i--)
        {
            var sum = right[i];
            for (var j = i + 1; j < n; j++)
            {
                sum -= matrix[i, j] * solution[j];
            }
            solution[i] = sum / matrix[i, i];
        }
        for (var i = 0; i < n; i++)
        {
            solution[i] /= scales[i];
        }
        return solution.All(double.IsFinite);
    }
}
