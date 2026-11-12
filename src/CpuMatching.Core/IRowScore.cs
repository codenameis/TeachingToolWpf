namespace CpuMatching.Core;

/// <summary>연속 CPU 응답 맵을 한 행씩 누적할 수 있는 점수 구현의 선택형 계약.</summary>
public interface IRowScore
{
    /// <summary>각 호출이 독립 버퍼를 사용할 때만 허용한다. 기존 구현은 순차 호출을 유지한다.</summary>
    bool SupportsParallelRows => false;

    void ScoreRow(int y, Span<double> output);
}
