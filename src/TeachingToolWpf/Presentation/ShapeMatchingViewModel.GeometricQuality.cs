using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionGeometry;

namespace TeachingToolWpf.Presentation;

public sealed partial class ShapeMatchingViewModel
{
    public double MinimumSupport
    {
        get => ActiveSearchSettings.Options.MinimumSupport;
        set => ChangeSearchOptions(ActiveSearchSettings.Options with { MinimumSupport = value });
    }

    public double GeometricTolerancePixels
    {
        get => ActiveSearchSettings.Options.GeometricTolerancePixels;
        set => ChangeSearchOptions(ActiveSearchSettings.Options with { GeometricTolerancePixels = value });
    }

    public double MinimumCoverage
    {
        get => ActiveSearchSettings.Options.MinimumCoverage;
        set => ChangeSearchOptions(ActiveSearchSettings.Options with { MinimumCoverage = value });
    }

    public bool LimitFitError
    {
        get => ActiveSearchSettings.LimitFitError;
        set => ChangeSearchSettings(ActiveSearchSettings with { LimitFitError = value });
    }

    public double MaximumFitErrorPixels
    {
        get => ActiveSearchSettings.MaximumFitErrorPixels;
        set => ChangeSearchSettings(ActiveSearchSettings with { MaximumFitErrorPixels = value });
    }

    public bool LimitClutter
    {
        get => ActiveSearchSettings.LimitClutter;
        set => ChangeSearchSettings(ActiveSearchSettings with { LimitClutter = value });
    }

    public double MaximumClutterRatio
    {
        get => ActiveSearchSettings.MaximumClutterRatio;
        set => ChangeSearchSettings(ActiveSearchSettings with { MaximumClutterRatio = value });
    }

    private SearchOptions CreateShapeSearchOptions() => ActiveSearchSettings.Options with
    {
        EvaluateGeometry = IsGeometricMethod,
        MinimumCoverage = IsGeometricMethod ? MinimumCoverage : 0,
        MaximumFitErrorPixels = IsGeometricMethod && LimitFitError ? MaximumFitErrorPixels : double.PositiveInfinity,
        MaximumClutterRatio = IsGeometricMethod && LimitClutter ? MaximumClutterRatio : 1
    };

    private static string FormatShapeMatch(ShapeMatch match, int index, bool geometric)
        => $"{index + 1} · X {match.Pose.X:F3} / Y {match.Pose.Y:F3}\n" +
            $"각도 {RoiGeometry.NormalizeAngle(match.Pose.Angle * 180 / Math.PI):F3}° · 배율 {match.Pose.Scale:F4}\n" +
            $"최종 점수 {match.Score:F3} · 방향 점수 {match.DirectionScore:F3}\n" +
            $"방향 지지율 {match.Support:P0}\n" +
            (match.DetailConsistency is { } detail ? $"원본 세부 획 일치 {detail:P1} · 원본 gradient 자세 검증\n" : "") +
            $"추가 윤곽 감점(대비) {(match.ClutterPenaltyRatio is { } penalty ? penalty.ToString("P1") : "미계산")} · " +
            $"개수 {(match.ClutterRatio is { } clutter ? clutter.ToString("P1") : "미계산")}\n" +
            (geometric ? FormatGeometry(match.Geometry) + "\n" : "") +
            (match.DetailConsistency is not null ? "세부 획 검증 · point-to-line 정밀화 미적용" :
                $"정밀화 {match.Refinement.Status} · 회전 관측 {(match.Refinement.AngleObservable ? "가능" : "불가")}");

    private static string FormatGeometry(GeometricMatchQuality? quality)
    {
        if (quality is null)
        {
            return "기하 검증 미계산";
        }
        var fit = quality.MatchedFeatures > 0 && double.IsFinite(quality.FitErrorPixels)
            ? $"{quality.FitErrorPixels:F3} px RMS" : "측정 불가";
        return $"윤곽 특징 검출비율 {quality.Coverage:P1} ({quality.MatchedFeatures}/{quality.ModelFeatures})\n" +
            $"위치 오차 {fit}\n불필요 윤곽비율(개수) {quality.ClutterRatio:P1} · 영역 에지 {quality.SceneEdges}개";
    }
}
