namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 소량 샘플 분류(Edge Few-Shot) 프리셋 ROI 학습 역할 구분.
/// </summary>
public enum FewShotSampleRole
{
    Unassigned = 0,
    TrainGood = 1,
    TrainDefect = 2,
    TestTarget = 3
}
