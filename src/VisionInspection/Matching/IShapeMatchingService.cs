using System.Collections.Immutable;
using System.Diagnostics;
using VisionAlgorithm;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Matching;

public interface IShapeMatchingService
{
    Task<ShapeTrainingOutput> TrainAsync(ImageReference image, bool live, RoiGeometry roi, TrainingOptions options, CancellationToken token, RoiExclusion? exclusion = null);
    Task<ShapeSearchOutput> SearchAsync(ImageReference image, bool live, ShapeModel model, RoiGeometry? region, ShapeSearchOptions options, CancellationToken token, RoiExclusion? exclusion = null);
    Task<ShapeTrainingOutput?> PreviewAsync(ImageReference image, bool live, RoiGeometry roi, TrainingOptions options, CancellationToken token, RoiExclusion? exclusion = null)
        => Task.FromResult<ShapeTrainingOutput?>(null);
}
