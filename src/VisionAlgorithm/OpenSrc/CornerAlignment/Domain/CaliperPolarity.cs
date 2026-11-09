// File: src/VisionAlgorithm/OpenSrc/CornerAlignment/Domain/CaliperPolarity.cs
namespace VisionAlgorithm.OpenSrc.CornerAlignment.Domain;

/// <summary>
/// 1D 프로젝션 캘리퍼의 에지 천이 극성(Polarity) 조건을 정의한다.
/// </summary>
public enum CaliperPolarity
{
    /// <summary>
    /// 어두운 영역에서 밝은 영역으로 상승하는 에지 (+1차 미분 극대).
    /// </summary>
    DarkToLight = 0,

    /// <summary>
    /// 밝은 영역에서 어두운 영역으로 하강하는 에지 (-1차 미분 극소).
    /// </summary>
    LightToDark = 1,

    /// <summary>
    /// 상승·하강 구분 없이 절대 기울기 진폭이 가장 큰 에지.
    /// </summary>
    Any = 2
}
