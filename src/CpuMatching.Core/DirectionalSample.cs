namespace CpuMatching.Core;

/// <summary>회전시킨 윤곽점과 인접 방향 두 개 사이의 가중치.</summary>
public readonly record struct DirectionalSample(double X, double Y, int Bin, int Next, double Fraction)
{
    public static DirectionalSample[] Rotate(CpuEdgePoint[] features, double angleDegrees, double scale, int bins)
    {
        var angle = angleDegrees * EdgeRefinementConstants.DegreesToRadians;
        var (sinVal, cosVal) = Math.SinCos(angle);
        var cos = cosVal * scale;
        var sin = sinVal * scale;
        var result = new DirectionalSample[features.Length];
        var twoPi = EdgeRefinementConstants.TwoPi;
        for (var i = 0; i < features.Length; i++)
        {
            var point = features[i];
            var orientation = ((point.Angle + angle + (4 * Math.PI)) % twoPi) * bins / twoPi;
            var bin = (int)orientation;
            result[i] = new DirectionalSample((point.X * cos) - (point.Y * sin), (point.X * sin) + (point.Y * cos),
                bin % bins, (bin + 1) % bins, orientation - bin);
        }
        return result;
    }
}
