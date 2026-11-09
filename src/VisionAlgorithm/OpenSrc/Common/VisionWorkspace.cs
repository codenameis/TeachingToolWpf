// File: src/VisionAlgorithm/OpenSrc/Common/VisionWorkspace.cs
using System.Runtime.CompilerServices;

namespace VisionAlgorithm.OpenSrc.Common;

/// <summary>
/// Zero-allocation scratch buffer pool facade backed by <see cref="InspectionWorkspace"/> for OCR/OCV,
/// sequence encoding, RLE text-line detection, and multi-part auto-teaching pipelines.
/// </summary>
public sealed class VisionWorkspace : IDisposable
{
    private readonly InspectionWorkspace _inner = new();

    public VisionWorkspace(int initialCapacity = 0)
    {
        if (initialCapacity > 0)
        {
            _ = _inner.GetByteBufferA(Math.Min(initialCapacity, 4096 * 4096));
        }
    }

    public InspectionWorkspace Inner => _inner;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<float> GetFloatBufferA(int requiredLength, bool clear = false)
        => _inner.GetFloatBufferA(requiredLength, clear);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<float> GetFloatBufferB(int requiredLength, bool clear = false)
        => _inner.GetFloatBufferB(requiredLength, clear);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<float> GetFloatBufferC(int requiredLength, bool clear = false)
        => _inner.GetFloatBufferC(requiredLength, clear);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<float> GetFloatBufferD(int requiredLength, bool clear = false)
        => _inner.GetFloatBufferD(requiredLength, clear);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<byte> GetByteBufferA(int requiredLength, bool clear = false)
        => _inner.GetByteBufferA(requiredLength, clear);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<byte> GetByteBufferB(int requiredLength, bool clear = false)
        => _inner.GetByteBufferB(requiredLength, clear);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<int> GetIntBuffer(int requiredLength, bool clear = false)
        => _inner.GetIntBuffer(requiredLength, clear);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<int> GetIntBufferA(int requiredLength, bool clear = false)
        => _inner.GetIntBufferA(requiredLength, clear);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<int> GetIntBufferB(int requiredLength, bool clear = false)
        => _inner.GetIntBufferB(requiredLength, clear);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator InspectionWorkspace(VisionWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return workspace._inner;
    }

    public void Dispose() => _inner.Dispose();
}
