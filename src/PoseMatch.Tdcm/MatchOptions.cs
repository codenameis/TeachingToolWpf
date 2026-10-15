namespace PoseMatch.Tdcm;

public sealed record MatchOptions
{
    public float MinimumScore { get; init; } = .5f;
    public int MaximumMatches { get; init; } = 256;
    public int MaximumCandidates { get; init; } = 4096;
    public int SuppressionRadius { get; init; } = 18;
    public int CenterRadius { get; init; } = 15;
    public bool RefinePose
    {
        get; init;
    }

    public void Validate()
    {
        if (!float.IsFinite(MinimumScore) || MinimumScore is <= 0 or > 1 ||
            MaximumMatches is < 1 or > 100000 || MaximumCandidates < MaximumMatches || MaximumCandidates > 100000 ||
            SuppressionRadius is < 1 or > 144 || CenterRadius is < 0 or > 72)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumScore), "점수 (0,1], 결과 1~100000, 후보 결과 수~100000, 억제 반경 1~144, 중심 반경 0~72가 필요합니다.");
        }
    }
}

/// <summary>각도는 영상 좌표의 시계 방향. 폭/높이는 독립 배율을 보존한다.</summary>
public sealed record PoseMatchResult(double X, double Y, double Width, double Height, double AngleDegrees, float Score);
public sealed record MatchOutput(IReadOnlyList<PoseMatchResult> Matches, bool CandidatesTruncated, bool MatchLimitExceeded);
public sealed record Prediction(int Width, int Height, float[] Score, float[] Sign, float[] Cos, float[] ScaleX, float[] ScaleY);
