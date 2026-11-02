using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

/// <summary>Gaussian 평활화로 구별하기 어려운 작은 형상의 원본 Sobel 에너지.
/// 모델 마스크 안의 모든 유효 stencil을 보관하며 밝기 offset과 균일 gain에 불변이다.</summary>
internal sealed class FineDetailModel
{
    internal readonly record struct Sample(double X, double Y, double Gx, double Gy);
    private readonly ImmutableArray<Sample> samples;
    private readonly double energy;
    private readonly double referenceAngle;
    private readonly double _radius;
    private readonly bool _enableElasticPatches;
    private readonly int[] _patchIndex;
    private readonly double[] _patchEnergy;

    private FineDetailModel(ImmutableArray<Sample> samples, double energy, double referenceAngle)
    {
        this.samples = samples;
        this.energy = energy;
        this.referenceAngle = referenceAngle;
        _radius = samples.IsEmpty ? 1 : samples.Max(sample => Math.Sqrt((sample.X * sample.X) + (sample.Y * sample.Y)));
        _patchIndex = new int[samples.Length];
        _patchEnergy = new double[9];
        if (!samples.IsEmpty)
        {
            var minX = double.PositiveInfinity;
            var maxX = double.NegativeInfinity;
            var minY = double.PositiveInfinity;
            var maxY = double.NegativeInfinity;
            foreach (var sample in samples)
            {
                if (sample.X < minX) minX = sample.X;
                if (sample.X > maxX) maxX = sample.X;
                if (sample.Y < minY) minY = sample.Y;
                if (sample.Y > maxY) maxY = sample.Y;
            }
            var spanX = Math.Max(1.0, maxX - minX + 1e-6);
            var spanY = Math.Max(1.0, maxY - minY + 1e-6);
            _enableElasticPatches = Math.Max(spanX, spanY) <= 22.0 && spanX <= spanY * 1.35 && spanY <= spanX * 1.35;
            if (_enableElasticPatches)
            {
                for (var i = 0; i < samples.Length; i++)
                {
                    var sample = samples[i];
                    var bx = Math.Clamp((int)(((sample.X - minX) / spanX) * 3), 0, 2);
                    var by = Math.Clamp((int)(((sample.Y - minY) / spanY) * 3), 0, 2);
                    var patch = (by * 3) + bx;
                    _patchIndex[i] = patch;
                    _patchEnergy[patch] += (sample.Gx * sample.Gx) + (sample.Gy * sample.Gy);
                }
            }
        }
    }

    public long EstimatedBytes => samples.Length * 36L;

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
                var gradient = Gradient(image.Pixels.Span, image.Width, image.Height, image.Stride, x + .5, y + .5, 1, 0);
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

    public bool HasElasticPatches => _enableElasticPatches;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public double Score(GrayImage image, MatchPose pose, ContrastPolarity polarity)
        => ScoreCore(image, pose, polarity, allowElastic: true);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private double ScoreCore(GrayImage image, MatchPose pose, ContrastPolarity polarity, bool allowElastic)
    {
        var cos = Math.Cos(pose.Angle - referenceAngle) * pose.Scale;
        var sin = Math.Sin(pose.Angle - referenceAngle) * pose.Scale;
        var dot = 0d;
        var sceneEnergy = 0d;
        var pixels = image.Pixels.Span;
        var width = image.Width;
        var height = image.Height;
        var stride = image.Stride;
        foreach (var sample in samples)
        {
            var x = pose.X + (cos * sample.X) - (sin * sample.Y);
            var y = pose.Y + (sin * sample.X) + (cos * sample.Y);
            var gradient = Gradient(pixels, width, height, stride, x, y, cos, sin);
            var product = (sample.Gx * gradient.X) + (sample.Gy * gradient.Y);
            dot += polarity == ContrastPolarity.IgnoreLocal ? Math.Abs(product) : product;
            sceneEnergy += (gradient.X * gradient.X) + (gradient.Y * gradient.Y);
        }
        // 최소제곱으로 설명 가능한 장면 gradient 에너지 비율. 음의 gain은 Preserve에서 허용하지 않는다.
        var rigidScore = sceneEnergy < 1e-12 || dot <= 0 ? 0 : Math.Clamp(dot * dot / (energy * sceneEnergy), 0, 1);
        if (!allowElastic || !_enableElasticPatches || rigidScore >= 0.999 || rigidScore <= 0)
        {
            return rigidScore;
        }
        return ScoreElasticPatches(pixels, width, height, stride, pose, cos, sin, polarity, rigidScore);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private double ScoreElasticPatches(ReadOnlySpan<byte> pixels, int width, int height, int stride,
        MatchPose pose, double cos, double sin, ContrastPolarity polarity, double rigidScore)
    {
        Span<double> bestCorrelation = stackalloc double[9];
        var highPatches = 0;
        var veryHighPatches = 0;
        var matchedEnergy = 0d;
        for (var patch = 0; patch < 9; patch++)
        {
            var patchModelEnergy = _patchEnergy[patch];
            if (patchModelEnergy < energy * 0.02)
            {
                continue;
            }
            var bestValue = 0d;
            for (var iy = -3; iy <= 3; iy++)
            {
                var dy = iy * 0.5;
                for (var ix = -3; ix <= 3; ix++)
                {
                    var dx = ix * 0.5;
                    var dot = 0d;
                    var sceneEnergy = 0d;
                    for (var i = 0; i < samples.Length; i++)
                    {
                        if (_patchIndex[i] != patch)
                        {
                            continue;
                        }
                        var sample = samples[i];
                        var x = pose.X + dx + (cos * sample.X) - (sin * sample.Y);
                        var y = pose.Y + dy + (sin * sample.X) + (cos * sample.Y);
                        var gradient = Gradient(pixels, width, height, stride, x, y, cos, sin);
                        var product = (sample.Gx * gradient.X) + (sample.Gy * gradient.Y);
                        dot += polarity == ContrastPolarity.IgnoreLocal ? Math.Abs(product) : product;
                        sceneEnergy += (gradient.X * gradient.X) + (gradient.Y * gradient.Y);
                    }
                    if (sceneEnergy > 1e-12 && dot > 0)
                    {
                        var r = (dot / Math.Sqrt(patchModelEnergy * sceneEnergy)) *
                            (1.0 - (0.02 * Math.Sqrt((dx * dx) + (dy * dy))));
                        if (r > bestValue)
                        {
                            bestValue = r;
                        }
                    }
                }
            }
            bestCorrelation[patch] = bestValue;
            if (bestValue >= 0.64)
            {
                highPatches++;
                matchedEnergy += patchModelEnergy;
            }
            if (bestValue >= 0.82)
            {
                veryHighPatches++;
            }
            var remainingPatches = 8 - patch;
            if (highPatches + remainingPatches < 5 || veryHighPatches + remainingPatches < 2)
            {
                return rigidScore;
            }
        }
        if (highPatches < 5 || veryHighPatches < 2 || matchedEnergy < energy * 0.60)
        {
            return rigidScore;
        }
        var weightedSum = 0d;
        var energySum = 0d;
        var discounted = 0;
        for (var patch = 0; patch < 9; patch++)
        {
            var patchModelEnergy = _patchEnergy[patch];
            if (patchModelEnergy < 1e-12)
            {
                continue;
            }
            var value = bestCorrelation[patch];
            var discount = value < 0.78 && discounted < 3;
            if (discount)
            {
                discounted++;
            }
            var weight = discount ? patchModelEnergy * 0.10 : patchModelEnergy;
            weightedSum += weight * value;
            energySum += weight;
        }
        if (energySum < 1e-12)
        {
            return rigidScore;
        }
        var elasticR = Math.Clamp(weightedSum / energySum, 0, 1);
        if (elasticR < 0.72)
        {
            return rigidScore;
        }
        return Math.Max(rigidScore, Math.Sqrt(elasticR));
    }

    public (MatchPose Pose, double Score) Optimize(GrayImage image, ShapeModel model, MatchPose initial,
        SearchOptions options, CancellationToken token)
    {
        var best = OptimizeLocal(image, model, initial, options, token);
        if (best.Score >= 0.70 && model.Options.Polarity == ContrastPolarity.Preserve)
        {
            return best;
        }
        // 후보 응답은 180도 무부호다. 미세 획으로 양방향을 구별하기 전에 한 방향만 확정하지 않는다.
        foreach (var offset in new[] { -Math.PI, Math.PI })
        {
            var opposite = initial with
            {
                Angle = initial.Angle + offset
            };
            if (!ContinuousVerifier.Allowed(opposite, model, options))
            {
                continue;
            }
            var alternative = OptimizeLocal(image, model, opposite, options, token);
            if (alternative.Score > best.Score + 1e-10)
            {
                best = alternative;
            }
            break;
        }
        return best;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private (MatchPose Pose, double Score) OptimizeLocal(GrayImage image, ShapeModel model, MatchPose initial,
        SearchOptions options, CancellationToken token)
    {
        var pose = initial;
        var best = ScoreCore(image, pose, model.Options.Polarity, allowElastic: false);
        var scaleStep = Math.Min(.02, 1 / _radius);
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
                            _ => original with { Scale = original.Scale + (sign * step * scaleStep) }
                        };
                        if (Math.Abs(proposal.X - initial.X) > 3 || Math.Abs(proposal.Y - initial.Y) > 3 ||
                            !ContinuousVerifier.Allowed(proposal, model, options))
                        {
                            continue;
                        }
                        var value = ScoreCore(image, proposal, model.Options.Polarity, allowElastic: false);
                        if (value > best + 1e-10)
                        {
                            best = value;
                            pose = proposal;
                            changed = true;
                        }
                    }
                }
                if (!changed || (step == 1d && iteration == 0 && best < (_enableElasticPatches ? 0.12 : 0.25)))
                {
                    break;
                }
            }
            if (step == 1d && best < (_enableElasticPatches ? 0.18 : 0.42))
            {
                break;
            }
        }
        if (_enableElasticPatches)
        {
            best = ScoreCore(image, pose, model.Options.Polarity, allowElastic: true);
        }
        return (pose, best);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ImagePoint Gradient(ReadOnlySpan<byte> pixels, int width, int height, int stride, double x, double y, double cos, double sin)
    {
        var a = At(pixels, width, height, stride, x - cos + sin, y - sin - cos);
        var b = At(pixels, width, height, stride, x + sin, y - cos);
        var c = At(pixels, width, height, stride, x + cos + sin, y + sin - cos);
        var d = At(pixels, width, height, stride, x - cos, y - sin);
        var f = At(pixels, width, height, stride, x + cos, y + sin);
        var g = At(pixels, width, height, stride, x - cos - sin, y - sin + cos);
        var h = At(pixels, width, height, stride, x - sin, y + cos);
        var i = At(pixels, width, height, stride, x + cos - sin, y + sin + cos);
        return new(c + (2 * f) + i - a - (2 * d) - g, g + (2 * h) + i - a - (2 * b) - c);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double At(ReadOnlySpan<byte> pixels, int width, int height, int stride, double x, double y)
    {
        x -= .5;
        y -= .5;
        if (x < 0 || y < 0 || x > width - 1 || y > height - 1)
        {
            return 0;
        }
        var ix = (int)x;
        var iy = (int)y;
        var tx = x - ix;
        var ty = y - iy;
        var row = iy * stride;
        var nextRow = Math.Min(iy + 1, height - 1) * stride;
        var nextX = Math.Min(ix + 1, width - 1);
        return (pixels[row + ix] * (1 - tx) * (1 - ty)) + (pixels[row + nextX] * tx * (1 - ty)) +
            (pixels[nextRow + ix] * (1 - tx) * ty) + (pixels[nextRow + nextX] * tx * ty);
    }
}
