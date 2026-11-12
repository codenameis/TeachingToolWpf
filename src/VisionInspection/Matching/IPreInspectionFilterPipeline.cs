using System.Collections.Immutable;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// Contract for executing the ordered pre-inspection grayscale preprocessing filter pipeline
/// before core vision inspection or training algorithms run.
/// </summary>
public interface IPreInspectionFilterPipeline
{
    ImmutableArray<PreInspectionFilterStep> ActiveSteps { get; }

    bool HasEnabledSteps { get; }

    long TotalExecutions { get; }

    double LastElapsedMilliseconds { get; }

    void SetActivePipeline(ImmutableArray<PreInspectionFilterStep> steps);

    void ClearActivePipeline();

    int ApplyInPlaceIfConfigured(Span<byte> pixels, int width, int height);
}
