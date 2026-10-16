using CpuMatching.Core;

namespace CpuMatching.SubNcc;

/// <summary>축소 원판의 선택 픽셀 ZNCC → 원본 사각형 전체 내부 픽셀 정밀화.</summary>
public sealed class SubNccMatcher : ITemplateMatcher
{
    private readonly GrayImage _template;
    private readonly SubNccOptions _parameters;

    public SubNccMatcher(GrayImage template, SubNccOptions? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        _parameters = parameters ?? new SubNccOptions();
        _parameters.Validate();
        var values = template.Pixels.Span;
        var minimum = byte.MaxValue;
        var maximum = byte.MinValue;
        foreach (var value in values)
        {
            minimum = Math.Min(minimum, value);
            maximum = Math.Max(maximum, value);
        }
        if (maximum - minimum < _parameters.MinimumContrast)
        {
            throw new ArgumentException("Template has insufficient contrast.", nameof(template));
        }
        _template = template;
    }

    public SearchResult Find(GrayImage image, SearchOptions? options = null, CancellationToken cancellationToken = default,
        Func<double, double, bool>? acceptCenter = null, ICoarseScoreAccelerator? accelerator = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        return PyramidSearch.Find(_template, image, options ?? new SearchOptions(),
            (template, scene, cx, cy, fine) => new CorrelationLevel(template, scene, cx, cy, fine, _parameters), cancellationToken,
            acceptCenter: acceptCenter, accelerator: accelerator, refineCoarsePositions: true);
    }

    private sealed class CorrelationLevel : IScoreLevel
    {
        public bool SupportsParallelEvaluation => true;

        private readonly GrayImage _scene;
        private readonly CorrelationSample[] _samples;
        private readonly double _energy;

        public CorrelationLevel(GrayImage template, GrayImage scene, double cx, double cy, bool fine, SubNccOptions parameters)
        {
            _scene = scene;
            var points = SelectPixels(template, cx, cy, fine, parameters.CoarseSamples);
            var mean = points.Average(point => point.Value);
            _samples = points.Select(point => point with { Value = point.Value - mean }).ToArray();
            _energy = _samples.Sum(point => point.Value * point.Value);
        }

        // 축소 영상에서는 밝기와 경계 표본을 섞고, 원본에서는 내부 픽셀을 모두 쓴다.
        private static List<CorrelationSample> SelectPixels(GrayImage template, double cx, double cy, bool fine, int coarseSamples)
        {
            var radius = (Math.Min(template.Width, template.Height) - 3) / 2.0;
            var points = new List<CorrelationSample>();
            var gradients = Edges.Gradients(template);
            for (var y = 1; y < template.Height - 1; y++)
            {
                for (var x = 1; x < template.Width - 1; x++)
                {
                    var dx = x - cx;
                    var dy = y - cy;
                    // 원본 정밀 검증은 가로로 긴 문자 양끝도 사용한다. 회전 padding은 표본에 없다.
                    if (fine || (dx * dx) + (dy * dy) <= radius * radius)
                    {
                        points.Add(new CorrelationSample(dx, dy, template[x, y]));
                    }
                }
            }
            if (!fine && points.Count > coarseSamples)
            {
                // 경계만 고르면 상수 명암 표본이 될 수 있어 균일 표본도 함께 보존한다.
                var uniform = points.Where((_, index) => index % Math.Max(1, points.Count / (coarseSamples / 2)) == 0).Take(coarseSamples / 2);
                var edges = points.OrderByDescending(point =>
                {
                    var (gx, gy) = gradients[((int)(point.Y + cy) * template.Width) + (int)(point.X + cx)];
                    return (gx * gx) + (gy * gy);
                }).Take(coarseSamples / 2);
                points = uniform.Concat(edges).Distinct().ToList();
            }
            return points;
        }

        public IPoseScore AtPose(double angleDegrees, double scale)
        {
            var angle = angleDegrees * Math.PI / 180;
            var cos = Math.Cos(angle) * scale;
            var sin = Math.Sin(angle) * scale;
            return new CorrelationPose(_scene, _samples.Select(point => new CorrelationSample(
                (point.X * cos) - (point.Y * sin), (point.X * sin) + (point.Y * cos), point.Value)).ToArray(), _energy);
        }
    }

    private sealed class CorrelationPose(GrayImage image, CorrelationSample[] samples, double templateEnergy) : IPoseScore, IAcceleratedPoseScore
    {
        public void ScoreGrid(ICoarseScoreAccelerator accelerator, double[] output, CancellationToken token) =>
            accelerator.ScoreCorrelation(image, samples, templateEnergy, output, token);

        public double Score(double x, double y)
        {
            if (templateEnergy < 1)
            {
                return 0;
            }
            double sum = 0;
            double squares = 0;
            double correlation = 0;
            var pixels = image.Pixels.Span;
            var width = image.Width;
            foreach (var point in samples)
            {
                var px = x + point.X;
                var py = y + point.Y;
                if (px < 0 || py < 0 || px >= image.Width - 1 || py >= image.Height - 1)
                {
                    return 0;
                }
                // 위에서 영상 경계를 확인했으므로 매 표본의 중복 검사/Clamp를 피한다.
                // Bilinear의 연산 순서는 GrayImage.Sample 및 CUDA와 동일하다.
                var ix = (int)px;
                var iy = (int)py;
                var dx = px - ix;
                var dy = py - iy;
                var index = (iy * width) + ix;
                var value = ((1 - dy) * (((1 - dx) * pixels[index]) + (dx * pixels[index + 1]))) +
                    (dy * (((1 - dx) * pixels[index + width]) + (dx * pixels[index + width + 1])));
                sum += value;
                squares += value * value;
                correlation += point.Value * value;
            }
            // 평균 밝기를 빼면 조명 차이를 줄일 수 있다. 분모는 두 영상의 명암 에너지다.
            var energy = squares - (sum * sum / samples.Length);
            return energy < 1 ? 0 : Math.Clamp(correlation / Math.Sqrt(templateEnergy * energy), 0, 1);
        }
    }
}
