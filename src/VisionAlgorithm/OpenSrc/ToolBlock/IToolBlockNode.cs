// File: src/VisionAlgorithm/OpenSrc/ToolBlock/IToolBlockNode.cs
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.ToolBlock;

/// <summary>
/// Contract for a zero-allocation runtime vision tool node or data-linking combiner inside a <see cref="ToolBlockRunner"/>.
/// Cold-path metadata (<see cref="GetInputTerminals"/>, <see cref="GetOutputTerminals"/>) is queried once at DAG compilation time;
/// <see cref="Execute"/> runs on the hot path with zero heap allocations.
/// </summary>
public interface IToolBlockNode
{
    /// <summary>Gets the unique positive integer node identifier within the <see cref="ToolBlockRunner"/>.</summary>
    int NodeId { get; }

    /// <summary>Gets the human-readable node name for UI display and diagnostics.</summary>
    string NodeName { get; }

    /// <summary>Returns the cold-path descriptors of all input ports consumed by this node.</summary>
    ReadOnlySpan<ToolBlockTerminalDescriptor> GetInputTerminals();

    /// <summary>Returns the cold-path descriptors of all output ports produced by this node.</summary>
    ReadOnlySpan<ToolBlockTerminalDescriptor> GetOutputTerminals();

    /// <summary>
    /// Executes the node on the hot path using <paramref name="image"/>, <paramref name="workspace"/>, and <paramref name="context"/>
    /// with zero boxing and zero managed heap allocations.
    /// </summary>
    void Execute(
        ReadOnlyImageView2D<byte> image,
        VisionWorkspace workspace,
        ToolBlockContext context);
}
