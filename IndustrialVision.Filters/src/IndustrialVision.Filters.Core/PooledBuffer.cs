using System.Buffers;
using System.Runtime.CompilerServices;

namespace IndustrialVision.Filters.Core;

/// <summary>실제 대여 용량을 계상하며 plan 종료 시 반환한다.</summary>
public sealed class PooledBuffer<T> : IDisposable where T : unmanaged
{
    private T[]? _array;
    public T[] Array => _array ?? throw new ObjectDisposedException(nameof(PooledBuffer<T>));
    public int Length
    {
        get;
    }
    public long Bytes => (long)Array.Length * Unsafe.SizeOf<T>();

    public static long EstimateBytes(int length)
    {
        if (length <= 0 || length > 1 << 29)
        {
            throw new MemoryBudgetException("Use tile execution for this image size.");
        }
        var capacity = 16;
        while (capacity < length)
        {
            capacity = checked(capacity * 2);
        }
        return (long)capacity * Unsafe.SizeOf<T>();
    }

    public PooledBuffer(int length)
    {
        Length = length;
        _array = ArrayPool<T>.Shared.Rent(length);
    }

    public void Dispose()
    {
        var array = Interlocked.Exchange(ref _array, null);
        if (array is not null)
        {
            ArrayPool<T>.Shared.Return(array);
        }
    }
}
