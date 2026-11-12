// File: src/VisionAlgorithm/OpenSrc/PatternMatching/StegerShape/FineDetailModel.cs
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
    private readonly bool _enableUniformGridElasticity;
    private readonly int[] _patchIndex;
    private readonly double[] _patchEnergy;
    private readonly Sample[][] _patchSamples;

    private FineDetailModel(ImmutableArray<Sample> samples, double energy, double referenceAngle, bool isCoreDominantIsotropic)
    {
        this.samples = samples;
        this.energy = energy;
        this.referenceAngle = referenceAngle;
        _radius = samples.IsEmpty ? 1 : samples.Max(sample => Math.Sqrt((sample.X * sample.X) + (sample.Y * sample.Y)));
        _patchIndex = new int[samples.Length];
        _patchEnergy = new double[9];
        _patchSamples = new Sample[9][];
        for (var p = 0; p < 9; p++)
        {
            _patchSamples[p] = [];
        }
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
            // 코어 집중형 등방성 소형 모델은 외곽 여백 가변성을 허용하는 미세 패치 탄성 모드로,
            // 그 외 모든 FineDetail 대상 모델(최대 4,096 샘플)은 중간 크기 사각지대 없이 균일 3×3 격자 탄성(Uniform Grid Elasticity) 모드로 연속 커버한다.
            _enableElasticPatches = isCoreDominantIsotropic;
            _enableUniformGridElasticity = !_enableElasticPatches && Math.Max(spanX, spanY) <= 96.0 && Math.Min(spanX, spanY) >= 8.0;
            if (_enableElasticPatches || _enableUniformGridElasticity)
            {
                var counts = new int[9];
                for (var i = 0; i < samples.Length; i++)
                {
                    var sample = samples[i];
                    var bx = Math.Clamp((int)(((sample.X - minX) / spanX) * 3), 0, 2);
                    var by = Math.Clamp((int)(((sample.Y - minY) / spanY) * 3), 0, 2);
                    var patch = (by * 3) + bx;
                    _patchIndex[i] = patch;
                    _patchEnergy[patch] += (sample.Gx * sample.Gx) + (sample.Gy * sample.Gy);
                    counts[patch]++;
                }
                var cursors = new int[9];
                for (var p = 0; p < 9; p++)
                {
                    _patchSamples[p] = counts[p] == 0 ? [] : new Sample[counts[p]];
                }
                for (var i = 0; i < samples.Length; i++)
                {
                    var patch = _patchIndex[i];
                    _patchSamples[patch][cursors[patch]++] = samples[i];
                }
            }
        }
    }

    public long EstimatedBytes => samples.Length * 36L;

    public static FineDetailModel? Create(ShapeGrayImage image, TrainingRegion region, CancellationToken token)
        => Create(image, region, hasAsymmetricContext: false, isRotationallySymmetric: false, token);

    public static FineDetailModel? Create(ShapeGrayImage image, TrainingRegion region, bool hasAsymmetricContext, bool isRotationallySymmetric, CancellationToken token)
    {
        var samples = ImmutableArray.CreateBuilder<Sample>();
        var energy = 0d;
        var minX = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var minY = double.PositiveInfinity;
        var maxY = double.NegativeInfinity;
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
                var sx = x + .5 - region.ReferencePoint.X;
                var sy = y + .5 - region.ReferencePoint.Y;
                if (sx < minX) minX = sx;
                if (sx > maxX) maxX = sx;
                if (sy < minY) minY = sy;
                if (sy > maxY) maxY = sy;
                var gradient = Gradient(image.Pixels.Span, image.Width, image.Height, image.Stride, x + .5, y + .5, 1, 0);
                samples.Add(new(sx, sy, gradient.X, gradient.Y));
                energy += (gradient.X * gradient.X) + (gradient.Y * gradient.Y);
            }
        }
        if (samples.Count == 0 || energy < 1e-12)
        {
            return null;
        }
        // 등방성(정방형) 모델 중 회전 대칭(isRotationallySymmetric)이고 비대칭 문맥(hasAsymmetricContext)이 없는 순수 동심 코어 단독 ROI(또는 22px 이하 소형 등방성 모델)는
        // 외곽 1~2px 여백의 미세 배경 차이를 허용하는 탄성 패치 모드로 분기하고,
        // 회전 비대칭(사각 패드, 핀 등)이거나 비대칭 문맥이 존재하는 ROI는 중간 크기 사각지대 없이 3×3 균일 격자 문맥 검증 모드로 분기한다.
        var spanX = Math.Max(1.0, maxX - minX + 1e-6);
        var spanY = Math.Max(1.0, maxY - minY + 1e-6);
        var maxSpan = Math.Max(spanX, spanY);
        var isCoreDominantIsotropic = !hasAsymmetricContext && (isRotationallySymmetric || maxSpan <= 22.0) && spanX <= spanY * 1.45 && spanY <= spanX * 1.45;
        return new(samples.ToImmutable(), energy, region.ReferenceAngle, isCoreDominantIsotropic);

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
    public bool HasUniformGridElasticity => _enableUniformGridElasticity;
    public double Radius => _radius;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public double Score(ShapeGrayImage image, MatchPose pose, ContrastPolarity polarity)
        => ScoreCore(image, pose, polarity, allowElastic: true);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private double ScoreCore(ShapeGrayImage image, MatchPose pose, ContrastPolarity polarity, bool allowElastic)
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
        if (!allowElastic || (!_enableElasticPatches && !_enableUniformGridElasticity) || rigidScore >= 0.999 || rigidScore <= 0)
        {
            return rigidScore;
        }
        return _enableUniformGridElasticity
            ? ScoreUniformGridElasticity(pixels, width, height, stride, pose, cos, sin, polarity, rigidScore)
            : ScoreElasticPatches(pixels, width, height, stride, pose, cos, sin, polarity, rigidScore);
    }

    /// <summary>중심 코어 특별 취급 없이 균일 3×3 격자 패치별로 에지 존재 게이트와 Zero-Mean 변위 제약을 만족할 때만 ±1px 국소 정렬을 반영한다.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private double ScoreUniformGridElasticity(ReadOnlySpan<byte> pixels, int width, int height, int stride,
        MatchPose pose, double cos, double sin, ContrastPolarity polarity, double rigidScore)
    {
        var weightedSum = 0d;
        var energySum = 0d;
        var significantCells = 0;
        var matchedCells = 0;
        var meanShiftX = 0d;
        var meanShiftY = 0d;
        var sqShift = 0d;
        for (var patch = 0; patch < 9; patch++)
        {
            var patchModelEnergy = _patchEnergy[patch];
            if (patchModelEnergy < 1e-12)
            {
                continue;
            }
            var isSignificantCell = patchModelEnergy >= energy * 0.045;
            if (isSignificantCell)
            {
                significantCells++;
            }
            var pSamples = _patchSamples[patch];
            var bestScore = -1d;
            var bestDx = 0d;
            var bestDy = 0d;
            for (var iy = -2; iy <= 2; iy++)
            {
                var dy = iy * 0.5;
                for (var ix = -2; ix <= 2; ix++)
                {
                    var dx = ix * 0.5;
                    var dot = 0d;
                    var dotX = 0d;
                    var dotY = 0d;
                    var modelEx = 0d;
                    var modelEy = 0d;
                    var sceneEx = 0d;
                    var sceneEy = 0d;
                    var sceneEnergy = 0d;
                    for (var i = 0; i < pSamples.Length; i++)
                    {
                        var sample = pSamples[i];
                        var x = pose.X + dx + (cos * sample.X) - (sin * sample.Y);
                        var y = pose.Y + dy + (sin * sample.X) + (cos * sample.Y);
                        var gradient = Gradient(pixels, width, height, stride, x, y, cos, sin);
                        // 회전된 자세에서의 장면 그래디언트(gradient)는 이미 모델 좌표계로 회전되어 있으므로,
                        // 각 성분(Gx, Gy)별 에지 에너지 비율도 모델 셀의 성분별 에너지와 일치해야 한다(90도 회전 시 가로/세로 에지 혼동 방지).
                        var px = sample.Gx * gradient.X;
                        var py = sample.Gy * gradient.Y;
                        var product = px + py;
                        dot += polarity == ContrastPolarity.IgnoreLocal ? Math.Abs(product) : product;
                        dotX += polarity == ContrastPolarity.IgnoreLocal ? Math.Abs(px) : px;
                        dotY += polarity == ContrastPolarity.IgnoreLocal ? Math.Abs(py) : py;
                        modelEx += sample.Gx * sample.Gx;
                        modelEy += sample.Gy * sample.Gy;
                        sceneEx += gradient.X * gradient.X;
                        sceneEy += gradient.Y * gradient.Y;
                        sceneEnergy += (gradient.X * gradient.X) + (gradient.Y * gradient.Y);
                    }
                    // 에지 존재 게이트: 격자 셀의 전체 및 방향 성분별(Gx, Gy) 에지 에너지가 모델 셀과 일치하지 않으면(예: 90도 회전되어 가로 에지 위치에 세로 에지가 없거나 역전된 경우) 거부한다.
                    if (sceneEnergy < patchModelEnergy * 0.45 || sceneEnergy > patchModelEnergy * 2.2)
                    {
                        continue;
                    }
                    if (modelEx >= patchModelEnergy * 0.25 && (sceneEx < modelEx * 0.45 || dotX < Math.Sqrt(modelEx * sceneEx) * 0.65))
                    {
                        continue;
                    }
                    if (modelEy >= patchModelEnergy * 0.25 && (sceneEy < modelEy * 0.45 || dotY < Math.Sqrt(modelEy * sceneEy) * 0.65))
                    {
                        continue;
                    }
                    if (sceneEnergy > 1e-12 && dot > 0)
                    {
                        var r = (dot / Math.Sqrt(patchModelEnergy * sceneEnergy)) *
                            (1.0 - (0.015 * Math.Sqrt((dx * dx) + (dy * dy))));
                        if (r > bestScore)
                        {
                            bestScore = r;
                            bestDx = dx;
                            bestDy = dy;
                        }
                    }
                }
            }
            if (bestScore >= 0.68)
            {
                if (isSignificantCell)
                {
                    matchedCells++;
                }
                weightedSum += patchModelEnergy * bestScore;
                energySum += patchModelEnergy;
                meanShiftX += patchModelEnergy * bestDx;
                meanShiftY += patchModelEnergy * bestDy;
                sqShift += patchModelEnergy * ((bestDx * bestDx) + (bestDy * bestDy));
            }
            else if (isSignificantCell)
            {
                // 필수 문맥 셀(예: SMD 패드나 기판 테두리)이 하나라도 비어 있거나 방향 성분이 역전/불일치하면(예: 90도 회전 오검출) 강하게 감점한다.
                return rigidScore * 0.45;
            }
            var cellRigidScore = bestScore <= 0 ? 0.0 : bestScore;
            if (patchModelEnergy >= energy * 0.015 && cellRigidScore < 0.55)
            {
                // 코너 문맥 셀(예: 내부 배선/패드 코너)의 방향 상관이 무너진 경우에도 가짜 회전이므로 감점한다.
                return rigidScore * 0.45;
            }
        }
        if (energySum < 1e-12 || (significantCells > 0 && matchedCells < significantCells))
        {
            return rigidScore;
        }
        // Zero-Mean 및 변위 분산 제약:
        // 패치들이 서로 반대 방향으로 찢어져(예: 중심 코어는 -1px, 외곽 테두리는 +1px로 이동해 가짜 90도 회전 정합을 끼워 맞추는 경우)
        // 에너지 가중 변위 분산(shiftVar)이 0.28px^2를 초과하거나 강체 상관이 무너진 상태(rigidScore < 0.52)면 탄성 보정을 거부하고 감점한다.
        meanShiftX /= energySum;
        meanShiftY /= energySum;
        var shiftVar = Math.Max(0, (sqShift / energySum) - ((meanShiftX * meanShiftX) + (meanShiftY * meanShiftY)));
        if (shiftVar > 0.28 || rigidScore < 0.52)
        {
            return rigidScore * 0.45;
        }
        var netDrift = Math.Sqrt((meanShiftX * meanShiftX) + (meanShiftY * meanShiftY));
        var driftPenalty = netDrift <= 0.60 ? 1.0 : Math.Max(0.92, 1.0 - (0.08 * (netDrift - 0.60)));
        return Math.Max(rigidScore, Math.Clamp((weightedSum / energySum) * driftPenalty, 0, 1));
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
            var pSamples = _patchSamples[patch];
            var bestValue = 0d;
            for (var iy = -3; iy <= 3; iy++)
            {
                var dy = iy * 0.5;
                for (var ix = -3; ix <= 3; ix++)
                {
                    var dx = ix * 0.5;
                    var dot = 0d;
                    var sceneEnergy = 0d;
                    for (var i = 0; i < pSamples.Length; i++)
                    {
                        var sample = pSamples[i];
                        var x = pose.X + dx + (cos * sample.X) - (sin * sample.Y);
                        var y = pose.Y + dy + (sin * sample.X) + (cos * sample.Y);
                        var gradient = Gradient(pixels, width, height, stride, x, y, cos, sin);
                        // 회전된 자세에서의 장면 그래디언트(gradient)는 이미 모델 좌표계로 회전되어 있으므로,
                        // 각 성분(Gx, Gy)별 에지 에너지 비율도 모델 셀의 성분별 에너지와 일치해야 한다(90도 회전 시 가로/세로 에지 혼동 방지).
                        var product = (sample.Gx * gradient.X) + (sample.Gy * gradient.Y);
                        dot += polarity == ContrastPolarity.IgnoreLocal ? Math.Abs(product) : product;
                        sceneEnergy += (gradient.X * gradient.X) + (gradient.Y * gradient.Y);
                    }
                    if (sceneEnergy > 1e-12 && dot > 0)
                    {
                        var r = (dot / Math.Sqrt(patchModelEnergy * sceneEnergy)) *
                            (1.0 - (0.005 * ((dx * dx) + (dy * dy))));
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
        // 인위적 비선형 부스팅(Math.Sqrt) 없이 에너지 가중 정규화 상관계수(elasticR)를 반환하되,
        // 소형 정방형 모델에서 0.5px 서브픽셀 그리드 양자화로 인한 위상 감쇠만 선형 보정(최대 +6%)한다.
        var phaseCompensated = Math.Clamp(elasticR * 1.08, 0, 1);
        return Math.Max(rigidScore, phaseCompensated);
    }

    public (MatchPose Pose, double Score) Optimize(ShapeGrayImage image, ShapeModel model, MatchPose initial,
        ShapeSearchOptions options, CancellationToken token)
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
    private (MatchPose Pose, double Score) OptimizeLocal(ShapeGrayImage image, ShapeModel model, MatchPose initial,
        ShapeSearchOptions options, CancellationToken token)
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
        if (_enableElasticPatches || _enableUniformGridElasticity)
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
