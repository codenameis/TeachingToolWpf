// File: src/VisionAlgorithm/OpenSrc/Common/InspectionWorkspace.cs
using System.Buffers;
using System.Runtime.CompilerServices;

namespace VisionAlgorithm.OpenSrc.Common;

/// <summary>
/// Reusable zero-allocation buffer pool manager backed by <see cref="ArrayPool{T}.Shared"/> for hot-path vision algorithms.
/// Renting and returning buffers within a frame or across frames avoids Gen0/LOH garbage collection pressure.
/// </summary>
public sealed class InspectionWorkspace : IDisposable
{
    // [Perf Why]: Minimum pooled allocation length avoids repeated tiny array churn when resizing scratch buffers.
    private const int MinimumScratchCapacity = 256;

    private float[]? _floatBufferA;
    private float[]? _floatBufferB;
    private float[]? _floatBufferC;
    private float[]? _floatBufferD;
    private byte[]? _byteBufferA;
    private byte[]? _byteBufferB;
    private int[]? _intBufferA;
    private int[]? _intBufferB;
    private double[]? _doubleBufferA;
    private bool _disposed;

    public Span<float> GetFloatBufferA(int requiredLength, bool clear = false)
        => EnsureBuffer(ref _floatBufferA, requiredLength, clear);

    public Span<float> GetFloatBufferB(int requiredLength, bool clear = false)
        => EnsureBuffer(ref _floatBufferB, requiredLength, clear);

    public Span<float> GetFloatBufferC(int requiredLength, bool clear = false)
        => EnsureBuffer(ref _floatBufferC, requiredLength, clear);

    public Span<float> GetFloatBufferD(int requiredLength, bool clear = false)
        => EnsureBuffer(ref _floatBufferD, requiredLength, clear);

    public Span<byte> GetByteBufferA(int requiredLength, bool clear = false)
        => EnsureBuffer(ref _byteBufferA, requiredLength, clear);

    public Span<byte> GetByteBufferB(int requiredLength, bool clear = false)
        => EnsureBuffer(ref _byteBufferB, requiredLength, clear);

    public Span<int> GetIntBufferA(int requiredLength, bool clear = false)
        => EnsureBuffer(ref _intBufferA, requiredLength, clear);

    public Span<int> GetIntBuffer(int requiredLength, bool clear = false)
        => EnsureBuffer(ref _intBufferA, requiredLength, clear);

    public Span<int> GetIntBufferB(int requiredLength, bool clear = false)
        => EnsureBuffer(ref _intBufferB, requiredLength, clear);

    public Span<double> GetDoubleBufferA(int requiredLength, bool clear = false)
        => EnsureBuffer(ref _doubleBufferA, requiredLength, clear);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Span<T> EnsureBuffer<T>(ref T[]? slot, int requiredLength, bool clear)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requiredLength);

        if (slot is null || slot.Length < requiredLength)
        {
            if (slot is not null)
            {
                ArrayPool<T>.Shared.Return(slot, clearArray: false);
            }

            slot = ArrayPool<T>.Shared.Rent(Math.Max(MinimumScratchCapacity, requiredLength));
        }

        Span<T> span = slot.AsSpan(0, requiredLength);
        if (clear)
        {
            span.Clear();
        }

        return span;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReturnSlot(ref _floatBufferA);
        ReturnSlot(ref _floatBufferB);
        ReturnSlot(ref _floatBufferC);
        ReturnSlot(ref _floatBufferD);
        ReturnSlot(ref _byteBufferA);
        ReturnSlot(ref _byteBufferB);
        ReturnSlot(ref _intBufferA);
        ReturnSlot(ref _intBufferB);
        ReturnSlot(ref _doubleBufferA);
    }

    private static void ReturnSlot<T>(ref T[]? slot)
    {
        if (slot is not null)
        {
            ArrayPool<T>.Shared.Return(slot, clearArray: false);
            slot = null;
        }
    }
}
