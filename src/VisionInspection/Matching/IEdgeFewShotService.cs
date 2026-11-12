using System.Collections.Immutable;
using System.Diagnostics;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Backbone;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Domain;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Engine;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Math;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Preprocessing;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Matching;

public interface IEdgeFewShotService
{
    Task<EdgeFewShotTrainingOutput> TrainAsync(
        ImageReference image,
        bool live,
        IReadOnlyList<EdgeFewShotSampleInput> samples,
        FewShotTrainOptions options,
        CancellationToken token);

    Task<EdgeFewShotClassificationOutput> ClassifyAsync(
        ImageReference image,
        bool live,
        ClassPrototypeModel model,
        IReadOnlyList<EdgeFewShotTargetInput> targets,
        CancellationToken token);
}
