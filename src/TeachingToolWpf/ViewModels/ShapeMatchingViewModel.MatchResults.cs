using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionGeometry;

namespace TeachingToolWpf.ViewModels;

public sealed partial class ShapeMatchingViewModel
{
    public double MinimumSupport
    {
        get => ActiveSearchSettings.Options.MinimumSupport;
        set => ChangeSearchOptions(ActiveSearchSettings.Options with { MinimumSupport = value });
    }

    public double ClutterTolerancePixels
    {
        get => ActiveSearchSettings.Options.ClutterTolerancePixels;
        set => ChangeSearchOptions(ActiveSearchSettings.Options with { ClutterTolerancePixels = value });
    }

    private SearchOptions CreateShapeSearchOptions() => ActiveSearchSettings.Options;

    private static string FormatShapeMatch(ShapeMatch match, int index)
        => $"{index + 1} · X {match.Pose.X:F3} / Y {match.Pose.Y:F3}\n" +
            $"각도 {RoiGeometry.NormalizeAngle(match.Pose.Angle * 180 / Math.PI):F3}° · 배율 {match.Pose.Scale:F4}\n" +
            $"최종 점수 {match.Score:F3} · 방향 점수 {match.DirectionScore:F3}\n" +
            $"방향 지지율 {match.Support:P0}\n" +
            (match.DetailConsistency is { } detail ? $"원본 세부 획 일치 {detail:P1} · 원본 gradient 자세 검증\n" : "") +
            $"추가 윤곽 감점(대비) {(match.ClutterPenaltyRatio is { } penalty ? penalty.ToString("P1") : "미계산")} · " +
            $"개수 {(match.ClutterRatio is { } clutter ? clutter.ToString("P1") : "미계산")}\n" +
            (match.DetailConsistency is not null ? "세부 획 검증 · point-to-line 정밀화 미적용" :
                $"정밀화 {match.Refinement.Status} · 회전 관측 {(match.Refinement.AngleObservable ? "가능" : "불가")}");
}
