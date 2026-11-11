// File: src/VisionOcr.PpOcr/PpOcrRoiPatch.cs
using System;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionOcr.PpOcr;

/// <summary>
/// Heap-safe 8-bit grayscale ROI descriptor for multi-ROI batched PP-OCR recognition.
/// </summary>
public readonly record struct PpOcrRoiPatch(
    ReadOnlyMemory<byte> Pixels,
    int Width,
    int Height,
    int Stride)
{
    public bool IsValid =>
        Width > 0 &&
        Height > 0 &&
        Stride >= Width &&
        Pixels.Length >= checked(((Height - 1) * Stride) + Width);

    public ReadOnlyImageView2D<byte> AsView()
    {
        if (!IsValid)
        {
            throw new InvalidOperationException("유효하지 않은 PpOcrRoiPatch 버퍼입니다.");
        }

        return new ReadOnlyImageView2D<byte>(Pixels.Span, Width, Height, Stride);
    }
}
