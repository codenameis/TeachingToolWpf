using System.Collections.Immutable;
using System.Numerics;
using TeachingToolWpf.Core.Geometry;
using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Rendering;
using VisionAlgorithm.OpenSrc.PatternMatching;

namespace TeachingToolWpf.Presentation;

/// <summary>알고리즘 결과를 공급자와 무관한 캔버스 표시 값으로 변환한다.</summary>
internal static class InspectionResultPresentation
{
    public static ImmutableArray<MatchMarker> Markers(RoiGeometry teachingRegion, TemplateSearchOutput output)
    {
        var color = Color(InspectionDecision.Evaluate(output).Verdict);
        return output.Matches.Select((match, index) =>
        {
            var referencePoint = new PointD(match.ReferencePoint.X, match.ReferencePoint.Y);
            // 고정 방향 템플릿은 crop AABB가 아니라 학습 도형을 평행 이동한다.
            var detectedRegion = teachingRegion.WithFrame(referencePoint, teachingRegion.Width, teachingRegion.Height, teachingRegion.Angle);
            return new MatchMarker(detectedRegion.Outline.ToImmutableArray(), referencePoint, teachingRegion.Angle * Math.PI / 180,
                $"#{index + 1} NCC {match.Score:F3}", color, []);
        }).ToImmutableArray();
    }

    public static string Name(InspectionVerdict verdict) => verdict switch
    {
        InspectionVerdict.Pass => "OK · 정상",
        InspectionVerdict.Fail => "NG · 불량",
        InspectionVerdict.Warning => "WARN · 경고",
        InspectionVerdict.Review => "CHECK · 사용자 확인",
        _ => throw new ArgumentOutOfRangeException(nameof(verdict))
    };

    public static string Hex(InspectionVerdict verdict) => verdict switch
    {
        InspectionVerdict.Pass => "#32CD32",
        InspectionVerdict.Fail => "#FF6347",
        InspectionVerdict.Warning => "#FFA500",
        InspectionVerdict.Review => "#FFD700",
        _ => throw new ArgumentOutOfRangeException(nameof(verdict))
    };

    public static Vector4 Color(InspectionVerdict verdict) => verdict switch
    {
        InspectionVerdict.Pass => new(50 / 255f, 205 / 255f, 50 / 255f, 1),
        InspectionVerdict.Fail => new(1, 99 / 255f, 71 / 255f, 1),
        InspectionVerdict.Warning => new(1, 165 / 255f, 0, 1),
        InspectionVerdict.Review => new(1, 215 / 255f, 0, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(verdict))
    };

    public static ImmutableArray<MatchMarker> Markers(ShapeModel model, RoiGeometry teachingRegion, ShapeSearchOutput output)
    {
        var warning = InspectionDecision.Evaluate(output).Verdict == InspectionVerdict.Warning;
        return output.Matches.Select((match, index) =>
        {
            var verdict = warning ? InspectionVerdict.Warning : InspectionDecision.Evaluate(match).Verdict;
            var pose = match.Pose;
            var angle = RoiGeometry.NormalizeAngle(pose.Angle * 180 / Math.PI);
            var cosine = Math.Cos(pose.Angle) * pose.Scale;
            var sine = Math.Sin(pose.Angle) * pose.Scale;
            // 가장 세밀한 학습 특징을 원본 좌표로 옮긴다. 검출 영상에서 재측정한 대응점은 아니다.
            var features = model.Levels[0].Features.Select(feature => new PointD(
                pose.X + (cosine * feature.X) - (sine * feature.Y),
                pose.Y + (sine * feature.X) + (cosine * feature.Y))).ToImmutableArray();
            // NMS용 마스크 AABB는 실제 ROI 윤곽과 다르다. 절대각을 한 번만 적용한다.
            var detectedRegion = teachingRegion.WithFrame(new(pose.X, pose.Y),
                teachingRegion.Width * pose.Scale, teachingRegion.Height * pose.Scale, angle);
            return new MatchMarker(
                detectedRegion.Outline.ToImmutableArray(),
                new(pose.X, pose.Y), pose.Angle,
                $"#{index + 1} 점수 {match.Score:F3}",
                Color(verdict), features);
        }).ToImmutableArray();
    }
}
