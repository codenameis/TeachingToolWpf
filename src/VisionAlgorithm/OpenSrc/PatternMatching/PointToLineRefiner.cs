using System.Runtime.CompilerServices;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

public static class PointToLineRefiner
{
    private readonly record struct Correspondence(ModelFeature Feature, EdgePoint Edge);
    internal readonly record struct PoseJacobian(double X, double Y, double Angle, double LogScale)
    {
        public double this[int index] => index switch { 0 => X, 1 => Y, 2 => Angle, 3 => LogScale, _ => throw new ArgumentOutOfRangeException(nameof(index)) };
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
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
        Span<int> active = stackalloc int[4];
        active[0] = 0;
        active[1] = 1;
        var dimensions = 2;
        var hasAngleDimension = model.Options.MaximumAngleDegrees > model.Options.MinimumAngleDegrees;
        if (hasAngleDimension)
        {
            active[dimensions++] = 2;
        }
        if (model.Options.MaximumScale > model.Options.MinimumScale)
        {
            active[dimensions++] = 3;
        }
        var features = model.Levels[0].Features;
        var meanFeatureRadiusSquared = 0d;
        for (var i = 0; i < features.Length; i++)
        {
            var f = features[i];
            meanFeatureRadiusSquared += (f.X * f.X) + (f.Y * f.Y);
        }
        meanFeatureRadiusSquared /= features.Length;

        Span<Correspondence> pairBuffer = stackalloc Correspondence[features.Length];
        Span<double> normal = stackalloc double[16];
        Span<double> right = stackalloc double[4];
        Span<double> solved = stackalloc double[4];
        Span<double> delta = stackalloc double[4];

        for (var iteration = 0; iteration < 20; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            iterations = iteration + 1;
            count = Correspond(image.Gradients[0], model, pose, pairBuffer);
            var pairs = pairBuffer.Slice(0, count);
            if (count < Math.Max(12, features.Length / 4))
            {
                status = RefinementStatus.InsufficientSupport;
                break;
            }
            normal.Clear();
            right.Clear();
            var residual = 0d;
            var angularEnergy = 0d;
            for (var index = 0; index < pairs.Length; index++)
            {
                var pair = pairs[index];
                var (r, jacobian) = ResidualAndJacobian(pair.Feature, pair.Edge, pose);
                var absR = Math.Abs(r);
                var weight = model.Options.UseFineDetailValidation && iteration > 0 && absR > 2.0 ? 0d : (absR <= 1 ? 1 : 1 / absR);
                if (weight <= 0)
                {
                    continue;
                }
                residual += r * r;
                angularEnergy += jacobian.Angle * jacobian.Angle;
                for (var row = 0; row < dimensions; row++)
                {
                    var activeRow = active[row];
                    var weightedRowJacobian = weight * jacobian[activeRow];
                    right[row] -= weightedRowJacobian * r;
                    for (var column = 0; column < dimensions; column++)
                    {
                        normal[(row * dimensions) + column] += weightedRowJacobian * jacobian[active[column]];
                    }
                }
            }
            var rms = Math.Sqrt(residual / count);
            if (iteration == 0)
            {
                initialResidual = rms;
            }
            finalResidual = rms;
            var radiusSquared = meanFeatureRadiusSquared * pose.Scale * pose.Scale;
            observable = angularEnergy > 0.002 * radiusSquared * count;
            if ((!observable && hasAngleDimension) || !TrySolve(normal, right, dimensions, solved))
            {
                status = RefinementStatus.IllConditioned;
                break;
            }
            delta.Clear();
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
            var currentRobustCost = RobustCost(pairs, pose);
            for (var trial = 0; trial < 8; trial++)
            {
                var fraction = 1d / (1 << trial);
                var proposal = new MatchPose(pose.X + (delta[0] * fraction), pose.Y + (delta[1] * fraction),
                    pose.Angle + (delta[2] * fraction), pose.Scale * Math.Exp(delta[3] * fraction));
                if (!ContinuousVerifier.Allowed(proposal, model, options))
                {
                    continue;
                }
                if (RobustCost(pairs, proposal) <= currentRobustCost + 1e-10)
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
        var finalCount = Correspond(image.Gradients[0], model, pose, pairBuffer);
        finalResidual = ResidualRms(pairBuffer.Slice(0, finalCount), pose);
        if (status is RefinementStatus.Converged or RefinementStatus.IterationLimit &&
            (score.Score < options.MinimumScore || score.Support < options.MinimumSupport ||
            finalResidual > initialResidual + 0.01 || (model.Options.BuildClutterModel && model.Options.UseFineDetailValidation && score.Score < match.Score - 0.01) || Math.Abs(pose.X - initial.X) > 6 || Math.Abs(pose.Y - initial.Y) > 6))
        {
            status = RefinementStatus.Rejected;
        }
        if (status is RefinementStatus.InsufficientSupport or RefinementStatus.IllConditioned or RefinementStatus.Rejected)
        {
            pose = initial;
            score = new(match.DirectionScore, match.Support, match.SupportedQuadrants);
            finalCount = Correspond(image.Gradients[0], model, pose, pairBuffer);
            finalResidual = ResidualRms(pairBuffer.Slice(0, finalCount), pose);
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
            Refinement = new(status, initial, initialResidual, finalResidual, iterations, finalCount, observable)
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

    private static int Correspond(GradientImage image, ShapeModel model, MatchPose pose, Span<Correspondence> destination)
    {
        var count = 0;
        var cos = Math.Cos(pose.Angle);
        var sin = Math.Sin(pose.Angle);
        var features = model.Levels[0].Features;
        var partitions = !model.FeaturePartitions.IsDefault && model.FeaturePartitions.Length == features.Length ? model.FeaturePartitions : default;
        byte excludedPartitionMask = 0;
        if (!partitions.IsEmpty && model.FineDetail is null && model.Options.BuildClutterModel && model.Options.UseFineDetailValidation)
        {
            Span<double> pSum = stackalloc double[8];
            Span<int> pTotal = stackalloc int[8];
            for (var i = 0; i < features.Length; i++)
            {
                var f = features[i];
                var sx = pose.X + (pose.Scale * ((cos * f.X) - (sin * f.Y))) - 0.5;
                var sy = pose.Y + (pose.Scale * ((sin * f.X) + (cos * f.Y))) - 0.5;
                var snx = (cos * f.NormalX) - (sin * f.NormalY);
                var sny = (sin * f.NormalX) + (cos * f.NormalY);
                image.SampleDirection(sx, sy, out var gx, out var gy);
                var dot = (snx * gx) + (sny * gy);
                var sc = model.Options.Polarity == ContrastPolarity.IgnoreLocal ? Math.Abs(dot) : Math.Max(0, dot);
                var p = partitions[i];
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
                excludedPartitionMask = occMask;
            }
        }
        for (var index = 0; index < features.Length; index++)
        {
            if (excludedPartitionMask != 0 && (excludedPartitionMask & (1 << partitions[index])) != 0)
            {
                continue;
            }
            var feature = features[index];
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
                destination[count++] = new(feature, found);
            }
        }
        return count;
    }

    private static double RobustCost(ReadOnlySpan<Correspondence> pairs, MatchPose pose)
    {
        var sum = 0d;
        for (var index = 0; index < pairs.Length; index++)
        {
            var pair = pairs[index];
            var (r, _) = ResidualAndJacobian(pair.Feature, pair.Edge, pose);
            sum += Math.Abs(r) <= 1 ? r * r / 2 : Math.Abs(r) - 0.5;
        }
        return sum;
    }

    private static double ResidualRms(ReadOnlySpan<Correspondence> pairs, MatchPose pose)
    {
        if (pairs.IsEmpty)
        {
            return double.NaN;
        }
        var sumSquared = 0d;
        for (var index = 0; index < pairs.Length; index++)
        {
            var pair = pairs[index];
            var (residual, _) = ResidualAndJacobian(pair.Feature, pair.Edge, pose);
            sumSquared += residual * residual;
        }
        return Math.Sqrt(sumSquared / pairs.Length);
    }

    private static bool TrySolve(Span<double> matrix, Span<double> right, int n, Span<double> solution)
    {
        Span<double> scales = stackalloc double[4];
        for (var i = 0; i < n; i++)
        {
            scales[i] = Math.Sqrt(matrix[(i * n) + i]);
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
                matrix[(i * n) + j] /= scales[i] * scales[j];
            }
        }
        for (var k = 0; k < n; k++)
        {
            var pivot = k;
            for (var i = k + 1; i < n; i++)
            {
                if (Math.Abs(matrix[(i * n) + k]) > Math.Abs(matrix[(pivot * n) + k]))
                {
                    pivot = i;
                }
            }
            if (Math.Abs(matrix[(pivot * n) + k]) < 1e-5)
            {
                return false;
            }
            for (var j = k; j < n; j++)
            {
                var rowK = (k * n) + j;
                var rowPivot = (pivot * n) + j;
                (matrix[rowK], matrix[rowPivot]) = (matrix[rowPivot], matrix[rowK]);
            }
            (right[k], right[pivot]) = (right[pivot], right[k]);
            for (var i = k + 1; i < n; i++)
            {
                var ratio = matrix[(i * n) + k] / matrix[(k * n) + k];
                for (var j = k; j < n; j++)
                {
                    matrix[(i * n) + j] -= ratio * matrix[(k * n) + j];
                }
                right[i] -= ratio * right[k];
            }
        }
        for (var i = n - 1; i >= 0; i--)
        {
            var sum = right[i];
            for (var j = i + 1; j < n; j++)
            {
                sum -= matrix[(i * n) + j] * solution[j];
            }
            solution[i] = sum / matrix[(i * n) + i];
        }
        for (var i = 0; i < n; i++)
        {
            solution[i] /= scales[i];
            if (!double.IsFinite(solution[i]))
            {
                return false;
            }
        }
        return true;
    }
}
