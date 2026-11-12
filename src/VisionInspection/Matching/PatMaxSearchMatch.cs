using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using VisionAlgorithm.OpenSrc.PatMax;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Matching;

public sealed record PatMaxSearchMatch(
    double X,
    double Y,
    double AngleDegrees,
    double Scale,
    double AspectRatio,
    float Score,
    float Coverage,
    float Clutter,
    int MatchedDipoles,
    int Iterations);
