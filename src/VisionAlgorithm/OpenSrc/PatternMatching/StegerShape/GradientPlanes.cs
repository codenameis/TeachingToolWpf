namespace VisionAlgorithm.OpenSrc.PatternMatching;

/// <summary>
/// X/Y는 threshold 이상일 때 단위 gradient, Magnitude는 모든 내부 픽셀의 Sobel 크기다.
/// Bits는 atan2를 가장 가까운 π/8 방향으로 짝수 반올림한 unsigned 8방향 bit다.
/// 영상 바깥 테두리 한 줄은 0이며 threshold 미만의 X/Y/Bits도 0이다.
/// </summary>
public sealed record GradientPlanes(float[] X, float[] Y, float[] Magnitude, byte[] Bits)
{
    /// <summary>
    /// Sobel 미분 배열의 소유권을 받아 CPU 기준과 같은 순서로 정규화한다.
    /// x/y는 결과 단위 벡터로 제자리 변경하며 장치의 근사 sqrt/FMA로 판정이 달라지는 것을 막는다.
    /// </summary>
    public static GradientPlanes FromSobelDerivatives(float[] x, float[] y, float minimumGradient,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        if (x.Length != y.Length || x.Length is < 1 or > 8_388_608)
        {
            throw new ArgumentException("Sobel 미분 배열의 길이 또는 작업 예산이 잘못되었습니다.");
        }
        if (!float.IsFinite(minimumGradient) || minimumGradient <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumGradient));
        }
        cancellationToken.ThrowIfCancellationRequested();
        var result = new GradientPlanes(x, y, new float[x.Length], new byte[x.Length]);
        for (var i = 0; i < x.Length; i++)
        {
            if ((i & 4095) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            NormalizeSample(result, i, x[i], y[i], minimumGradient);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    internal static void NormalizeSample(GradientPlanes output, int index, float dx, float dy, float threshold)
    {
        var magnitude = MathF.Sqrt((dx * dx) + (dy * dy));
        output.Magnitude[index] = magnitude;
        if (magnitude >= threshold)
        {
            output.X[index] = dx / magnitude;
            output.Y[index] = dy / magnitude;
            output.Bits[index] = (byte)(1 << OrientationMaps.Quantize(dx, dy));
        }
        else
        {
            output.X[index] = 0;
            output.Y[index] = 0;
            output.Bits[index] = 0;
        }
    }
}
