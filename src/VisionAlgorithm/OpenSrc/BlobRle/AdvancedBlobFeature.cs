// File: src/VisionAlgorithm/OpenSrc/BlobRle/AdvancedBlobFeature.cs
using System.Numerics;

namespace VisionAlgorithm.OpenSrc.BlobRle;

/// <summary>
/// Commercial-grade topological, geometric, and grayscale intensity descriptors of an RLE-encoded blob component
/// (HALCON <c>connect_and_holes</c>, <c>smallest_rectangle2</c>, <c>convexity</c>, <c>intensity</c> /
/// Cognex <c>CogBlobResult</c> parity).
/// </summary>
public readonly record struct AdvancedBlobFeature(
    int ComponentId,
    long AreaPixels,
    Vector2 Centroid,
    float PrincipalAxisAngleRad,
    float Eccentricity,
    int HoleCount,
    long HoleAreaPixels,
    float PerimeterPixels,
    float Circularity,
    float ConvexHullAreaPixels,
    float Solidity,
    Vector2 MinAreaBoxCenter,
    float MinAreaBoxWidth,
    float MinAreaBoxHeight,
    float MinAreaBoxAngleRad,
    float MeanGray,
    float StdDevGray,
    byte MinGray,
    byte MaxGray,
    int MinCol,
    int MinRow,
    int MaxCol,
    int MaxRow,
    ReadOnlyMemory<Vector2> ConvexHullVertices)
{
    public int BoundingWidth => (MaxCol - MinCol) + 1;
    public int BoundingHeight => (MaxRow - MinRow) + 1;
    public int EulerNumber => 1 - HoleCount;
}
