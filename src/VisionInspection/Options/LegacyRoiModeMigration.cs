namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 기존 레시피 문서나 구형 프리셋 ROI 이름에 인코딩되어 있던 동작 힌트를
/// 로드 시점(1회성)에 명시적 <see cref="RoiSuiteExecutionMode"/>로 이관하는 마이그레이터.
/// </summary>
public static class LegacyRoiModeMigration
{
    public static RoiSuiteExecutionMode FromRoiName(string? roiName)
    {
        if (string.IsNullOrWhiteSpace(roiName))
        {
            return RoiSuiteExecutionMode.Default;
        }

        MetrologyMeasurementMode metrologyMode = ContainsAny(roiName, "원", "Circle", "패드", "홀")
            ? MetrologyMeasurementMode.SpokeCircleFit
            : MetrologyMeasurementMode.EdgePairWidth;

        ShadingFilterVariant shadingVariant = ContainsAny(roiName, "확산", "Diffusion")
            ? ShadingFilterVariant.AnisotropicDiffusion
            : ContainsAny(roiName, "재구성", "Recon", "핀홀")
                ? ShadingFilterVariant.MorphologicalReconstruction
                : ShadingFilterVariant.PolynomialShadingAndReconstruction;

        AdvancedMetrologyVariant advancedMetrology = ContainsAny(roiName, "캘리퍼", "Caliper", "복합")
            ? AdvancedMetrologyVariant.CompositeCaliper
            : AdvancedMetrologyVariant.FitzgibbonEllipse;

        AdvancedBlobVariant advancedBlob = ContainsAny(roiName, "워터셰드", "Watershed", "접촉", "분리")
            ? AdvancedBlobVariant.WatershedSplit
            : AdvancedBlobVariant.TopologyAndMinAreaBox;

        FrequencyFilterVariant frequencyFilter = ContainsAny(roiName, "호모모픽", "Homomorphic")
            ? FrequencyFilterVariant.HomomorphicIllumination
            : ContainsAny(roiName, "가보", "Gabor", "텍스처")
                ? FrequencyFilterVariant.GaborTexture
                : FrequencyFilterVariant.FftPeriodicNotch;

        FewShotSampleRole fewShotRole = InferFewShotRole(roiName);

        return new RoiSuiteExecutionMode(
            MetrologyDefaultMode: metrologyMode,
            ShadingVariant: shadingVariant,
            AdvancedMetrology: advancedMetrology,
            AdvancedBlob: advancedBlob,
            FrequencyFilter: frequencyFilter,
            FewShotRole: fewShotRole);
    }

    public static FewShotSampleRole InferFewShotRole(string? roiName)
    {
        if (string.IsNullOrWhiteSpace(roiName))
        {
            return FewShotSampleRole.Unassigned;
        }

        if (ContainsAny(roiName, "학습-정상", "Train-Good"))
        {
            return FewShotSampleRole.TrainGood;
        }

        if (ContainsAny(roiName, "학습-불량", "Train-BackBad"))
        {
            return FewShotSampleRole.TrainDefect;
        }

        if (ContainsAny(roiName, "테스트", "Test"))
        {
            return FewShotSampleRole.TestTarget;
        }

        return FewShotSampleRole.Unassigned;
    }

    private static bool ContainsAny(string text, params ReadOnlySpan<string> tokens)
    {
        for (int i = 0; i < tokens.Length; i++)
        {
            if (text.Contains(tokens[i], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
