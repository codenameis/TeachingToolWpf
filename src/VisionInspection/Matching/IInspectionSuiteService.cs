using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 검사 스위트 ROI 실행 및 알고리즘 레지스트리 조회 서비스 계약.
/// </summary>
public interface IInspectionSuiteService
{
    InspectionAlgorithmRegistry Registry => InspectionAlgorithmRegistry.CreateDefault();

    bool IsPpOcrWired => false;

    string PpOcrStatusSummary => "PP-OCR 미연결";

    Task<InspectionSuiteExecutionOutput> ExecuteRoiInspectionAsync(
        ImageReference image,
        bool live,
        InspectionSuiteAlgorithmKind algorithm,
        IReadOnlyList<InspectionSuiteRoiInput> targetRois,
        IReadOnlyList<InspectionSuiteRoiInput> referenceRois,
        InspectionSuiteParameters parameters,
        CancellationToken token);

    Task<InspectionSuiteExecutionOutput> RunMaster8kSuiteAsync(CancellationToken token);
}
