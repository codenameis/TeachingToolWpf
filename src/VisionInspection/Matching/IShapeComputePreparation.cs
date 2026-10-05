namespace TeachingToolWpf.Imaging.Matching;

/// <summary>장치·커널의 최초 준비와 실제 이미지 검사를 구별한다. 준비 실패는 검사 실패로 전파한다.</summary>
public interface IShapeComputePreparation
{
    Task<double> PrepareGpuAsync(CancellationToken cancellationToken);
}
