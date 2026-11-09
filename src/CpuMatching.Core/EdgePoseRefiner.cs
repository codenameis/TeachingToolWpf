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
        Span<double> matrix = stackalloc double[12];
        Span<double> delta = stackalloc double[3];
        for (var iteration = 0; iteration < EdgeRefinementConstants.MaxRefinementIterations; iteration++)
        {
            // 미지수는 X 이동, Y 이동, 회전의 세 개다. 마지막 열에는 오차를 누적한다.
            matrix.Clear();
            var cos = Math.Cos(pose.AngleDegrees * EdgeRefinementConstants.DegreesToRadians);
            var sin = Math.Sin(pose.AngleDegrees * EdgeRefinementConstants.DegreesToRadians);
            var count = 0;
            foreach (var point in _template)
            {
                var rx = ((point.X * cos) - (point.Y * sin)) * pose.Scale;
                var ry = ((point.X * sin) + (point.Y * cos)) * pose.Scale;
                var px = pose.X + rx;
                var py = pose.Y + ry;
                var expected = point.Angle + (pose.AngleDegrees * EdgeRefinementConstants.DegreesToRadians);
                var best = FindNearbyEdge(px, py, expected);
                if (best is not { } edge)
                {
                    continue;
                }
                var nx = Math.Cos(edge.Angle);
                var ny = Math.Sin(edge.Angle);
                var residual = (nx * (edge.X - px)) + (ny * (edge.Y - py));
                var weight = Math.Min(1, 1 / Math.Max(EdgeRefinementConstants.RobustWeightEpsilon, Math.Abs(residual)));
                var j0 = nx;
                var j1 = ny;
                var j2 = (-nx * ry) + (ny * rx);

                var wj0 = weight * j0;
                var wj1 = weight * j1;
                var wj2 = weight * j2;

                matrix[0] += wj0 * j0;
                matrix[1] += wj0 * j1;
                matrix[2] += wj0 * j2;
                matrix[3] += wj0 * residual;

                matrix[4] += wj1 * j0;
                matrix[5] += wj1 * j1;
                matrix[6] += wj1 * j2;
                matrix[7] += wj1 * residual;

                matrix[8] += wj2 * j0;
                matrix[9] += wj2 * j1;
                matrix[10] += wj2 * j2;
                matrix[11] += wj2 * residual;

                count++;
            }
            if (count < EdgeRefinementConstants.MinEdgeCorrespondenceCount || !Solve(matrix, delta))
            {
                break;
            }
            var angleDelta = Math.Clamp(delta[2] * EdgeRefinementConstants.RadiansToDegrees, -EdgeRefinementConstants.MaxPerIterationAngleStepDegrees, EdgeRefinementConstants.MaxPerIterationAngleStepDegrees);
            var next = pose with
            {
                X = pose.X + Math.Clamp(delta[0], -EdgeRefinementConstants.MaxPerIterationTranslationStepPixels, EdgeRefinementConstants.MaxPerIterationTranslationStepPixels),
                Y = pose.Y + Math.Clamp(delta[1], -EdgeRefinementConstants.MaxPerIterationTranslationStepPixels, EdgeRefinementConstants.MaxPerIterationTranslationStepPixels),
                AngleDegrees = options.MaximumAngle - options.MinimumAngle == EdgeRefinementConstants.FullCircleDegrees ? pose.AngleDegrees + angleDelta :
                    Math.Clamp(pose.AngleDegrees + angleDelta, options.MinimumAngle, options.MaximumAngle)
            };
            // 국소 보정이 다른 물체나 다른 자세로 넘어가는 것을 막는다.
            if (Math.Abs(next.X - seed.X) > EdgeRefinementConstants.MaxAllowedTranslationDriftPixels ||
                Math.Abs(next.Y - seed.Y) > EdgeRefinementConstants.MaxAllowedTranslationDriftPixels ||
                Math.Abs(next.AngleDegrees - seed.AngleDegrees) > EdgeRefinementConstants.MaxAllowedAngleDriftDegrees)
            {
                break;
            }
            pose = next;
            if (Math.Abs(delta[0]) + Math.Abs(delta[1]) + Math.Abs(angleDelta) < EdgeRefinementConstants.ConvergenceTolerance)
            {
                break;
            }
        }
        return pose;
    }

    private EdgePoint? FindNearbyEdge(double px, double py, double expected)
    {
        EdgePoint? best = null;
        var cost = double.MaxValue;
        var ix = (int)Math.Round(px);
        var iy = (int)Math.Round(py);
        const int searchRadius = EdgeRefinementConstants.NearbyEdgeSearchRadiusPixels;
        // [Domain Why]: cos(30도) = 0.866... 여기서 0.8 이상은 약 36.8도 이내로 정렬된 에지 포인트만 후보로 허용함을 의미한다.
        const double minCosineAlignment = 0.8;
        // [Domain Why]: 각도 불일치(1 - cos)에 부여하는 가중 계수 (거리 단위 px^2와 스케일을 맞춤).
        const double anglePenaltyWeight = 4.0;

        for (var y = Math.Max(1, iy - searchRadius); y <= Math.Min(_height - 2, iy + searchRadius); y++)
        {
            for (var x = Math.Max(1, ix - searchRadius); x <= Math.Min(_width - 2, ix + searchRadius); x++)
            {
                if (_scene[(y * _width) + x] is not { } candidate)
                {
                    continue;
                }
                var alignment = Math.Cos(candidate.Angle - expected);
                if (alignment < minCosineAlignment)
                {
                    continue;
                }
                var distance = ((candidate.X - px) * (candidate.X - px)) + ((candidate.Y - py) * (candidate.Y - py)) + (anglePenaltyWeight * (1 - alignment));
                if (distance < cost)
                {
                    best = candidate;
                    cost = distance;
                }
            }
        }
        return best;
    }

    // 두꺼운 경계를 밝기 변화 방향의 최대점 한 줄로 줄이고, 1픽셀 이내 위치를 보간한다.
    private static EdgePoint?[] Thin(GrayImage image)
    {
        var gradients = Edges.Gradients(image);
        var magnitude = new float[gradients.Length];
        for (var i = 0; i < gradients.Length; i++)
        {
            var (gx, gy) = gradients[i];
            magnitude[i] = (float)Math.Sqrt((gx * gx) + (gy * gy));
        }
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

    // 가장 큰 pivot부터 소거해 세 보정량을 구한다. 특이 행렬이면 보정을 멈춘다.
    private static bool Solve(Span<double> matrix, Span<double> result)
    {
        for (var pivot = 0; pivot < 3; pivot++)
        {
            var best = pivot;
            for (var row = pivot + 1; row < 3; row++)
            {
                if (Math.Abs(matrix[(row * 4) + pivot]) > Math.Abs(matrix[(best * 4) + pivot]))
                {
                    best = row;
                }
            }
            if (Math.Abs(matrix[(best * 4) + pivot]) < 1e-8)
            {
                return false;
            }
            for (var column = 0; column < 4; column++)
            {
                (matrix[(pivot * 4) + column], matrix[(best * 4) + column]) = (matrix[(best * 4) + column], matrix[(pivot * 4) + column]);
            }
            var divisor = matrix[(pivot * 4) + pivot];
            for (var column = pivot; column < 4; column++)
            {
                matrix[(pivot * 4) + column] /= divisor;
            }
            for (var row = 0; row < 3; row++)
            {
                if (row == pivot)
                {
                    continue;
                }
                var factor = matrix[(row * 4) + pivot];
                for (var column = pivot; column < 4; column++)
                {
                    matrix[(row * 4) + column] -= factor * matrix[(pivot * 4) + column];
                }
            }
        }
        for (var row = 0; row < 3; row++)
        {
            result[row] = matrix[(row * 4) + 3];
        }
        return true;
    }
}
