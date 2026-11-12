namespace VisionAlgorithm.OpenSrc.PatternMatching;

/// <summary>
/// Gray8의 경계 복제 [1,4,6,4,1]/16 평활화 두 패스와 Sobel gradient를 계산한다.
/// 장치와 작업 버퍼는 호출자가 소유하며 실패를 다른 실행 경로로 대체하지 않는다.
/// </summary>
public interface IImagePreprocessor : IDisposable
{
    ImagePreprocessingDiagnostics Diagnostics
    {
        get;
    }

    /// <summary>
    /// 입력을 변경하지 않고 Width×Height 평탄 배열의 소유권을 반환한다. 장치 작업 종료 전에
    /// 반환·취소하거나 임시 버퍼를 재사용하지 않는다. NMS와 영역 마스크는 알고리즘이 적용한다.
    /// </summary>
    GradientPlanes Prepare(ShapeGrayImage image, float minimumGradient, CancellationToken cancellationToken = default);
}
