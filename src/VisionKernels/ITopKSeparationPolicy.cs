namespace VisionKernels;

/// <summary>
/// Strategy interface for zero-allocation descending Top-K / NMS buffers (<see cref="BoundedTopK"/>).
/// </summary>
public interface ITopKSeparationPolicy<T>
    where T : struct
{
    bool AreTooClose(in T existing, in T candidate);
    bool IsHigherScore(in T candidate, in T existing);
}
