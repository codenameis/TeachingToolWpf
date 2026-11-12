using System.Collections.Immutable;
using System.Diagnostics;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Matching;

public sealed record TemplateTrainingOutput(
    CorrelationTemplate Model,
    double Milliseconds,
    byte[]? PreviewGray8 = null,
    int PreviewWidth = 0,
    int PreviewHeight = 0);
