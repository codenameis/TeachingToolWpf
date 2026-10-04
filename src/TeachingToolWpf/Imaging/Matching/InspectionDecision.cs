using VisionAlgorithm.OpenSrc.PatternMatching;

namespace TeachingToolWpf.Imaging.Matching;

public enum InspectionVerdict
{
    Pass,
    Fail,
    Warning,
    Review
}

/// <summary>패턴 존재 검사 정책. 오류·불완전 검색은 제품 불량으로 판정하지 않는다.</summary>
public sealed record InspectionDecision(InspectionVerdict Verdict, string Reason)
{
    public static InspectionDecision Evaluate(ShapeSearchOutput output)
    {
        if (output.Truncated)
        {
            return new(InspectionVerdict.Warning, "검색 예산 초과 · 일부 후보를 확인하지 못했습니다.");
        }
        if (output.Windows == 0)
        {
            return new(InspectionVerdict.Warning, "검색된 영역이 없습니다.");
        }
        if (output.Matches.IsEmpty)
        {
            return new(InspectionVerdict.Fail, "설정 점수·지지율을 만족하는 패턴이 없습니다.");
        }
        var review = output.Matches.Select(Evaluate).FirstOrDefault(value => value.Verdict == InspectionVerdict.Review);
        return review ?? new(InspectionVerdict.Pass, "패턴 검출 및 위치 정밀화 완료");
    }

    public static InspectionDecision Evaluate(ShapeMatch match)
    {
        if (!match.Refinement.AngleObservable)
        {
            return new(InspectionVerdict.Review, "회전각을 확정하기 어려운 패턴입니다. 위치·방향을 확인하세요.");
        }
        return match.Refinement.Status == RefinementStatus.Converged
            ? new(InspectionVerdict.Pass, "위치 정밀화 완료")
            : new(InspectionVerdict.Review, $"위치 정밀화 미확정 ({match.Refinement.Status}) · 검출 위치를 확인하세요.");
    }
}
