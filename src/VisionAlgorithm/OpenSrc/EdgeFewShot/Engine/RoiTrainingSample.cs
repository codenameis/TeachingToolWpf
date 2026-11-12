using System.Buffers;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Backbone;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Domain;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Math;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Preprocessing;

namespace VisionAlgorithm.OpenSrc.EdgeFewShot.Engine;

/// <summary>
/// Per-sample local image crop and fixture pose input for few-shot training without full-image copies or atlas packing.
/// </summary>
public readonly record struct RoiTrainingSample(
    ReadOnlyMemory<byte> Pixels,
    int Width,
    int Height,
    int Stride,
    RoiFixturePose Pose,
    FewShotClassId ClassId);
