using System.Buffers;
using System.Numerics;
using CpuMatching.Core;

namespace CpuMatching.Line2D;

/// <summary>방향 응답 맵/희소 특징 기반 CPU 매칭. 최종 단계는 극성을 보존한다.</summary>
public sealed class Line2DMatcher : ITemplateMatcher
{
    private static readonly PyramidSearchPlan SearchPlan = new(RefineEdges: true, ReuseIdenticalTiles: true);

    private readonly CpuGrayImage _template;
    private readonly Line2DOptions _parameters;

    public Line2DMatcher(CpuGrayImage template, Line2DOptions? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        _parameters = parameters ?? new Line2DOptions();
        _parameters.Validate();
        if (Edges.Extract(template, 0, 0).Length < 6)
        {
            throw new ArgumentException("Template needs at least six distinct edges.", nameof(template));
        }
        _template = template;
        PreviewPoints = ExtractPreviewPoints(template, _parameters);
    }

    public IReadOnlyList<(double X, double Y)> PreviewPoints { get; }

    private static (double X, double Y)[] ExtractPreviewPoints(CpuGrayImage template, Line2DOptions parameters)
    {
        var cx = (template.Width - 1) / 2.0;
        var cy = (template.Height - 1) / 2.0;
        var edges = Edges.Extract(template, cx, cy, parameters.FineFeatures);
        var points = new (double X, double Y)[edges.Length];
        for (var i = 0; i < edges.Length; i++)
        {
            points[i] = (edges[i].X, edges[i].Y);
        }
        return points;
    }

    public SearchResult Find(CpuGrayImage image, CpuSearchOptions? options = null, CancellationToken cancellationToken = default,
        Func<double, double, bool>? acceptCenter = null, ICoarseScoreAccelerator? accelerator = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        options ??= new CpuSearchOptions();
        return PyramidSearch.Find(_template, image, options,
            (template, scene, cx, cy, fine) => new OrientationLevel(template, scene, cx, cy, fine, _parameters,
                options.MaximumDegreeOfParallelism, cancellationToken), cancellationToken,
            plan: SearchPlan, acceptCenter: acceptCenter, accelerator: accelerator);
    }

    public SearchResult FindWindowed(IGrayImageSource image, CpuSearchOptions? options = null, CancellationToken cancellationToken = default,
        Func<double, double, bool>? acceptCenter = null, ICoarseScoreAccelerator? accelerator = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        options ??= new CpuSearchOptions();
        return PyramidSearch.FindWindowed(_template, image, options,
            (template, scene, cx, cy, fine) => new OrientationLevel(template, scene, cx, cy, fine, _parameters,
                options.MaximumDegreeOfParallelism, cancellationToken), cancellationToken,
            plan: SearchPlan, acceptCenter: acceptCenter, accelerator: accelerator);
    }

    private sealed class OrientationLevel : IScoreLevel
    {
        public bool SupportsParallelEvaluation => true;

        private const int Bins = 16;
        private readonly float[][] _maps;
        private readonly CpuEdgePoint[] _features;
        private readonly int _width;
        private readonly int _height;
        private readonly bool _fine;

        public OrientationLevel(CpuGrayImage template, CpuGrayImage scene, double cx, double cy, bool fine, Line2DOptions parameters,
            int workers, CancellationToken token)
        {
            _width = scene.Width;
            _height = scene.Height;
            _fine = fine;
            _features = Edges.Extract(template, cx, cy, fine ? parameters.FineFeatures : parameters.CoarseFeatures);
            _maps = new float[Bins][];
            var gradients = Edges.Gradients(scene);
            var lengths = new double[gradients.Length];
            for (var i = 0; i < gradients.Length; i++)
            {
                var g = gradients[i];
                lengths[i] = Math.Sqrt((g.X * g.X) + (g.Y * g.Y));
            }
            if (workers > 1 && gradients.Length >= 16_384)
            {
                Parallel.For(0, Bins, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = token }, BuildMap);
            }
            else
            {
                for (var bin = 0; bin < Bins; bin++)
                {
                    BuildMap(bin);
                }
            }

            void BuildMap(int bin)
            {
                var map = new float[_width * _height];
                var angle = bin * 2 * Math.PI / Bins;
                var nx = Math.Cos(angle);
                var ny = Math.Sin(angle);
                for (var i = 0; i < map.Length; i++)
                {
                    if (i % 16_384 == 0)
                    {
                        token.ThrowIfCancellationRequested();
                    }
                    var (gx, gy) = gradients[i];
                    var length = lengths[i];
                    if (length > 4)
                    {
                        map[i] = (float)(Math.Max(0, ((gx * nx) + (gy * ny)) / length) * Math.Min(1, length / 12));
                    }
                }
                _maps[bin] = fine ? map : Spread(map);
            }
        }

        public IPoseScore AtPose(double angleDegrees, double scale)
        {
            var points = DirectionalSample.Rotate(_features, angleDegrees, scale, Bins);
            return _fine ? new DirectionalPoseScore(_maps, points, _width, _height) : new CoarsePose(_maps, points, _width, _height);
        }

        private float[] Spread(float[] source)
        {
            var result = new float[source.Length];
            for (var y = 1; y < _height - 1; y++)
            {
                var x = 1;
                // 각 lane은 서로 다른 픽셀이다. 기존 3×3 이웃 순서와 바깥 테두리 0을 유지한다.
                for (; x <= _width - 1 - Vector<float>.Count; x += Vector<float>.Count)
                {
                    var best = Vector<float>.Zero;
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            best = Vector.Max(best, new Vector<float>(source, ((y + dy) * _width) + x + dx));
                        }
                    }
                    best.CopyTo(result, (y * _width) + x);
                }
                for (; x < _width - 1; x++)
                {
                    var best = 0f;
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            best = Math.Max(best, source[((y + dy) * _width) + x + dx]);
                        }
                    }
                    result[(y * _width) + x] = best;
                }
            }
            return result;
        }
    }

    private sealed class CoarsePose : IPoseScore, IRowScore, IAcceleratedPoseScore
    {
        private readonly float[][] _maps;
        private readonly DirectionalSample[] _points;
        private readonly int _width;
        private readonly int _height;

        public bool SupportsParallelRows => true;

        public CoarsePose(float[][] maps, DirectionalSample[] points, int width, int height)
        {
            _maps = maps;
            var rounded = new DirectionalSample[points.Length];
            for (var i = 0; i < points.Length; i++)
            {
                var pt = points[i];
                rounded[i] = pt with { X = Math.Round(pt.X), Y = Math.Round(pt.Y) };
            }
            _points = rounded;
            _width = width;
            _height = height;
        }

        public double Score(double x, double y)
        {
            double sum = 0;
            foreach (var point in _points)
            {
                var px = (int)Math.Round(x + point.X);
                var py = (int)Math.Round(y + point.Y);
                if (px < 1 || py < 1 || px >= _width - 2 || py >= _height - 2)
                {
                    return 0;
                }
                var index = (py * _width) + px;
                sum += ((1 - point.Fraction) * _maps[point.Bin][index]) + (point.Fraction * _maps[point.Next][index]);
            }
            return _points.Length == 0 ? 0 : sum / _points.Length;
        }

        public void ScoreGrid(ICoarseScoreAccelerator accelerator, double[] output, CancellationToken token) =>
            accelerator.ScoreDirections(_maps, _points, _width, _height, DirectionalSampling.RoundedRow, output, token);

        public void ScoreRow(int y, Span<double> output)
        {
            output.Clear();
            var left = 0;
            var right = _width;
            foreach (var point in _points)
            {
                var py = y + (int)point.Y;
                if (py < 1 || py >= _height - 2)
                {
                    return;
                }
                left = Math.Max(left, 1 - (int)point.X);
                right = Math.Min(right, _width - 2 - (int)point.X);
            }
            if (_points.Length == 0 || left >= right)
            {
                return;
            }
            // 한 행의 누적 버퍼만 빌린다. 다른 행과 공유하지 않으며 예외에서도 반환한다.
            var row = ArrayPool<float>.Shared.Rent(_width);
            try
            {
                row.AsSpan(left, right - left).Clear();
                AccumulateRow(y, left, right, row, output);
            }
            finally
            {
                ArrayPool<float>.Shared.Return(row);
            }
        }

        private void AccumulateRow(int y, int left, int right, float[] row, Span<double> output)
        {
            foreach (var point in _points)
            {
                var offset = ((y + (int)point.Y) * _width) + (int)point.X;
                var first = _maps[point.Bin];
                var second = _maps[point.Next];
                var weight = new Vector<float>((float)point.Fraction);
                var inverse = new Vector<float>((float)(1 - point.Fraction));
                var x = left;
                for (; x <= right - Vector<float>.Count; x += Vector<float>.Count)
                {
                    var sum = new Vector<float>(row, x) + (new Vector<float>(first, offset + x) * inverse) +
                        (new Vector<float>(second, offset + x) * weight);
                    sum.CopyTo(row, x);
                }
                for (; x < right; x++)
                {
                    row[x] += (float)(((1 - point.Fraction) * first[offset + x]) + (point.Fraction * second[offset + x]));
                }
            }
            for (var x = left; x < right; x++)
            {
                output[x] = row[x] / _points.Length;
            }
        }
    }

}
