using System.Collections.Immutable;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

public sealed record TrainingOptions
{
    public int PyramidLevels { get; init; } = 3;
    public int MaximumFeatures { get; init; } = 160;
    public float MinimumGradient { get; init; } = 8;
    /// <summary>산포 특징 후보의 최소 강도/학습 최대 강도 비율. 전체 윤곽과 마스크는 생략하지 않는다.</summary>
    public double MinimumFeatureContrastRatio { get; init; } = 0.2;
    /// <summary>학습 ROI 방향에서 더한 시계 방향 회전 편차의 최솟값.</summary>
    public double MinimumAngleDegrees { get; init; } = -30;
    /// <summary>학습 ROI 방향에서 더한 시계 방향 회전 편차의 최댓값.</summary>
    public double MaximumAngleDegrees { get; init; } = 30;
    public double AngleStepDegrees { get; init; } = 5;
    public double MinimumScale { get; init; } = 1;
    public double MaximumScale { get; init; } = 1;
    public double ScaleStep { get; init; } = 0.1;
    public int MaximumTemplates { get; init; } = 2048;
    public ContrastPolarity Polarity { get; init; } = ContrastPolarity.Preserve;
    /// <summary>한 단계만 남은 작은 논문 형상의 원본 gradient 세부 검증. 추가 윤곽 감점과 함께 적용한다.</summary>
    public bool UseFineDetailValidation
    {
        get; init;
    }
    /// <summary>추가 윤곽 점수에 필요한 전체 윤곽과 마스크를 보관한다.</summary>
    public bool BuildClutterModel
    {
        get; init;
    }

    public void Validate()
    {
        if (PyramidLevels is < 1 or > 6 || MaximumFeatures is < 12 or > 512 ||
            !float.IsFinite(MinimumGradient) || MinimumGradient <= 0 ||
            !double.IsFinite(MinimumFeatureContrastRatio) || MinimumFeatureContrastRatio is < 0 or > 1 ||
            !double.IsFinite(MinimumAngleDegrees) || !double.IsFinite(MaximumAngleDegrees) ||
            MaximumAngleDegrees < MinimumAngleDegrees || MaximumAngleDegrees - MinimumAngleDegrees > 360 ||
            !double.IsFinite(AngleStepDegrees) || AngleStepDegrees <= 0 || AngleStepDegrees > 45 ||
            !double.IsFinite(MinimumScale) || !double.IsFinite(MaximumScale) ||
            MinimumScale <= 0 || MaximumScale < MinimumScale || MaximumScale > 8 ||
            !double.IsFinite(ScaleStep) || ScaleStep <= 0 || MaximumTemplates is < 1 or > 16384 ||
            !Enum.IsDefined(Polarity))
        {
            throw new ArgumentOutOfRangeException(nameof(TrainingOptions));
        }
        var angles = Math.Ceiling((MaximumAngleDegrees - MinimumAngleDegrees) / AngleStepDegrees) + 1;
        var scales = Math.Ceiling((MaximumScale - MinimumScale) / ScaleStep) + 1;
        if (angles * scales > MaximumTemplates)
        {
            throw new ArgumentException("회전·배율 템플릿 예산을 초과했습니다. 범위 또는 간격을 조정하십시오.");
        }
    }
}
