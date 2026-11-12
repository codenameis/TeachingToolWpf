namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 2D 다항식 조명 평탄화·비등방성 확산·형태학적 재구성 전처리 세부 변형 모드.
/// </summary>
public enum ShadingFilterVariant
{
    PolynomialShadingAndReconstruction = 0,
    AnisotropicDiffusion = 1,
    MorphologicalReconstruction = 2
}
