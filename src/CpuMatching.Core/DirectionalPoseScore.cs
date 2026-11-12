namespace CpuMatching.Core;

/// <summary>각 윤곽점의 위치와 방향을 보간하고, 전체 평균으로 자세 점수를 계산한다.</summary>
public sealed class DirectionalPoseScore(float[][] maps, DirectionalSample[] points, int width, int height) : IPoseScore, IAcceleratedPoseScore
{
    public void ScoreGrid(ICoarseScoreAccelerator accelerator, double[] output, CancellationToken token) =>
        accelerator.ScoreDirections(maps, points, width, height, DirectionalSampling.Bilinear, output, token);

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
