using System.Collections.Immutable;
using System.Diagnostics;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Matching;

public interface ITemplateMatchingService
{
    Task<TemplateTrainingOutput> TrainAsync(ImageReference image, bool live, RoiGeometry roi, CancellationToken token, RoiExclusion? exclusion = null);
    Task<TemplateSearchOutput> SearchAsync(ImageReference image, bool live, CorrelationTemplate model, RoiGeometry? region, ShapeSearchOptions options, CancellationToken token, RoiExclusion? exclusion = null);
}
