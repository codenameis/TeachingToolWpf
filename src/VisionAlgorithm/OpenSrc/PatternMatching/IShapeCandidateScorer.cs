using System.Collections.Immutable;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

/// <summary>
/// 양자화 방향 영상의 확산·방향 응답과 여러 템플릿의 정수 점수 격자를 한 번에 계산한다.
/// 후보 선택·예산·연속 방향 검증은 공통 알고리즘이 소유하며 장치 오류를 숨기지 않는다.
/// </summary>
public interface IShapeCandidateScorer
{
    CandidateScoringDiagnostics CandidateDiagnostics
    {
        get;
    }

    /// <summary>
    /// 반환 배열은 호출자가 소유한다. 배열/장치 버퍼를 반환하거나 재사용하기 전에 실행을 완료하며,
    /// 취소도 제출된 작업의 완료를 확인한 뒤 보고한다.
    /// </summary>
    ShapeCandidateScoreBatch Score(ShapeCandidateScoreRequest request, CancellationToken cancellationToken = default);
}

/// <summary>기준점에서의 정수 픽셀 오프셋과 unsigned 8방향 bin(0..7).</summary>
public readonly record struct CandidateScoreFeature(int X, int Y, int Bin);

/// <summary>
/// OrientationBits는 Width×Height 행 우선 방향 bit이고, 응답은 정사각 radius 확산 후
/// max(abs(cos((bin-other)*π/8)))×100을 짝수 반올림한다. 격자는 (x*Step,y*Step)에서 시작한다.
/// 영상 밖 특징의 점수는 0이며, 템플릿마다 1..512개 특징을 모두 더해 ushort로 반환한다.
/// 한 배치의 반환 점수는 32 MiB 이하이며 초과 템플릿은 호출자가 순서를 보존해 나눠 요청한다.
/// </summary>
public sealed record ShapeCandidateScoreRequest(ReadOnlyMemory<byte> OrientationBits, int Width, int Height,
    int Radius, int Step, ImmutableArray<ImmutableArray<CandidateScoreFeature>> Templates)
{
    public const int MaximumPixels = 2_097_152;
    public const int MaximumScoreCount = 16_777_216;

    public int GridWidth => (Width + Step - 1) / Step;
    public int GridHeight => (Height + Step - 1) / Step;

    public void Validate()
    {
        if (Width < 1 || Height < 1 || (long)Width * Height > MaximumPixels ||
            OrientationBits.Length != (long)Width * Height || Radius is < 0 or > 8 || Step is < 1 or > 16 ||
            Templates.IsDefaultOrEmpty || Templates.Length > 16384)
        {
            throw new ArgumentException("후보 점수 입력 크기·방향 영상·템플릿 또는 확산 설정이 잘못되었습니다.");
        }
        if ((long)GridWidth * GridHeight * Templates.Length > MaximumScoreCount)
        {
            throw new ArgumentException("후보 점수 배치가 32 MiB 반환 예산을 초과했습니다. 템플릿 배치를 나눠 요청하십시오.");
        }
        foreach (var template in Templates)
        {
            if (template.IsDefaultOrEmpty || template.Length > 512)
            {
                throw new ArgumentException("후보 점수 템플릿에는 1..512개 특징이 필요합니다.");
            }
            foreach (var feature in template)
            {
                if (feature.Bin is < 0 or > 7 || feature.X is < -1_073_741_824 or > 1_073_741_824 ||
                    feature.Y is < -1_073_741_824 or > 1_073_741_824)
                {
                    throw new ArgumentException("후보 점수 특징의 방향 또는 좌표 범위가 잘못되었습니다.");
                }
            }
        }
    }
}

/// <summary>템플릿 순서 → 격자 행 → 격자 열 순서의 정수 합. 임계값과 NMS는 아직 적용하지 않는다.</summary>
public sealed record ShapeCandidateScoreBatch(int GridWidth, int GridHeight, int TemplateCount, ushort[] Scores);

/// <summary>후보 점수 계산의 전송·GPU 대기·반환을 포함한 누적 wall time. 전체 검사 시간과 구별한다.</summary>
public sealed record CandidateScoringDiagnostics(string Backend, string AdapterName, long Calls,
    long ProcessedPixels, long TemplateCount, double TotalMilliseconds);
