using System.Collections.Immutable;
using System.Diagnostics;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Backbone;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Domain;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Engine;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Math;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Preprocessing;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Matching;

public sealed record EdgeFewShotItemOutput(
    Guid RoiId,
    string Name,
    RoiFixturePose Pose,
    ImmutableArray<VisionGeometry.PointD> Outline,
    FewShotPredictionResult Prediction,
    double Microseconds);
