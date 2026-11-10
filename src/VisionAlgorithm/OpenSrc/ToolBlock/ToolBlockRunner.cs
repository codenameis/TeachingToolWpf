// File: src/VisionAlgorithm/OpenSrc/ToolBlock/ToolBlockRunner.cs
using System.Runtime.CompilerServices;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.ToolBlock;

/// <summary>
/// Immutable, topologically sorted DAG execution engine that runs all registered <see cref="IToolBlockNode"/> instances
/// sequentially in a single pass with zero heap allocations on the hot path.
/// </summary>
public sealed class ToolBlockRunner
{
    private readonly IToolBlockNode[] _orderedNodes;
    private readonly ToolBlockLinkDefinition[] _verifiedLinks;

    public int NodeCount => _orderedNodes.Length;
    public ReadOnlySpan<IToolBlockNode> OrderedNodes => _orderedNodes;
    public ReadOnlySpan<ToolBlockLinkDefinition> VerifiedLinks => _verifiedLinks;

    internal ToolBlockRunner(IToolBlockNode[] orderedNodes, ToolBlockLinkDefinition[] verifiedLinks)
    {
        ArgumentNullException.ThrowIfNull(orderedNodes);
        ArgumentNullException.ThrowIfNull(verifiedLinks);
        _orderedNodes = orderedNodes;
        _verifiedLinks = verifiedLinks;
    }

    /// <summary>
    /// Executes the compiled DAG plan over <paramref name="image"/> using <paramref name="workspace"/> and <paramref name="context"/>
    /// with zero boxing and zero managed heap allocations.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public ToolBlockInspectionResult Execute(
        ReadOnlyImageView2D<byte> image,
        VisionWorkspace workspace,
        ToolBlockContext context)
    {
        image.EnsureValid();
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(context);

        context.ResetAllSlots();

        IToolBlockNode[] nodes = _orderedNodes;
        for (int i = 0; i < nodes.Length; i++)
        {
            nodes[i].Execute(image, workspace, context);
        }

        return context.FinalResult;
    }
}
