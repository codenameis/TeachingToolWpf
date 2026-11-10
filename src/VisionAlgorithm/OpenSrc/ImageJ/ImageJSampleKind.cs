// File: src/VisionAlgorithm/OpenSrc/ImageJ/ImageJSampleKind.cs
namespace VisionAlgorithm.OpenSrc.ImageJ;

/// <summary>
/// Built-in synthetic reference images modeled after ImageJ "File &gt; Open Samples".
/// </summary>
public enum ImageJSampleKind : byte
{
    BlobsAndParticles = 0,
    CellColonyCounter = 1,
    MetrologyEdgesAndRings = 2,
    UnevenShadingAndSaltPepper = 3
}
