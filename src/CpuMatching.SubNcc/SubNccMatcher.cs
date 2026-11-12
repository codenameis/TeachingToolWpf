using CpuMatching.Core;

namespace CpuMatching.SubNcc;

/// <summary>축소 원판의 선택 픽셀 ZNCC → 원본 사각형 전체 내부 픽셀 정밀화.</summary>
public sealed class SubNccMatcher : ITemplateMatcher
{
    private static readonly PyramidSearchPlan SearchPlan = new(RefineCoarsePositions: true);

    private readonly CpuGrayImage _template;
    private readonly SubNccOptions _parameters;

    public SubNccMatcher(CpuGrayImage template, SubNccOptions? parameters = null)
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
        PreviewPoints = ExtractPreviewPoints(template, _parameters);
    }

    public IReadOnlyList<(double X, double Y)> PreviewPoints { get; }

    private static (double X, double Y)[] ExtractPreviewPoints(CpuGrayImage template, SubNccOptions parameters)
    {
        var cx = (template.Width - 1) / 2.0;
        var cy = (template.Height - 1) / 2.0;
        var samples = CorrelationLevel.SelectPixels(template, cx, cy, false, parameters.CoarseSamples);
        var points = new (double X, double Y)[samples.Count];
        for (var i = 0; i < samples.Count; i++)
        {
            points[i] = (samples[i].X, samples[i].Y);
        }
        return points;
    }

    public SearchResult Find(CpuGrayImage image, CpuSearchOptions? options = null, CancellationToken cancellationToken = default,
        Func<double, double, bool>? acceptCenter = null, ICoarseScoreAccelerator? accelerator = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        return PyramidSearch.Find(_template, image, options ?? new CpuSearchOptions(),
            (template, scene, cx, cy, fine) => new CorrelationLevel(template, scene, cx, cy, fine, _parameters), cancellationToken,
            plan: SearchPlan, acceptCenter: acceptCenter, accelerator: accelerator);
    }

    public SearchResult FindWindowed(IGrayImageSource image, CpuSearchOptions? options = null, CancellationToken cancellationToken = default,
        Func<double, double, bool>? acceptCenter = null, ICoarseScoreAccelerator? accelerator = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        return PyramidSearch.FindWindowed(_template, image, options ?? new CpuSearchOptions(),
            (template, scene, cx, cy, fine) => new CorrelationLevel(template, scene, cx, cy, fine, _parameters), cancellationToken,
            plan: SearchPlan, acceptCenter: acceptCenter, accelerator: accelerator);
    }

    private sealed class CorrelationLevel : IScoreLevel
    {
        public bool SupportsParallelEvaluation => true;

        private readonly CpuGrayImage _scene;
        private readonly CorrelationSample[] _samples;
        private readonly double _energy;

        public CorrelationLevel(CpuGrayImage template, CpuGrayImage scene, double cx, double cy, bool fine, SubNccOptions parameters)
        {
            _scene = scene;
            var points = SelectPixels(template, cx, cy, fine, parameters.CoarseSamples);
            var mean = points.Average(point => point.Value);
            _samples = points.Select(point => point with { Value = point.Value - mean }).ToArray();
            _energy = _samples.Sum(point => point.Value * point.Value);
        }

        // 축소 영상에서는 밝기와 경계 표본을 섞고, 원본에서는 내부 픽셀을 모두 쓴다.
        internal static List<CorrelationSample> SelectPixels(CpuGrayImage template, double cx, double cy, bool fine, int coarseSamples)
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
            var (sinVal, cosVal) = Math.SinCos(angle);
            var cos = cosVal * scale;
            var sin = sinVal * scale;
            var rotated = new CorrelationSample[_samples.Length];
            for (var i = 0; i < _samples.Length; i++)
            {
                var point = _samples[i];
                rotated[i] = new CorrelationSample(
                    (point.X * cos) - (point.Y * sin), (point.X * sin) + (point.Y * cos), point.Value);
            }
            return new CorrelationPose(_scene, rotated, _energy);
        }
    }

    private sealed class CorrelationPose(CpuGrayImage image, CorrelationSample[] samples, double templateEnergy) : IPoseScore, IAcceleratedPoseScore
    {
        private readonly (double Left, double Top, double Right, double Bottom) _bounds = GetBounds(samples);

        private static (double Left, double Top, double Right, double Bottom) GetBounds(CorrelationSample[] points)
        {
            var left = double.PositiveInfinity;
            var top = double.PositiveInfinity;
            var right = double.NegativeInfinity;
            var bottom = double.NegativeInfinity;
            foreach (var point in points)
            {
                left = Math.Min(left, point.X);
                top = Math.Min(top, point.Y);
                right = Math.Max(right, point.X);
                bottom = Math.Max(bottom, point.Y);
            }
            return (left, top, right, bottom);
        }

        public void ScoreGrid(ICoarseScoreAccelerator accelerator, double[] output, CancellationToken token) =>
            accelerator.ScoreCorrelation(image, samples, templateEnergy, output, token);

        public double Score(double x, double y)
        {
            if (templateEnergy < 1 || x + _bounds.Left < 0 || y + _bounds.Top < 0 ||
                x + _bounds.Right >= image.Width - 1 || y + _bounds.Bottom >= image.Height - 1)
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
                // 회전된 전체 표본의 경계를 먼저 확인했다. 표본별 검사와 Clamp를 반복하지 않는다.
                // Bilinear의 연산 순서는 CpuGrayImage.Sample 및 CUDA와 동일하다.
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
