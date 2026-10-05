using System.Runtime.CompilerServices;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

internal readonly record struct VerifiedScore(double Score, double Support, int Quadrants);

internal static class ContinuousVerifier
{
    public static VerifiedScore Score(GradientImage image, ShapeModel model, MatchPose pose, int level = 0)
        => ScoreProposal(image, model, pose, level, double.NegativeInfinity, double.PositiveInfinity);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static VerifiedScore ScoreProposal(GradientImage image, ShapeModel model, MatchPose pose, int level,
        double minimumScore, double featureUpperBound)
    {
        var sum = 0d;
        var supported = 0;
        var quadrants = 0;
        var cos = Math.Cos(pose.Angle);
        var sin = Math.Sin(pose.Angle);
        var features = model.Levels[level].Features;
        var count = features.Length;
        for (var index = 0; index < count; index++)
        {
            var feature = features[index];
            var x = pose.X + (pose.Scale * ((cos * feature.X) - (sin * feature.Y))) - 0.5;
            var y = pose.Y + (pose.Scale * ((sin * feature.X) + (cos * feature.Y))) - 0.5;
            var nx = (cos * feature.NormalX) - (sin * feature.NormalY);
            var ny = (sin * feature.NormalX) + (cos * feature.NormalY);
            image.SampleDirection(x, y, out var gx, out var gy);
            // 벡터 보간 길이를 다시 1로 만들지 않는다. 약한/누락 에지와 경계 밖은 지지를 얻지 못한다.
            var dot = (nx * gx) + (ny * gy);
            var score = model.Options.Polarity == ContrastPolarity.IgnoreLocal ? Math.Abs(dot) : Math.Max(0, dot);
            sum += score;
            if (score >= 0.8)
            {
                supported++;
                quadrants |= 1 << ((feature.X >= 0 ? 1 : 0) + (feature.Y >= 0 ? 2 : 0));
            }
            if ((index & 7) == 7 && index + 1 < count &&
                ((sum + ((count - index - 1) * featureUpperBound) + (count * 1e-12)) / count) <= minimumScore)
            {
                return new(double.NegativeInfinity, 0, 0);
            }
        }
        return new(sum / count, supported / (double)count, System.Numerics.BitOperations.PopCount((uint)quadrants));
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static (MatchPose Pose, VerifiedScore Score) Optimize(GradientImage image, ShapeModel model,
        MatchPose initial, SearchOptions options, CancellationToken token, int level = 0)
    {
        var pose = initial;
        var best = Score(image, model, pose, level);
        var radius = model.Levels[level].Features.Max(feature => Math.Sqrt((feature.X * feature.X) + (feature.Y * feature.Y)));
        var featureUpperBound = model.Levels[level].Features.Max(feature => Math.Sqrt(
            (feature.NormalX * feature.NormalX) + (feature.NormalY * feature.NormalY))) * image.DirectionLengthUpperBound * 1.000000001;
        var rotation = Math.Min(model.Options.AngleStepDegrees * Math.PI / 360, 2 / (radius * pose.Scale));
        var scale = model.Options.MaximumScale > model.Options.MinimumScale ? Math.Min(model.Options.ScaleStep / 2, 2 / radius) : 0;
        for (var resolution = 0; resolution < 5; resolution++)
        {
            var translation = 2d / (1 << resolution);
            for (var iteration = 0; iteration < 5; iteration++)
            {
                token.ThrowIfCancellationRequested();
                var changed = false;
                for (var dimension = 0; dimension < 4; dimension++)
                {
                    // 고정 배율의 두 제안은 현재 자세와 같으므로 점수를 다시 계산할 필요가 없다.
                    if (dimension == 3 && scale == 0)
                    {
                        continue;
                    }
                    var original = pose;
                    for (var sign = -1; sign <= 1; sign += 2)
                    {
                        var proposal = dimension switch
                        {
                            0 => original with { X = original.X + (sign * translation) },
                            1 => original with { Y = original.Y + (sign * translation) },
                            2 => original with { Angle = original.Angle + (sign * rotation) },
                            _ => original with { Scale = original.Scale + (sign * scale) }
                        };
                        if (!Allowed(proposal, model, options))
                        {
                            continue;
                        }
                        var score = ScoreProposal(image, model, proposal, level, best.Score + 1e-10, featureUpperBound);
                        if (score.Score > best.Score + 1e-10)
                        {
                            pose = proposal;
                            best = score;
                            changed = true;
                        }
                    }
                }
                if (!changed)
                {
                    break;
                }
            }
            rotation /= 2;
            scale /= 2;
        }
        return (pose, best);
    }

    internal static bool Allowed(MatchPose pose, ShapeModel model, SearchOptions options)
        => pose.Scale >= model.Options.MinimumScale && pose.Scale <= model.Options.MaximumScale &&
        pose.Angle - model.TrainingReferenceAngle >= (model.Options.MinimumAngleDegrees * Math.PI / 180) - 1e-12 &&
        pose.Angle - model.TrainingReferenceAngle <= (model.Options.MaximumAngleDegrees * Math.PI / 180) + 1e-12;
}
