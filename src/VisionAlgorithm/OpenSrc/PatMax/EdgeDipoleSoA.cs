// File: src/VisionAlgorithm/OpenSrc/PatMax/EdgeDipoleSoA.cs
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace VisionAlgorithm.OpenSrc.PatMax;

/// <summary>
/// Value snapshot of a single boundary dipole (sub-pixel position, unit normal vector, and edge magnitude).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct EdgeDipole(
    float X,
    float Y,
    float CosTheta,
    float SinTheta,
    float Magnitude);

/// <summary>
/// Structure-of-Arrays (SoA) container for boundary dipoles backed by <see cref="ArrayPool{T}"/> for contiguous SIMD access.
/// </summary>
public sealed class EdgeDipoleSoA : IDisposable
{
    private float[]? _x;
    private float[]? _y;
    private float[]? _cosTheta;
    private float[]? _sinTheta;
    private float[]? _magnitude;
    private bool _disposed;

    public int Count { get; private set; }
    public int Capacity { get; private set; }
    public bool IsEmpty => Count == 0;

    public ReadOnlySpan<float> X
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _x.AsSpan(0, Count);
        }
    }

    public ReadOnlySpan<float> Y
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _y.AsSpan(0, Count);
        }
    }

    public ReadOnlySpan<float> CosTheta
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _cosTheta.AsSpan(0, Count);
        }
    }

    public ReadOnlySpan<float> SinTheta
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _sinTheta.AsSpan(0, Count);
        }
    }

    public ReadOnlySpan<float> Magnitude
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _magnitude.AsSpan(0, Count);
        }
    }

    public Span<float> WritableX
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _x.AsSpan(0, Capacity);
        }
    }

    public Span<float> WritableY
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _y.AsSpan(0, Capacity);
        }
    }

    public Span<float> WritableCosTheta
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _cosTheta.AsSpan(0, Capacity);
        }
    }

    public Span<float> WritableSinTheta
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _sinTheta.AsSpan(0, Capacity);
        }
    }

    public Span<float> WritableMagnitude
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _magnitude.AsSpan(0, Capacity);
        }
    }

    private EdgeDipoleSoA(int capacity)
    {
        Capacity = capacity;
        if (capacity == 0)
        {
            _x = [];
            _y = [];
            _cosTheta = [];
            _sinTheta = [];
            _magnitude = [];
            return;
        }

        _x = ArrayPool<float>.Shared.Rent(capacity);
        _y = ArrayPool<float>.Shared.Rent(capacity);
        _cosTheta = ArrayPool<float>.Shared.Rent(capacity);
        _sinTheta = ArrayPool<float>.Shared.Rent(capacity);
        _magnitude = ArrayPool<float>.Shared.Rent(capacity);
    }

    public static EdgeDipoleSoA Empty { get; } = new(0);

    public static EdgeDipoleSoA Rent(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        return capacity == 0 ? Empty : new EdgeDipoleSoA(capacity);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Clear()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Count = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetCount(int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, Capacity);
        Count = count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(float x, float y, float cosTheta, float sinTheta, float magnitude)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var index = Count;
        if ((uint)index >= (uint)Capacity)
        {
            if (Capacity == 0)
            {
                throw new InvalidOperationException("Cannot add to EdgeDipoleSoA.Empty.");
            }

            Grow();
        }

        _x![index] = x;
        _y![index] = y;
        _cosTheta![index] = cosTheta;
        _sinTheta![index] = sinTheta;
        _magnitude![index] = magnitude;
        Count = index + 1;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Grow()
    {
        var newCapacity = checked(Capacity * 2);
        GrowBuffer(ref _x, Count, newCapacity);
        GrowBuffer(ref _y, Count, newCapacity);
        GrowBuffer(ref _cosTheta, Count, newCapacity);
        GrowBuffer(ref _sinTheta, Count, newCapacity);
        GrowBuffer(ref _magnitude, Count, newCapacity);
        Capacity = newCapacity;
    }

    private static void GrowBuffer(ref float[]? buffer, int count, int newCapacity)
    {
        var next = ArrayPool<float>.Shared.Rent(newCapacity);
        if (buffer is not null && count > 0)
        {
            buffer.AsSpan(0, count).CopyTo(next);
        }

        ReturnBuffer(ref buffer);
        buffer = next;
    }

    public EdgeDipole this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
            return new EdgeDipole(_x![index], _y![index], _cosTheta![index], _sinTheta![index], _magnitude![index]);
        }
    }

    public void Dispose()
    {
        if (_disposed || Capacity == 0)
        {
            return;
        }

        _disposed = true;
        Count = 0;
        ReturnBuffer(ref _x);
        ReturnBuffer(ref _y);
        ReturnBuffer(ref _cosTheta);
        ReturnBuffer(ref _sinTheta);
        ReturnBuffer(ref _magnitude);
    }

    private static void ReturnBuffer(ref float[]? buffer)
    {
        var array = buffer;
        buffer = null;
        if (array is { Length: > 0 })
        {
            ArrayPool<float>.Shared.Return(array);
        }
    }
}
