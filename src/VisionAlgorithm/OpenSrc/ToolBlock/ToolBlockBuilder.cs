// File: src/VisionAlgorithm/OpenSrc/ToolBlock/ToolBlockBuilder.cs
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.ToolBlock;

/// <summary>
/// Cold-path DAG builder and slot allocator that assigns $O(1)$ <see cref="TerminalHandle{T}"/> indices,
/// validates port bindings, and performs Kahn's topological sort to produce an executable <see cref="ToolBlockRunner"/>.
/// </summary>
public sealed class ToolBlockBuilder
{
    private const int InitialNodeCapacity = 16;
    private const int InitialLinkCapacity = 32;

    private readonly List<IToolBlockNode> _nodes = new(InitialNodeCapacity);
    private readonly List<ToolBlockLinkDefinition> _links = new(InitialLinkCapacity);

    private int _nextPoseSlot;
    private int _nextPointSlot;
    private int _nextLineSlot;
    private int _nextCircleSlot;
    private int _nextScalarSlot;
    private int _nextBoolSlot;

    public int PoseSlotCount => _nextPoseSlot;
    public int PointSlotCount => _nextPointSlot;
    public int LineSlotCount => _nextLineSlot;
    public int CircleSlotCount => _nextCircleSlot;
    public int ScalarSlotCount => _nextScalarSlot;
    public int BoolSlotCount => _nextBoolSlot;

    /// <summary>Allocates a strongly-typed <see cref="FixturePose2D"/> terminal slot in the registry.</summary>
    public TerminalHandle<FixturePose2D> AllocatePoseSlot()
        => new(_nextPoseSlot++);

    /// <summary>Allocates a strongly-typed <see cref="Point2D"/> terminal slot in the registry.</summary>
    public TerminalHandle<Point2D> AllocatePointSlot()
        => new(_nextPointSlot++);

    /// <summary>Allocates a strongly-typed <see cref="Line2D"/> terminal slot in the registry.</summary>
    public TerminalHandle<Line2D> AllocateLineSlot()
        => new(_nextLineSlot++);

    /// <summary>Allocates a strongly-typed <see cref="Circle2D"/> terminal slot in the registry.</summary>
    public TerminalHandle<Circle2D> AllocateCircleSlot()
        => new(_nextCircleSlot++);

    /// <summary>Allocates a strongly-typed scalar (<see cref="double"/>) terminal slot in the registry.</summary>
    public TerminalHandle<double> AllocateScalarSlot()
        => new(_nextScalarSlot++);

    /// <summary>Allocates a strongly-typed boolean (<see cref="bool"/>) terminal slot in the registry.</summary>
    public TerminalHandle<bool> AllocateBoolSlot()
        => new(_nextBoolSlot++);

    /// <summary>Creates a pre-sized <see cref="ToolBlockContext"/> matching all slots allocated on this builder.</summary>
    public ToolBlockContext CreateContext(int extraMarginSlots = 8)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(extraMarginSlots);
        return new ToolBlockContext(
            poseCapacity: _nextPoseSlot + extraMarginSlots,
            pointCapacity: _nextPointSlot + extraMarginSlots,
            lineCapacity: _nextLineSlot + extraMarginSlots,
            circleCapacity: _nextCircleSlot + extraMarginSlots,
            scalarCapacity: _nextScalarSlot + extraMarginSlots,
            boolCapacity: _nextBoolSlot + extraMarginSlots);
    }

    /// <summary>Registers a tool or combiner node in the DAG.</summary>
    public ToolBlockBuilder AddNode(IToolBlockNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        _nodes.Add(node);
        return this;
    }

    /// <summary>
    /// Declares an explicit directed link between <paramref name="sourceNodeId"/>.<paramref name="sourceOutputPort"/>
    /// and <paramref name="targetNodeId"/>.<paramref name="targetInputPort"/> over <paramref name="sharedHandle"/>.
    /// </summary>
    public ToolBlockBuilder AddLink<T>(
        int sourceNodeId,
        string sourceOutputPort,
        int targetNodeId,
        string targetInputPort,
        TerminalHandle<T> sharedHandle)
        where T : struct
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceOutputPort);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetInputPort);
        if (!sharedHandle.IsBound)
        {
            throw new ArgumentException("Shared terminal handle must be bound to an allocated slot.", nameof(sharedHandle));
        }

        _links.Add(new ToolBlockLinkDefinition(
            SourceNodeId: sourceNodeId,
            SourceOutputPort: sourceOutputPort,
            TargetNodeId: targetNodeId,
            TargetInputPort: targetInputPort,
            ExpectedDataKind: TerminalHandle<T>.ResolveDataKind(),
            SharedSlotIndex: sharedHandle.SlotIndex));
        return this;
    }

    /// <summary>
    /// Validates all nodes, port types, and slot dependencies, performs Kahn's topological sort,
    /// and compiles an immutable <see cref="ToolBlockRunner"/>. Throws <see cref="InvalidOperationException"/> on failure.
    /// </summary>
    public ToolBlockRunner BuildPlan()
    {
        if (!TryBuildPlan(out ToolBlockRunner? runner, out ToolBlockCompileStatus status, out string errorMessage) || runner is null)
        {
            throw new InvalidOperationException($"ToolBlock compilation failed ({status}): {errorMessage}");
        }

        return runner;
    }

    /// <summary>
    /// Attempts to validate and topologically sort the ToolBlock DAG without throwing exceptions on graph errors.
    /// </summary>
    public bool TryBuildPlan(
        out ToolBlockRunner? runner,
        out ToolBlockCompileStatus status,
        out string errorMessage)
    {
        runner = null;
        int n = _nodes.Count;
        if (n == 0)
        {
            status = ToolBlockCompileStatus.EmptyPipeline;
            errorMessage = "ToolBlock contains no nodes.";
            return false;
        }

        // 1. Validate unique NodeIds
        var nodeIndexById = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++)
        {
            int id = _nodes[i].NodeId;
            if (!nodeIndexById.TryAdd(id, i))
            {
                status = ToolBlockCompileStatus.DuplicateNodeId;
                errorMessage = $"Duplicate NodeId {id} ('{_nodes[i].NodeName}').";
                return false;
            }
        }

        // 2. Validate explicit links (ports exist, types match, and shared slot indices match)
        for (int linkIdx = 0; linkIdx < _links.Count; linkIdx++)
        {
            ToolBlockLinkDefinition link = _links[linkIdx];
            if (!nodeIndexById.TryGetValue(link.SourceNodeId, out int srcIdx))
            {
                status = ToolBlockCompileStatus.UnknownNodeId;
                errorMessage = $"Link source NodeId {link.SourceNodeId} does not exist.";
                return false;
            }

            if (!nodeIndexById.TryGetValue(link.TargetNodeId, out int dstIdx))
            {
                status = ToolBlockCompileStatus.UnknownNodeId;
                errorMessage = $"Link target NodeId {link.TargetNodeId} does not exist.";
                return false;
            }

            if (!TryFindPort(_nodes[srcIdx].GetOutputTerminals(), link.SourceOutputPort, out ToolBlockTerminalDescriptor srcPort))
            {
                status = ToolBlockCompileStatus.UnknownPortName;
                errorMessage = $"Output port '{link.SourceOutputPort}' not found on node '{_nodes[srcIdx].NodeName}'.";
                return false;
            }

            if (!TryFindPort(_nodes[dstIdx].GetInputTerminals(), link.TargetInputPort, out ToolBlockTerminalDescriptor dstPort))
            {
                status = ToolBlockCompileStatus.UnknownPortName;
                errorMessage = $"Input port '{link.TargetInputPort}' not found on node '{_nodes[dstIdx].NodeName}'.";
                return false;
            }

            if (srcPort.DataKind != link.ExpectedDataKind || dstPort.DataKind != link.ExpectedDataKind)
            {
                status = ToolBlockCompileStatus.PortTypeMismatch;
                errorMessage = $"Port type mismatch between '{_nodes[srcIdx].NodeName}.{link.SourceOutputPort}' ({srcPort.DataKind}) and '{_nodes[dstIdx].NodeName}.{link.TargetInputPort}' ({dstPort.DataKind}).";
                return false;
            }

            if (srcPort.SlotIndex != link.SharedSlotIndex || dstPort.SlotIndex != link.SharedSlotIndex)
            {
                status = ToolBlockCompileStatus.PortTypeMismatch;
                errorMessage = $"Port slot index mismatch on link '{_nodes[srcIdx].NodeName}.{link.SourceOutputPort}' -> '{_nodes[dstIdx].NodeName}.{link.TargetInputPort}'.";
                return false;
            }
        }

        // 3. Build adjacency matrix from both explicit links and implicit shared (DataKind, SlotIndex) producers -> consumers
        bool[,] hasEdge = new bool[n, n];
        int[] inDegree = new int[n];
        var producerBySlot = new Dictionary<(TerminalDataKind Kind, int Slot), int>(capacity: 64);

        for (int i = 0; i < n; i++)
        {
            ReadOnlySpan<ToolBlockTerminalDescriptor> outputs = _nodes[i].GetOutputTerminals();
            for (int p = 0; p < outputs.Length; p++)
            {
                ToolBlockTerminalDescriptor port = outputs[p];
                if (!port.IsBound)
                {
                    continue;
                }

                if (!IsSlotWithinAllocatedRange(port.DataKind, port.SlotIndex))
                {
                    status = ToolBlockCompileStatus.SlotIndexOutOfRange;
                    errorMessage = $"Node '{_nodes[i].NodeName}' output port '{port.PortName}' references unallocated {port.DataKind} slot {port.SlotIndex}.";
                    return false;
                }

                producerBySlot[(port.DataKind, port.SlotIndex)] = i;
            }
        }

        for (int targetIdx = 0; targetIdx < n; targetIdx++)
        {
            ReadOnlySpan<ToolBlockTerminalDescriptor> inputs = _nodes[targetIdx].GetInputTerminals();
            for (int p = 0; p < inputs.Length; p++)
            {
                ToolBlockTerminalDescriptor port = inputs[p];
                if (!port.IsBound)
                {
                    continue;
                }

                if (!IsSlotWithinAllocatedRange(port.DataKind, port.SlotIndex))
                {
                    status = ToolBlockCompileStatus.SlotIndexOutOfRange;
                    errorMessage = $"Node '{_nodes[targetIdx].NodeName}' input port '{port.PortName}' references unallocated {port.DataKind} slot {port.SlotIndex}.";
                    return false;
                }

                if (producerBySlot.TryGetValue((port.DataKind, port.SlotIndex), out int sourceIdx))
                {
                    if (sourceIdx == targetIdx)
                    {
                        status = ToolBlockCompileStatus.CycleDetected;
                        errorMessage = $"Self-dependency cycle detected on node '{_nodes[targetIdx].NodeName}' port '{port.PortName}'.";
                        return false;
                    }

                    if (!hasEdge[sourceIdx, targetIdx])
                    {
                        hasEdge[sourceIdx, targetIdx] = true;
                        inDegree[targetIdx]++;
                    }
                }
            }
        }

        for (int linkIdx = 0; linkIdx < _links.Count; linkIdx++)
        {
            ToolBlockLinkDefinition link = _links[linkIdx];
            int srcIdx = nodeIndexById[link.SourceNodeId];
            int dstIdx = nodeIndexById[link.TargetNodeId];
            if (srcIdx == dstIdx)
            {
                status = ToolBlockCompileStatus.CycleDetected;
                errorMessage = $"Self-loop detected on NodeId {link.SourceNodeId}.";
                return false;
            }

            if (!hasEdge[srcIdx, dstIdx])
            {
                hasEdge[srcIdx, dstIdx] = true;
                inDegree[dstIdx]++;
            }
        }

        // 4. Kahn's topological sort algorithm (preserving registration order among independent ready nodes)
        int[] queue = new int[n];
        int head = 0;
        int tail = 0;

        for (int i = 0; i < n; i++)
        {
            if (inDegree[i] == 0)
            {
                queue[tail++] = i;
            }
        }

        var orderedNodes = new IToolBlockNode[n];
        int sortedCount = 0;

        while (head < tail)
        {
            int u = queue[head++];
            orderedNodes[sortedCount++] = _nodes[u];

            for (int v = 0; v < n; v++)
            {
                if (hasEdge[u, v])
                {
                    inDegree[v]--;
                    if (inDegree[v] == 0)
                    {
                        queue[tail++] = v;
                    }
                }
            }
        }

        if (sortedCount != n)
        {
            status = ToolBlockCompileStatus.CycleDetected;
            errorMessage = $"Cyclic dependency detected in ToolBlock DAG ({sortedCount} of {n} nodes resolved).";
            return false;
        }

        runner = new ToolBlockRunner(orderedNodes, _links.ToArray());
        status = ToolBlockCompileStatus.Success;
        errorMessage = string.Empty;
        return true;
    }

    private bool IsSlotWithinAllocatedRange(TerminalDataKind kind, int slotIndex)
    {
        if (slotIndex < 0)
        {
            return false;
        }

        return kind switch
        {
            TerminalDataKind.Pose => slotIndex < _nextPoseSlot || _nextPoseSlot == 0,
            TerminalDataKind.Point => slotIndex < _nextPointSlot || _nextPointSlot == 0,
            TerminalDataKind.Line => slotIndex < _nextLineSlot || _nextLineSlot == 0,
            TerminalDataKind.Circle => slotIndex < _nextCircleSlot || _nextCircleSlot == 0,
            TerminalDataKind.Scalar => slotIndex < _nextScalarSlot || _nextScalarSlot == 0,
            TerminalDataKind.Boolean => slotIndex < _nextBoolSlot || _nextBoolSlot == 0,
            _ => false
        };
    }

    private static bool TryFindPort(
        ReadOnlySpan<ToolBlockTerminalDescriptor> ports,
        string portName,
        out ToolBlockTerminalDescriptor descriptor)
    {
        for (int i = 0; i < ports.Length; i++)
        {
            if (string.Equals(ports[i].PortName, portName, StringComparison.Ordinal))
            {
                descriptor = ports[i];
                return true;
            }
        }

        descriptor = default;
        return false;
    }
}
