namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 서브픽셀 캘리퍼·기하 계측(Metrology) 세부 동작 모드.
/// </summary>
public enum MetrologyMeasurementMode
{
    Auto = 0,
    EdgePairWidth = 1,
    RakeLineFit = 2,
    SpokeCircleFit = 3
}
