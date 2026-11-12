using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.EdgeFewShot.Engine;

/// <summary>
/// Represents a labeled few-shot training example in feature space.
/// </summary>
public readonly record struct FewShotTrainingExample(int ClassLabel, float[] RawEmbedding);
