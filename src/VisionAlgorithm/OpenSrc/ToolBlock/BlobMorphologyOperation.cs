// File: src/VisionAlgorithm/OpenSrc/ToolBlock/BlobMorphologyOperation.cs
namespace VisionAlgorithm.OpenSrc.ToolBlock;

/// <summary>
/// Specifies the optional morphological RLE filtering operation applied inside <see cref="LargestBlobToolNode"/>.
/// </summary>
public enum BlobMorphologyOperation
{
    None = 0,
    DilateBox = 1,
    ErodeBox = 2,
    OpenBox = 3,
    CloseBox = 4
}
