using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using VisionAlgorithm.OpenSrc.PatMax;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Matching;

public interface IPatMaxMatchingService
{
    Task<PatMaxTrainedTemplate> TrainAsync(
        ImageReference image,
        bool live,
        RoiGeometry roi,
        PatMaxTrainParams trainParams,
        CancellationToken token,
        RoiExclusion? exclusion = null);

    Task<PatMaxTrainedTemplate?> PreviewAsync(
        ImageReference image,
        bool live,
        RoiGeometry roi,
        PatMaxTrainParams trainParams,
        CancellationToken token,
        RoiExclusion? exclusion = null);

    Task<PatMaxSearchOutput> SearchAsync(
        ImageReference image,
        bool live,
        PatMaxTrainedTemplate template,
        RoiGeometry? searchRegion,
        PatMaxSearchParams searchParams,
        CancellationToken token,
        RoiExclusion? exclusion = null);
}
