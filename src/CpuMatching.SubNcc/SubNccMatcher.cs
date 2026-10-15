using CpuMatching.Core;

namespace CpuMatching.SubNcc;

/// <summary>회전 유효 원판의 선택 픽셀 ZNCC → 원판 전체 픽셀 정밀화.</summary>
public sealed class SubNccMatcher : ITemplateMatcher
{
    private readonly GrayImage _template;

    public SubNccMatcher(GrayImage template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var values = template.Pixels.Span;
        var minimum = byte.MaxValue;
        var maximum = byte.MinValue;
        foreach (var value in values)
        {
            minimum = Math.Min(minimum, value);
            maximum = Math.Max(maximum, value);
        }
        if (maximum - minimum < 8)
        {
            throw new ArgumentException("Template has insufficient contrast.", nameof(template));
        }
        _template = template;
    }

    public SearchResult Find(GrayImage image, SearchOptions? options = null, CancellationToken cancellationToken = default,
        Func<double, double, bool>? acceptCenter = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        return PyramidSearch.Find(_template, image, options ?? new SearchOptions(),
            (template, scene, cx, cy, fine) => new CorrelationLevel(template, scene, cx, cy, fine), cancellationToken, acceptCenter: acceptCenter);
    }

    private readonly record struct Sample(double X, double Y, double Value);

    private sealed class CorrelationLevel : IScoreLevel
    {
        private readonly GrayImage _scene;
        private readonly Sample[] _samples;
        private readonly double _energy;

        public CorrelationLevel(GrayImage template, GrayImage scene, double cx, double cy, bool fine)
        {
            _scene = scene;
            var radius = (Math.Min(template.Width, template.Height) - 3) / 2.0;
            var points = new List<Sample>();
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
                        points.Add(new Sample(dx, dy, template[x, y]));
                    }
                }
            }
            if (!fine && points.Count > 96)
            {
                // 경계만 고르면 상수 명암 표본이 될 수 있어 균일 표본도 함께 보존한다.
                var uniform = points.Where((_, index) => index % Math.Max(1, points.Count / 48) == 0).Take(48);
                var edges = points.OrderByDescending(point =>
                {
                    var (gx, gy) = gradients[((int)(point.Y + cy) * template.Width) + (int)(point.X + cx)];
                    return (gx * gx) + (gy * gy);
                }).Take(48);
                points = uniform.Concat(edges).Distinct().ToList();
            }
            var mean = points.Average(point => point.Value);
            _samples = points.Select(point => point with { Value = point.Value - mean }).ToArray();
            _energy = _samples.Sum(point => point.Value * point.Value);
        }

        public IPoseScore AtPose(double angleDegrees, double scale)
        {
            var angle = angleDegrees * Math.PI / 180;
            var cos = Math.Cos(angle) * scale;
            var sin = Math.Sin(angle) * scale;
            return new CorrelationPose(_scene, _samples.Select(point => new Sample(
                (point.X * cos) - (point.Y * sin), (point.X * sin) + (point.Y * cos), point.Value)).ToArray(), _energy);
        }
    }

    private sealed class CorrelationPose(GrayImage image, Sample[] samples, double templateEnergy) : IPoseScore
    {
        public double Score(double x, double y)
        {
            if (templateEnergy < 1)
            {
                return 0;
            }
            double sum = 0;
            double squares = 0;
            double correlation = 0;
            foreach (var point in samples)
            {
                var px = x + point.X;
                var py = y + point.Y;
                if (px < 0 || py < 0 || px >= image.Width - 1 || py >= image.Height - 1)
                {
                    return 0;
                }
                var value = image.Sample(px, py);
                sum += value;
                squares += value * value;
                correlation += point.Value * value;
            }
            var energy = squares - (sum * sum / samples.Length);
            return energy < 1 ? 0 : Math.Clamp(correlation / Math.Sqrt(templateEnergy * energy), 0, 1);
        }
    }
}
