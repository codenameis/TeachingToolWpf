namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 2D FFT 주기적 노치·호모모픽 조명 분리·Gabor 텍스처 전처리 세부 변형 모드.
/// </summary>
public enum FrequencyFilterVariant
{
    FftPeriodicNotch = 0,
    HomomorphicIllumination = 1,
    GaborTexture = 2
}
