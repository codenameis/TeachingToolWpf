namespace CpuMatching.Core;

public readonly record struct SearchRegion(int X, int Y, int Width, int Height)
{
    public void Validate(GrayImage image)
    {
        if (X < 0 || Y < 0 || Width < 4 || Height < 4 || (long)X + Width > image.Width || (long)Y + Height > image.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(image), "Search region must be inside the image.");
        }
    }
}

/// <summary>픽셀 인덱스 기준 중심, 영상 시계 방향 도, 균일 배율.</summary>
public readonly record struct Match(double X, double Y, double AngleDegrees, double Scale, double Score);

public sealed record SearchOptions
{
    public double MinimumAngle { get; init; } = -180;
    public double MaximumAngle { get; init; } = 180;
    public double AngleStep { get; init; } = 10;
    public double MinimumScale { get; init; } = 1;
    public double MaximumScale { get; init; } = 1;
    public double ScaleStep { get; init; } = 0.1;
    public double MinimumScore { get; init; } = 0.75;
    public int MaximumMatches { get; init; } = 64;
    /// <summary>한 번에 정밀 검사할 후보 수. 초과 후보도 다음 묶음에서 모두 검사한다.</summary>
    public int CandidateLimit { get; init; } = 2048;
    public int PyramidLevels { get; init; } = 3;
    /// <summary>각 CPU 단계의 병렬 작업 상한. 여러 영역을 밖에서 병렬 검사할 때는 1로 둔다.</summary>
    public int MaximumDegreeOfParallelism { get; init; } = 4;
    public double SuppressionRadius { get; init; } = 8;
    public SearchRegion? Region
    {
        get; init;
    }

    public void Validate()
    {
        if (!double.IsFinite(MinimumAngle) || !double.IsFinite(MaximumAngle) || MinimumAngle < -360 || MaximumAngle > 360 ||
            MaximumAngle < MinimumAngle || MaximumAngle - MinimumAngle > 360 || !double.IsFinite(AngleStep) || AngleStep is < 0.25 or > 30 ||
            !double.IsFinite(MinimumScale) || !double.IsFinite(MaximumScale) || MinimumScale < 0.5 || MaximumScale > 2 || MaximumScale < MinimumScale ||
            !double.IsFinite(ScaleStep) || ScaleStep is < 0.01 or > 0.5 || !double.IsFinite(MinimumScore) || MinimumScore is <= 0 or > 1 ||
            MaximumMatches is < 1 or > 4096 || CandidateLimit is < 16 or > 16384 || PyramidLevels is < 1 or > 5 ||
            MaximumDegreeOfParallelism is < 1 or > 64 ||
            !double.IsFinite(SuppressionRadius) || SuppressionRadius < 1 ||
            (((MaximumAngle - MinimumAngle) / AngleStep) + 1) * (((MaximumScale - MinimumScale) / ScaleStep) + 1) > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(SearchOptions));
        }
    }
}

public sealed record SearchResult(IReadOnlyList<Match> Matches, bool CandidatesTruncated, bool MatchLimitExceeded, long Evaluations)
{
    public long CandidateCount
    {
        get; init;
    }
    public int CandidateBatches
    {
        get; init;
    }

    public SearchTimings? Timings
    {
        get; init;
    }
}

/// <summary>같은 검색 안의 경과 시간(ms). 준비·전체 후보·정밀 보정·원본 검증·최종 중복 제거를 구분한다.</summary>
public sealed record SearchTimings(double Preparation, double CoarseSearch, double Refinement, double Verification, double Suppression);

public interface ITemplateMatcher
{
    /// <summary>
    /// acceptCenter는 입력 영상의 픽셀 인덱스 좌표로 최종 중심을 검사하며, 최종 NMS/출력 제한 전에 적용한다.
    /// accelerator의 수명은 호출자가 소유한다. 생략하면 기존 CPU 경로이며 미지원 구현은 명시적으로 거부한다.
    /// </summary>
    SearchResult Find(GrayImage image, SearchOptions? options = null, CancellationToken cancellationToken = default,
        Func<double, double, bool>? acceptCenter = null, ICoarseScoreAccelerator? accelerator = null);
}

/// <summary>세 CPU 알고리즘의 공통 피라미드 검색에 쓰이는 레벨별 점수 전략.</summary>
public interface IScoreLevel
{
    /// <summary>AtPose/Verify와 반환된 Score가 읽기 전용일 때만 허용한다. 행 병렬화는 IRowScore에서 별도로 허용한다.</summary>
    bool SupportsParallelEvaluation => false;

    IPoseScore AtPose(double angleDegrees, double scale);

    /// <summary>최종 자세에서 원본 검증 점수를 구한다. 후보 검색 점수와 별도의 검증을 둘 수 있다.</summary>
    double Verify(Match match) => AtPose(match.AngleDegrees, match.Scale).Score(match.X, match.Y);
}

public interface IPoseScore
{
    double Score(double x, double y);
}

/// <summary>연속 CPU 응답 맵을 한 행씩 누적할 수 있는 점수 구현의 선택형 계약.</summary>
public interface IRowScore
{
    /// <summary>각 호출이 독립 버퍼를 사용할 때만 허용한다. 기존 구현은 순차 호출을 유지한다.</summary>
    bool SupportsParallelRows => false;

    void ScoreRow(int y, Span<double> output);
}
