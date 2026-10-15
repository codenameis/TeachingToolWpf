namespace CpuMatching.Core;

/// <summary>방향이 일치하는 원본 서브픽셀 경계의 point-to-line 최소제곱 자세 보정.</summary>
public sealed class EdgePoseRefiner
{
    private readonly EdgePoint[] _template;
    private readonly EdgePoint?[] _scene;
    private readonly int _width;
    private readonly int _height;

    public EdgePoseRefiner(GrayImage template, GrayImage scene)
    {
        _width = scene.Width;
        _height = scene.Height;
        _template = Thin(template).Where(point => point.HasValue).Select(point => point!.Value with
        {
            X = point.Value.X - ((template.Width - 1) / 2.0),
            Y = point.Value.Y - ((template.Height - 1) / 2.0)
        }).ToArray();
        _scene = Thin(scene);
    }

    public Match Refine(Match seed, SearchOptions options)
    {
        var pose = seed;
        for (var iteration = 0; iteration < 8; iteration++)
        {
            var matrix = new double[3, 4];
            var cos = Math.Cos(pose.AngleDegrees * Math.PI / 180);
            var sin = Math.Sin(pose.AngleDegrees * Math.PI / 180);
            var count = 0;
            foreach (var point in _template)
            {
                var rx = ((point.X * cos) - (point.Y * sin)) * pose.Scale;
                var ry = ((point.X * sin) + (point.Y * cos)) * pose.Scale;
                var px = pose.X + rx;
                var py = pose.Y + ry;
                var expected = point.Angle + (pose.AngleDegrees * Math.PI / 180);
                EdgePoint? best = null;
                var cost = double.MaxValue;
                var ix = (int)Math.Round(px);
                var iy = (int)Math.Round(py);
                for (var y = Math.Max(1, iy - 3); y <= Math.Min(_height - 2, iy + 3); y++)
                {
                    for (var x = Math.Max(1, ix - 3); x <= Math.Min(_width - 2, ix + 3); x++)
                    {
                        if (_scene[(y * _width) + x] is not { } candidate)
                        {
                            continue;
                        }
                        var alignment = Math.Cos(candidate.Angle - expected);
                        if (alignment < 0.8)
                        {
                            continue;
                        }
                        var distance = ((candidate.X - px) * (candidate.X - px)) + ((candidate.Y - py) * (candidate.Y - py)) + (4 * (1 - alignment));
                        if (distance < cost)
                        {
                            best = candidate;
                            cost = distance;
                        }
                    }
                }
                if (best is not { } edge)
                {
                    continue;
                }
                var nx = Math.Cos(edge.Angle);
                var ny = Math.Sin(edge.Angle);
                var residual = (nx * (edge.X - px)) + (ny * (edge.Y - py));
                var weight = Math.Min(1, 1 / Math.Max(1e-6, Math.Abs(residual)));
                double[] jacobian = [nx, ny, (-nx * ry) + (ny * rx)];
                for (var row = 0; row < 3; row++)
                {
                    for (var column = 0; column < 3; column++)
                    {
                        matrix[row, column] += weight * jacobian[row] * jacobian[column];
                    }
                    matrix[row, 3] += weight * jacobian[row] * residual;
                }
                count++;
            }
            if (count < 8 || !Solve(matrix, out var delta))
            {
                break;
            }
            var angleDelta = Math.Clamp(delta[2] * 180 / Math.PI, -2, 2);
            var next = pose with
            {
                X = pose.X + Math.Clamp(delta[0], -1, 1),
                Y = pose.Y + Math.Clamp(delta[1], -1, 1),
                AngleDegrees = options.MaximumAngle - options.MinimumAngle == 360 ? pose.AngleDegrees + angleDelta :
                    Math.Clamp(pose.AngleDegrees + angleDelta, options.MinimumAngle, options.MaximumAngle)
            };
            // 국소 보정이 다른 물체나 다른 자세로 넘어가는 것을 막는다.
            if (Math.Abs(next.X - seed.X) > 3 || Math.Abs(next.Y - seed.Y) > 3 ||
                Math.Abs(next.AngleDegrees - seed.AngleDegrees) > 6)
            {
                break;
            }
            pose = next;
            if (Math.Abs(delta[0]) + Math.Abs(delta[1]) + Math.Abs(angleDelta) < 0.005)
            {
                break;
            }
        }
        return pose;
    }

    private static EdgePoint?[] Thin(GrayImage image)
    {
        var gradients = Edges.Gradients(image);
        var magnitude = gradients.Select(value => (float)Math.Sqrt((value.X * value.X) + (value.Y * value.Y))).ToArray();
        var result = new EdgePoint?[gradients.Length];
        for (var y = 2; y < image.Height - 2; y++)
        {
            for (var x = 2; x < image.Width - 2; x++)
            {
                var index = (y * image.Width) + x;
                var length = magnitude[index];
                if (length < 12)
                {
                    continue;
                }
                var (gx, gy) = gradients[index];
                var nx = gx / length;
                var ny = gy / length;
                var before = Edges.Bilinear(magnitude, image.Width, image.Height, x - nx, y - ny);
                var after = Edges.Bilinear(magnitude, image.Width, image.Height, x + nx, y + ny);
                if (length < before || length < after)
                {
                    continue;
                }
                var denominator = before - (2 * length) + after;
                var offset = Math.Abs(denominator) < 1e-6 ? 0 : Math.Clamp(0.5 * (before - after) / denominator, -0.5, 0.5);
                result[index] = new EdgePoint(x + (offset * nx), y + (offset * ny), Math.Atan2(gy, gx), length);
            }
        }
        return result;
    }

    private static bool Solve(double[,] matrix, out double[] result)
    {
        result = new double[3];
        for (var pivot = 0; pivot < 3; pivot++)
        {
            var best = pivot;
            for (var row = pivot + 1; row < 3; row++)
            {
                if (Math.Abs(matrix[row, pivot]) > Math.Abs(matrix[best, pivot]))
                {
                    best = row;
                }
            }
            if (Math.Abs(matrix[best, pivot]) < 1e-8)
            {
                return false;
            }
            for (var column = 0; column < 4; column++)
            {
                (matrix[pivot, column], matrix[best, column]) = (matrix[best, column], matrix[pivot, column]);
            }
            var divisor = matrix[pivot, pivot];
            for (var column = pivot; column < 4; column++)
            {
                matrix[pivot, column] /= divisor;
            }
            for (var row = 0; row < 3; row++)
            {
                if (row == pivot)
                {
                    continue;
                }
                var factor = matrix[row, pivot];
                for (var column = pivot; column < 4; column++)
                {
                    matrix[row, column] -= factor * matrix[pivot, column];
                }
            }
        }
        for (var row = 0; row < 3; row++)
        {
            result[row] = matrix[row, 3];
        }
        return true;
    }
}
