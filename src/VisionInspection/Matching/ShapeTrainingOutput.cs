using System.Collections.Immutable;
using System.Diagnostics;
using VisionAlgorithm;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Matching;

public sealed record ShapeTrainingOutput(
    ShapeModel Model,
    double ReadMilliseconds,
    double TrainingMilliseconds,
    byte[]? PreviewGray8 = null,
    int PreviewWidth = 0,
    int PreviewHeight = 0);
