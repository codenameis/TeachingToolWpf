namespace CpuMatching.Core;

/// <summary>GPU 라이브러리 참조 없이, 이 자세의 전체 위치 점수를 가속기에 전달한다.</summary>
public interface IAcceleratedPoseScore
{
    void ScoreGrid(ICoarseScoreAccelerator accelerator, double[] output, CancellationToken token);
}
