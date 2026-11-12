namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 캘리퍼 에지 검출 극성(Polarity) 조건.
/// </summary>
public enum MetrologyPolarityMode
{
    Any = 0,
    BrightStripe = 1,
    DarkSlot = 2,
    DarkToLight = 3,
    LightToDark = 4
}
