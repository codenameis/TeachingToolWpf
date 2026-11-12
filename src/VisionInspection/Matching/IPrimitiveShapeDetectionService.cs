using System.Collections.Immutable;
using System.Diagnostics;
using VisionAlgorithm;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionAlgorithm.OpenSrc.ShapeDetection;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Matching;

public interface IPrimitiveShapeDetectionService
{
    Task<PrimitiveDetectionOutput> DetectAsync(ImageReference image, bool live, RoiGeometry? region,
        PrimitiveDetectionOptions options, CancellationToken token, RoiExclusion? exclusion = null);
}
