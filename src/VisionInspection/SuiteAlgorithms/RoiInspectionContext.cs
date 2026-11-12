using VisionAlgorithm.OpenSrc.Common;
using VisionOcr.PpOcr;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 개별 ROI 스위트 알고리즘 실행 문맥(영상 리더, 해상도, 대상/기준 ROI, 파라미터, 작업 버퍼, 취소 토큰).
/// </summary>
public sealed class RoiInspectionContext
{
    internal RoiInspectionContext(
        MatchingImageReader reader,
        int imageWidth,
        int imageHeight,
        IReadOnlyList<InspectionSuiteRoiInput> targetRois,
        IReadOnlyList<InspectionSuiteRoiInput> referenceRois,
        InspectionSuiteParameters parameters,
        InspectionWorkspace workspace,
        IPpOcrInferenceService? ppOcrService,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(targetRois);
        ArgumentNullException.ThrowIfNull(referenceRois);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(imageWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(imageHeight);

        Reader = reader;
        ImageWidth = imageWidth;
        ImageHeight = imageHeight;
        TargetRois = targetRois;
        ReferenceRois = referenceRois;
        Parameters = parameters;
        Workspace = workspace;
        PpOcrService = ppOcrService;
        CancellationToken = cancellationToken;
    }

    internal MatchingImageReader Reader { get; }

    public int ImageWidth { get; }

    public int ImageHeight { get; }

    public IReadOnlyList<InspectionSuiteRoiInput> TargetRois { get; }

    public IReadOnlyList<InspectionSuiteRoiInput> ReferenceRois { get; }

    public InspectionSuiteParameters Parameters { get; }

    public InspectionWorkspace Workspace { get; }

    public IPpOcrInferenceService? PpOcrService { get; }

    public CancellationToken CancellationToken { get; }
}
