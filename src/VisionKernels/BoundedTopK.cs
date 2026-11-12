using System.Runtime.CompilerServices;

namespace VisionKernels;

/// <summary>
/// Zero-allocation bounded Top-K insertion with spatial/pose non-maximum suppression.
/// </summary>
public static class BoundedTopK
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void InsertOrSuppress<T, TPolicy>(
        Span<T> buffer,
        ref int count,
        in T candidate,
        in TPolicy policy)
        where T : struct
        where TPolicy : struct, ITopKSeparationPolicy<T>
    {
        for (var i = 0; i < count; i++)
        {
            if (policy.AreTooClose(in buffer[i], in candidate))
            {
                if (policy.IsHigherScore(in candidate, in buffer[i]))
                {
                    buffer[i] = candidate;
                    SortUp(buffer, i, in policy);
                }

                return;
            }
        }

        if (count < buffer.Length)
        {
            buffer[count] = candidate;
            SortUp(buffer, count, in policy);
            count++;
            return;
        }

        if (policy.IsHigherScore(in candidate, in buffer[count - 1]))
        {
            buffer[count - 1] = candidate;
            SortUp(buffer, count - 1, in policy);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SortUp<T, TPolicy>(Span<T> buffer, int index, in TPolicy policy)
        where T : struct
        where TPolicy : struct, ITopKSeparationPolicy<T>
    {
        while (index > 0 && policy.IsHigherScore(in buffer[index], in buffer[index - 1]))
        {
            (buffer[index - 1], buffer[index]) = (buffer[index], buffer[index - 1]);
            index--;
        }
    }
}
