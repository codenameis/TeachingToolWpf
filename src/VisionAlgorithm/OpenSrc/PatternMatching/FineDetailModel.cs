using System.Collections.Immutable;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

/// <summary>Gaussian 평활화로 구별하기 어려운 작은 형상의 원본 Sobel 에너지.
/// 모델 마스크 안의 모든 유효 stencil을 보관하며 밝기 offset과 균일 gain에 불변이다.</summary>
internal sealed class FineDetailModel(ImmutableArray<FineDetailModel.Sample> samples, double energy, double referenceAngle)
{
    internal readonly record struct Sample(double X, double Y, double Gx, double Gy);
    public long EstimatedBytes => samples.Length * 32L;

    public static FineDetailModel? Create(GrayImage image, TrainingRegion region, CancellationToken token)
    {
        var samples = ImmutableArray.CreateBuilder<Sample>();
        var energy = 0d;
        for (var y = 1; y < image.Height - 1; y++)
        {
            token.ThrowIfCancellationRequested();
            for (var x = 1; x < image.Width - 1; x++)
            {
                if (!InsideMask(x, y))
                {
                    continue;
                }
                // 이 경로는 최대 4096개 원본 픽셀의 작은 형상에만 사용한다. 큰 모델은 기존 검증을 유지한다.
                if (samples.Count == 4096)
                {
                    return null;
                }
                var gradient = Gradient(image, x + .5, y + .5, 1, 0);
                samples.Add(new(x + .5 - region.ReferencePoint.X, y + .5 - region.ReferencePoint.Y, gradient.X, gradient.Y));
                energy += (gradient.X * gradient.X) + (gradient.Y * gradient.Y);
            }
        }
        return samples.Count == 0 || energy < 1e-12 ? null : new(samples.ToImmutable(), energy, region.ReferenceAngle);

        bool InsideMask(int x, int y)
        {
            if (region.Mask.IsEmpty)
            {
                return true;
            }
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (region.Mask.Span[((y + dy) * image.Width) + x + dx] == 0)
                    {
                        return false;
                    }
                }
            }
            return true;
        }
    }

    public double Score(GrayImage image, MatchPose pose, ContrastPolarity polarity)
    {
        var cos = Math.Cos(pose.Angle - referenceAngle) * pose.Scale;
        var sin = Math.Sin(pose.Angle - referenceAngle) * pose.Scale;
        var dot = 0d;
        var sceneEnergy = 0d;
        foreach (var sample in samples)
        {
            var x = pose.X + (cos * sample.X) - (sin * sample.Y);
            var y = pose.Y + (sin * sample.X) + (cos * sample.Y);
            var gradient = Gradient(image, x, y, cos, sin);
            var product = (sample.Gx * gradient.X) + (sample.Gy * gradient.Y);
            dot += polarity == ContrastPolarity.IgnoreLocal ? Math.Abs(product) : product;
            sceneEnergy += (gradient.X * gradient.X) + (gradient.Y * gradient.Y);
        }
        // 최소제곱으로 설명 가능한 장면 gradient 에너지 비율. 음의 gain은 Preserve에서 허용하지 않는다.
        return sceneEnergy < 1e-12 || dot <= 0 ? 0 : Math.Clamp(dot * dot / (energy * sceneEnergy), 0, 1);
    }

    public (MatchPose Pose, double Score) Optimize(GrayImage image, ShapeModel model, MatchPose initial,
        SearchOptions options, CancellationToken token)
    {
        var best = OptimizeLocal(image, model, initial, options, token);
        // 후보 응답은 180도 무부호다. 미세 획으로 양방향을 구별하기 전에 한 방향만 확정하지 않는다.
        foreach (var offset in new[] { -Math.PI, Math.PI })
        {
            var opposite = initial with { Angle = initial.Angle + offset };
            if (!ContinuousVerifier.Allowed(opposite, model, options))
            {
                continue;
            }
            var alternative = OptimizeLocal(image, model, opposite, options, token);
            if (alternative.Score > best.Score + 1e-10)
            {
                best = alternative;
            }
        }
        return best;
    }

    private (MatchPose Pose, double Score) OptimizeLocal(GrayImage image, ShapeModel model, MatchPose initial,
        SearchOptions options, CancellationToken token)
    {
        var pose = initial;
        var best = Score(image, pose, model.Options.Polarity);
        var radius = samples.Max(sample => Math.Sqrt((sample.X * sample.X) + (sample.Y * sample.Y)));
        for (var step = 1d; step >= .125; step /= 2)
        {
            for (var iteration = 0; iteration < 4; iteration++)
            {
                token.ThrowIfCancellationRequested();
                var changed = false;
                for (var dimension = 0; dimension < 4; dimension++)
                {
                    if (dimension == 3 && model.Options.MinimumScale == model.Options.MaximumScale)
                    {
                        continue;
                    }
                    var original = pose;
                    for (var sign = -1; sign <= 1; sign += 2)
                    {
                        var proposal = dimension switch
                        {
                            0 => original with { X = original.X + (sign * step) },
                            1 => original with { Y = original.Y + (sign * step) },
                            2 => original with { Angle = original.Angle + (sign * step * .02) },
                            _ => original with { Scale = original.Scale + (sign * step * Math.Min(.02, 1 / radius)) }
                        };
                        if (Math.Abs(proposal.X - initial.X) > 3 || Math.Abs(proposal.Y - initial.Y) > 3 ||
                            !ContinuousVerifier.Allowed(proposal, model, options))
                        {
                            continue;
                        }
                        var value = Score(image, proposal, model.Options.Polarity);
                        if (value > best + 1e-10)
                        {
                            best = value;
                            pose = proposal;
                            changed = true;
                        }
                    }
                }
                if (!changed)
                {
                    break;
                }
            }
        }
        return (pose, best);
    }

    private static ImagePoint Gradient(GrayImage image, double x, double y, double cos, double sin)
    {
        var a = At(image, x - cos + sin, y - sin - cos);
        var b = At(image, x + sin, y - cos);
        var c = At(image, x + cos + sin, y + sin - cos);
        var d = At(image, x - cos, y - sin);
        var f = At(image, x + cos, y + sin);
        var g = At(image, x - cos - sin, y - sin + cos);
        var h = At(image, x - sin, y + cos);
        var i = At(image, x + cos - sin, y + sin + cos);
        return new(c + (2 * f) + i - a - (2 * d) - g, g + (2 * h) + i - a - (2 * b) - c);
    }

    private static double At(GrayImage image, double x, double y)
    {
        x -= .5;
        y -= .5;
        var ix = (int)Math.Floor(x);
        var iy = (int)Math.Floor(y);
        if (x < 0 || y < 0 || x > image.Width - 1 || y > image.Height - 1)
        {
            return 0;
        }
        var tx = x - ix;
        var ty = y - iy;
        var row = iy * image.Stride;
        var nextRow = Math.Min(iy + 1, image.Height - 1) * image.Stride;
        var nextX = Math.Min(ix + 1, image.Width - 1);
        var pixels = image.Pixels.Span;
        return (pixels[row + ix] * (1 - tx) * (1 - ty)) + (pixels[row + nextX] * tx * (1 - ty)) +
            (pixels[nextRow + ix] * (1 - tx) * ty) + (pixels[nextRow + nextX] * tx * ty);
    }
}
