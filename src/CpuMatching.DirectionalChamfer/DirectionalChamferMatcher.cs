using CpuMatching.Core;

namespace CpuMatching.DirectionalChamfer;

/// <summary>방향별 거리 변환에 기반한 CPU Chamfer. 원 논문의 선분 적분 가속은 포함하지 않는다.</summary>
public sealed class DirectionalChamferMatcher : ITemplateMatcher
{
    private readonly GrayImage _template;

    public DirectionalChamferMatcher(GrayImage template)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (Edges.Extract(template, 0, 0).Length < 6)
        {
            throw new ArgumentException("Template needs at least six distinct edges.", nameof(template));
        }
        _template = template;
    }

    public SearchResult Find(GrayImage image, SearchOptions? options = null, CancellationToken cancellationToken = default,
        Func<double, double, bool>? acceptCenter = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        return PyramidSearch.Find(_template, image, options ?? new SearchOptions(),
            (template, scene, cx, cy, fine) => new DistanceLevel(template, scene, cx, cy, fine), cancellationToken, refineEdges: true, acceptCenter: acceptCenter);
    }

    private const int Bins = 16;

    private readonly record struct Sample(double X, double Y, int Bin, int Next, double Fraction);

    private sealed class DistanceLevel : IScoreLevel
    {
        private readonly float[][] _maps;
        private readonly EdgePoint[] _features;
        private readonly int _width;
        private readonly int _height;
        private readonly double _tolerance;
        private readonly (double X, double Y)[]? _gradients;
        private readonly float[][]? _templateMaps;
        private readonly int _templateWidth;
        private readonly int _templateHeight;

        public DistanceLevel(GrayImage template, GrayImage scene, double cx, double cy, bool fine)
        {
            _width = scene.Width;
            _height = scene.Height;
            _features = Edges.Extract(template, cx, cy, fine ? 128 : 64);
            // 원본 윤곽의 거리 단위는 물체 크기에 비례해 늘어나지 않는다.
            // 넓은 후보 탐색 허용은 축소 레벨에만 두고 원본에서는 1px 단위로 평가한다.
            _tolerance = fine ? Math.Clamp(Math.Min(template.Width, template.Height) / 16.0, 0.6, 1) : 5;
            var gradients = Edges.Gradients(scene);
            _maps = CreateMaps(gradients, _width, _height, _tolerance);
            if (fine)
            {
                _gradients = gradients;
                _templateWidth = template.Width;
                _templateHeight = template.Height;
                _templateMaps = CreateMaps(Edges.Gradients(template), template.Width, template.Height, _tolerance);
            }
        }

        private static float[][] CreateMaps((double X, double Y)[] gradients, int width, int height, double tolerance)
        {
            var maps = Enumerable.Range(0, Bins).Select(_ => Enumerable.Repeat(32f, width * height).ToArray()).ToArray();
            for (var i = 0; i < gradients.Length; i++)
            {
                var (gx, gy) = gradients[i];
                if ((gx * gx) + (gy * gy) >= 144)
                {
                    var bin = (int)Math.Round((Math.Atan2(gy, gx) + (2 * Math.PI)) % (2 * Math.PI) * Bins / (2 * Math.PI)) % Bins;
                    maps[bin][i] = 0;
                }
            }
            foreach (var map in maps)
            {
                Transform(map, width, height);
            }
            // 순환 방향 축의 L1 거리 변환. 모든 bin 쌍을 비교하는 O(B²N)과 같은 최소 비용이다.
            for (var pass = 0; pass < 2; pass++)
            {
                for (var bin = 0; bin < Bins * 2; bin++)
                {
                    var current = pass == 0 ? bin % Bins : Bins - 1 - (bin % Bins);
                    var previous = (current + (pass == 0 ? Bins - 1 : 1)) % Bins;
                    for (var i = 0; i < maps[current].Length; i++)
                    {
                        maps[current][i] = Math.Min(maps[current][i], maps[previous][i] + 1.5f);
                    }
                }
            }
            for (var bin = 0; bin < Bins; bin++)
            {
                var map = maps[bin];
                for (var i = 0; i < map.Length; i++)
                {
                    // 영상 밖이 0점이 되도록 거리 대신 점수 맵을 보간한다.
                    map[i] = (float)Math.Max(0, 1 - (map[i] / (tolerance * 4)));
                }
            }
            return maps;
        }

        public double Verify(Match match)
        {
            var forward = AtPose(match.AngleDegrees, match.Scale).Score(match.X, match.Y);
            if (_gradients is null || _templateMaps is null || forward <= 0)
            {
                return forward;
            }
            var angle = match.AngleDegrees * Math.PI / 180;
            var cos = Math.Cos(angle);
            var sin = Math.Sin(angle);
            var rx = ((Math.Abs(cos) * _templateWidth) + (Math.Abs(sin) * _templateHeight)) * match.Scale / 2;
            var ry = ((Math.Abs(sin) * _templateWidth) + (Math.Abs(cos) * _templateHeight)) * match.Scale / 2;
            double sum = 0;
            double weight = 0;
            for (var y = Math.Max(1, (int)Math.Floor(match.Y - ry)); y <= Math.Min(_height - 2, (int)Math.Ceiling(match.Y + ry)); y++)
            {
                for (var x = Math.Max(1, (int)Math.Floor(match.X - rx)); x <= Math.Min(_width - 2, (int)Math.Ceiling(match.X + rx)); x++)
                {
                    var tx = ((((x - match.X) * cos) + ((y - match.Y) * sin)) / match.Scale) + ((_templateWidth - 1) / 2d);
                    var ty = (((-(x - match.X) * sin) + ((y - match.Y) * cos)) / match.Scale) + ((_templateHeight - 1) / 2d);
                    // ROI 밖의 배경과 crop 경계의 미분은 모델의 일부가 아니다.
                    if (tx < 2 || ty < 2 || tx > _templateWidth - 3 || ty > _templateHeight - 3)
                    {
                        continue;
                    }
                    var (gx, gy) = _gradients[(y * _width) + x];
                    var strength = Math.Sqrt((gx * gx) + (gy * gy));
                    if (strength < 12)
                    {
                        continue;
                    }
                    var orientation = (Math.Atan2(gy, gx) - angle + (8 * Math.PI)) % (2 * Math.PI) * Bins / (2 * Math.PI);
                    var bin = (int)orientation;
                    var fraction = orientation - bin;
                    var agreement = ((1 - fraction) * Edges.Bilinear(_templateMaps[bin % Bins], _templateWidth, _templateHeight, tx, ty)) +
                        (fraction * Edges.Bilinear(_templateMaps[(bin + 1) % Bins], _templateWidth, _templateHeight, tx, ty));
                    sum += strength * agreement;
                    weight += strength;
                }
            }
            var reverse = weight > 0 ? sum / weight : 0;
            // 조밀한 무관 윤곽은 단방향 거리만 작게 만들 수 있다. 양방향 지지율을 함께 평가한다.
            return forward + reverse > 0 ? 2 * forward * reverse / (forward + reverse) : 0;
        }

        public IPoseScore AtPose(double angleDegrees, double scale)
        {
            var angle = angleDegrees * Math.PI / 180;
            var cos = Math.Cos(angle) * scale;
            var sin = Math.Sin(angle) * scale;
            var points = _features.Select(point =>
            {
                var orientation = (point.Angle + angle + (4 * Math.PI)) % (2 * Math.PI) * Bins / (2 * Math.PI);
                var bin = (int)orientation;
                return new Sample((point.X * cos) - (point.Y * sin), (point.X * sin) + (point.Y * cos),
                    bin % Bins, (bin + 1) % Bins, orientation - bin);
            }).ToArray();
            return new DistancePose(_maps, points, _width, _height);
        }

        private static void Transform(float[] map, int width, int height)
        {
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var index = (y * width) + x;
                    if (x > 0)
                    {
                        map[index] = Math.Min(map[index], map[index - 1] + 1);
                    }
                    if (y > 0)
                    {
                        map[index] = Math.Min(map[index], map[index - width] + 1);
                        if (x > 0)
                        {
                            map[index] = Math.Min(map[index], map[index - width - 1] + 1.414214f);
                        }
                        if (x + 1 < width)
                        {
                            map[index] = Math.Min(map[index], map[index - width + 1] + 1.414214f);
                        }
                    }
                }
            }
            for (var y = height - 1; y >= 0; y--)
            {
                for (var x = width - 1; x >= 0; x--)
                {
                    var index = (y * width) + x;
                    if (x + 1 < width)
                    {
                        map[index] = Math.Min(map[index], map[index + 1] + 1);
                    }
                    if (y + 1 < height)
                    {
                        map[index] = Math.Min(map[index], map[index + width] + 1);
                        if (x > 0)
                        {
                            map[index] = Math.Min(map[index], map[index + width - 1] + 1.414214f);
                        }
                        if (x + 1 < width)
                        {
                            map[index] = Math.Min(map[index], map[index + width + 1] + 1.414214f);
                        }
                    }
                }
            }
        }
    }

    private sealed class DistancePose(float[][] maps, Sample[] points, int width, int height) : IPoseScore
    {
        public double Score(double x, double y)
        {
            if (points.Length == 0)
            {
                return 0;
            }
            double sum = 0;
            foreach (var point in points)
            {
                var px = x + point.X;
                var py = y + point.Y;
                if (px < 1 || py < 1 || px >= width - 2 || py >= height - 2)
                {
                    return 0;
                }
                sum += ((1 - point.Fraction) * Edges.Bilinear(maps[point.Bin], width, height, px, py)) +
                    (point.Fraction * Edges.Bilinear(maps[point.Next], width, height, px, py));
            }
            return sum / points.Length;
        }
    }
}
