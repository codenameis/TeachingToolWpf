namespace VisionAlgorithm.OpenSrc.IndustrialOcrOcv;

/// <summary>
/// Industrial character printing modality controlling RLE morphological dot-bridging prior to vertical pitch segmentation.
/// </summary>
public enum PrintMethodType : byte
{
    ContinuousLaser = 0,
    InkjetDotMatrix = 1
}
