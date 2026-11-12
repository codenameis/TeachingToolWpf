namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 8K 전수 통합 합성 검증(<c>Master 8K Suite</c>) 실행을 추상화하는 인터페이스.
/// 제품 어셈블리가 합성 테스트 생성기(<c>VisionAlgorithm.Verification</c>)를 직접 참조하지 않도록 분리한다.
/// </summary>
public interface IMaster8kSuiteRunner
{
    Task<InspectionSuiteExecutionOutput> RunAsync(
        CancellationToken cancellationToken,
        int unifiedSheetWidth = DefaultUnifiedSheetWidth,
        int unifiedSheetHeight = DefaultUnifiedSheetHeight);

    const int DefaultUnifiedSheetWidth = 7680;
    const int DefaultUnifiedSheetHeight = 4320;
}
