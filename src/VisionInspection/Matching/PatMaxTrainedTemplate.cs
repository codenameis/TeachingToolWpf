using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using VisionAlgorithm.OpenSrc.PatMax;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Matching;

public sealed record PatMaxTrainedTemplate(
    PatMaxModel Model,
    RoiGeometry TeachingRegion,
    PatMaxTrainParams TrainParams,
    ImmutableArray<PointD> PreviewPoints,
    ImmutableArray<PointD> PreviewNormals,
    ImmutableArray<PointD> CoarsePreviewPoints,
    double ReadMilliseconds,
    double TrainMilliseconds,
    byte[]? FilteredPreviewGray8 = null,
    int PreviewWidth = 0,
    int PreviewHeight = 0,
    byte[]? PreviewMask = null,
    byte[]? RawPreviewGray8 = null,
    int RawPreviewWidth = 0,
    int RawPreviewHeight = 0) : IDisposable
{
    public void Dispose() => Model.Dispose();
}
