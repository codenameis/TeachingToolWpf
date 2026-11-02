namespace TeachingToolWpf.Models;

/// <summary>학습 방향에 대한 정상 편차. 탐색 범위와 독립적이며 주기 경계를 넘는 범위도 허용한다.</summary>
public sealed record RotationAcceptance(double MinimumDegrees = 0, double MaximumDegrees = 10)
{
    public void Validate()
    {
        if (!double.IsFinite(MinimumDegrees) || !double.IsFinite(MaximumDegrees) ||
            MinimumDegrees < -360 || MaximumDegrees > 360 || MaximumDegrees < MinimumDegrees ||
            MaximumDegrees - MinimumDegrees > 360)
        {
            throw new ArgumentException("정상 회전 편차는 -360~360° 안에서 최소 ≤ 최대, 범위 360° 이하로 입력하세요.");
        }
    }

    public static double DeviationDegrees(double angleRadians, double referenceRadians)
    {
        var degrees = (angleRadians - referenceRadians) * 180 / Math.PI;
        return degrees - (360 * Math.Floor((degrees + 180) / 360));
    }

    public bool Contains(double angleRadians, double referenceRadians)
    {
        Validate();
        var deviation = DeviationDegrees(angleRadians, referenceRadians);
        var equivalent = deviation + (360 * Math.Ceiling((MinimumDegrees - deviation - 1e-9) / 360));
        return double.IsFinite(equivalent) && equivalent <= MaximumDegrees + 1e-9;
    }
}
