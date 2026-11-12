using System.Runtime.CompilerServices;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.GoldenVariation;

/// <summary>
/// Represents a single normal training sample aligned by its <see cref="FixturePose2D"/>.
/// </summary>
public readonly record struct VariationTrainingSample(
    byte[] SourceImage,
    int SourceWidth,
    int SourceHeight,
    int SourceStride,
    FixturePose2D FixturePose);
