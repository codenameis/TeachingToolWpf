// File: src/VisionAlgorithm/OpenSrc/EdgeFewShot/Backbone/IFeatureMapExtractor.cs
namespace VisionAlgorithm.OpenSrc.EdgeFewShot.Backbone;

/// <summary>
/// Contract for mid-layer feature map extractors (PaDiM ICPR 2021 / PatchCore CVPR 2022)
/// returning planar CHW tensors for <c>Layer 2</c> and <c>Layer 3</c> without heap allocations on the inference path.
/// </summary>
public interface IFeatureMapExtractor
{
    int Layer2Channels { get; }
    int Layer3Channels { get; }
    int TotalConcatenatedChannels => Layer2Channels + Layer3Channels;

    (int GridWidth, int GridHeight) GetLayer2Dimensions(int roiWidth, int roiHeight);

    (int GridWidth, int GridHeight) GetLayer3Dimensions(int roiWidth, int roiHeight);

    int GetRequiredScratchFloats(int roiWidth, int roiHeight);

    /// <summary>
    /// Extracts planar <c>[C2, H2, W2]</c> into <paramref name="layer2Out"/> and planar <c>[C3, H3, W3]</c> into <paramref name="layer3Out"/>.
    /// </summary>
    void ExtractMidLayerMaps(
        ReadOnlySpan<float> normalizedRoi,
        int roiWidth,
        int roiHeight,
        Span<float> layer2Out,
        Span<float> layer3Out,
        Span<float> scratchBuffer);
}
