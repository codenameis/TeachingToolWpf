using TeachingToolWpf.Imaging.Matching;
using VisionAlgorithm.OpenSrc.PatternMatching;

namespace TeachingToolWpf.Models;

public enum InspectionVerdict
{
    Pass,
    Fail,
    Warning,
    Review
}

/// <summary>패턴 존재와 선택형 회전 검사 정책. 오류·불완전 검색은 제품 불량으로 판정하지 않는다.</summary>
public sealed record InspectionDecision(InspectionVerdict Verdict, string Reason)
{
    private const string OutputLimitWarning = "출력 상한 초과 · 추가 검출 결과를 생략했습니다. 최대 결과 수를 늘려 다시 검사하세요.";

    public static InspectionDecision Evaluate(TemplateSearchOutput output)
    {
        var warnings = new List<string>();
        if (output.Truncated)
        {
            warnings.Add("후보 예산 초과 · OpenCV 검색 불완전");
        }
        if (output.MatchLimitExceeded)
        {
            warnings.Add(OutputLimitWarning);
        }
        if (output.Windows == 0)
        {
            warnings.Add("템플릿이 들어갈 검색 영역이 없습니다.");
        }
        if (warnings.Count != 0)
        {
            return new(InspectionVerdict.Warning, string.Join(Environment.NewLine, warnings));
        }
        return output.Matches.IsEmpty
            ? new(InspectionVerdict.Fail, "기준 NCC 점수를 만족하는 패턴이 없습니다.")
            : new(InspectionVerdict.Pass, "OpenCV 템플릿 위치 검출 완료");
    }

    public static InspectionDecision Evaluate(ShapeSearchOutput output, RotationAcceptance? rotation = null, double referenceAngle = 0)
    {
        var warnings = new List<string>();
        if (output.Truncated)
        {
            warnings.Add("검색 예산 초과 · 일부 후보를 확인하지 못했습니다.");
        }
        if (output.MatchLimitExceeded)
        {
            warnings.Add(OutputLimitWarning);
        }
        if (output.Windows == 0)
        {
            warnings.Add("검색된 영역이 없습니다.");
        }
        if (warnings.Count != 0)
        {
            return new(InspectionVerdict.Warning, string.Join(Environment.NewLine, warnings));
        }
        if (output.Matches.IsEmpty)
        {
            return new(InspectionVerdict.Fail, "설정 점수·지지율·기하 검증 조건을 만족하는 패턴이 없습니다.");
        }
        var decisions = output.Matches.Select(match => Evaluate(match, rotation, referenceAngle)).ToArray();
        var failed = decisions.Count(value => value.Verdict == InspectionVerdict.Fail);
        if (failed > 0)
        {
            return new(InspectionVerdict.Fail, $"회전 허용각 초과 {failed}개 / 검출 {output.Matches.Length}개 · 정상 편차 {rotation!.MinimumDegrees:G}~{rotation.MaximumDegrees:G}°" +
                (decisions.Any(value => value.Verdict == InspectionVerdict.Review) ? " · 자세 미확정 결과는 개별 CHECK 확인" : ""));
        }
        var review = decisions.FirstOrDefault(value => value.Verdict == InspectionVerdict.Review);
        return review ?? new(InspectionVerdict.Pass, "패턴 검출 및 위치 정밀화 완료");
    }

    public static InspectionDecision Evaluate(ShapeMatch match, RotationAcceptance? rotation = null, double referenceAngle = 0)
    {
        if (match.Geometry is { } geometry && (geometry.MatchedFeatures == 0 || !double.IsFinite(geometry.FitErrorPixels)))
        {
            return new(InspectionVerdict.Review, "실제 윤곽 대응의 위치 오차를 측정할 수 없습니다. 검출 위치를 확인하세요.");
        }
        if (!match.Refinement.AngleObservable)
        {
            return new(InspectionVerdict.Review, "회전각을 확정하기 어려운 패턴입니다. 위치·방향을 확인하세요.");
        }
        if (!double.IsFinite(match.Pose.Angle))
        {
            return new(InspectionVerdict.Review, "회전각이 유효하지 않습니다. 방향을 확인하세요.");
        }
        if (rotation is not null && !rotation.Contains(match.Pose.Angle, referenceAngle))
        {
            return new(InspectionVerdict.Fail,
                $"회전 허용각 초과 · 학습 대비 {RotationAcceptance.DeviationDegrees(match.Pose.Angle, referenceAngle):F3}° · 정상 {rotation.MinimumDegrees:G}~{rotation.MaximumDegrees:G}°");
        }
        return match.Refinement.Status == RefinementStatus.Converged
            ? new(InspectionVerdict.Pass, "위치 정밀화 완료")
            : new(InspectionVerdict.Review, $"위치 정밀화 미확정 ({match.Refinement.Status}) · 검출 위치를 확인하세요.");
    }
}
