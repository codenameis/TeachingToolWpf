namespace VisionAlgorithm.OpenSrc.IndustrialOcrOcv;

/// <summary>
/// Golden character training patch pairing a character label with its cropped grayscale pixels.
/// </summary>
public readonly record struct GoldenCharSample(
    char Character,
    byte[] Pixels,
    int Width,
    int Height,
    int Stride,
    bool DarkOnLight = true);
