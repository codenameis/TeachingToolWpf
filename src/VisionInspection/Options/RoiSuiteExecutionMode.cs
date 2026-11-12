namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// ROI별 명시적 스위트 세부 실행 모드.
/// 알고리즘 실행 경로는 ROI 표시 이름(<c>Name</c>) 문자열을 읽지 않고 이 구조체 값만 참조한다.
/// </summary>
public readonly record struct RoiSuiteExecutionMode(
    MetrologyMeasurementMode MetrologyDefaultMode = MetrologyMeasurementMode.EdgePairWidth,
    ShadingFilterVariant ShadingVariant = ShadingFilterVariant.PolynomialShadingAndReconstruction,
    AdvancedMetrologyVariant AdvancedMetrology = AdvancedMetrologyVariant.FitzgibbonEllipse,
    AdvancedBlobVariant AdvancedBlob = AdvancedBlobVariant.TopologyAndMinAreaBox,
    FrequencyFilterVariant FrequencyFilter = FrequencyFilterVariant.FftPeriodicNotch,
    FewShotSampleRole FewShotRole = FewShotSampleRole.Unassigned)
{
    public static RoiSuiteExecutionMode Default => new();
}
