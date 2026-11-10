// File: src/VisionAlgorithm/OpenSrc/ToolBlock/ToolBlockContext.cs
using System.Runtime.CompilerServices;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.ToolBlock;

/// <summary>
/// Pre-allocated, zero-boxing, zero-allocation terminal slot registry shared across all nodes in a <see cref="ToolBlockRunner"/>.
/// Stores strongly-typed <see cref="SlotValue{T}"/> arrays indexed in $O(1)$ via <see cref="TerminalHandle{T}"/>.
/// </summary>
public sealed class ToolBlockContext
{
    private const int DefaultBankCapacity = 64;

    private readonly SlotValue<FixturePose2D>[] _poseSlots;
    private readonly SlotValue<Point2D>[] _pointSlots;
    private readonly SlotValue<Line2D>[] _lineSlots;
    private readonly SlotValue<Circle2D>[] _circleSlots;
    private readonly SlotValue<double>[] _scalarSlots;
    private readonly SlotValue<bool>[] _boolSlots;

    private ToolBlockInspectionResult _finalResult;

    public int PoseCapacity => _poseSlots.Length;
    public int PointCapacity => _pointSlots.Length;
    public int LineCapacity => _lineSlots.Length;
    public int CircleCapacity => _circleSlots.Length;
    public int ScalarCapacity => _scalarSlots.Length;
    public int BoolCapacity => _boolSlots.Length;

    /// <summary>Gets the final aggregated inspection verdict produced at the end of the pipeline run.</summary>
    public ToolBlockInspectionResult FinalResult => _finalResult;

    public ToolBlockContext(
        int poseCapacity = DefaultBankCapacity,
        int pointCapacity = DefaultBankCapacity,
        int lineCapacity = DefaultBankCapacity,
        int circleCapacity = DefaultBankCapacity,
        int scalarCapacity = DefaultBankCapacity,
        int boolCapacity = DefaultBankCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(poseCapacity);
        ArgumentOutOfRangeException.ThrowIfNegative(pointCapacity);
        ArgumentOutOfRangeException.ThrowIfNegative(lineCapacity);
        ArgumentOutOfRangeException.ThrowIfNegative(circleCapacity);
        ArgumentOutOfRangeException.ThrowIfNegative(scalarCapacity);
        ArgumentOutOfRangeException.ThrowIfNegative(boolCapacity);

        _poseSlots = poseCapacity > 0 ? new SlotValue<FixturePose2D>[poseCapacity] : [];
        _pointSlots = pointCapacity > 0 ? new SlotValue<Point2D>[pointCapacity] : [];
        _lineSlots = lineCapacity > 0 ? new SlotValue<Line2D>[lineCapacity] : [];
        _circleSlots = circleCapacity > 0 ? new SlotValue<Circle2D>[circleCapacity] : [];
        _scalarSlots = scalarCapacity > 0 ? new SlotValue<double>[scalarCapacity] : [];
        _boolSlots = boolCapacity > 0 ? new SlotValue<bool>[boolCapacity] : [];
        _finalResult = default;
    }

    /// <summary>Clears all slot banks to <c>IsValid = false</c> before a new frame execution without allocating memory.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ResetAllSlots()
    {
        Array.Clear(_poseSlots);
        Array.Clear(_pointSlots);
        Array.Clear(_lineSlots);
        Array.Clear(_circleSlots);
        Array.Clear(_scalarSlots);
        Array.Clear(_boolSlots);
        _finalResult = default;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WritePose(TerminalHandle<FixturePose2D> handle, in FixturePose2D pose, bool isValid = true)
    {
        if (handle.IsBound)
        {
            _poseSlots[handle.SlotIndex] = new SlotValue<FixturePose2D>(pose, isValid && pose.IsValid);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void InvalidatePose(TerminalHandle<FixturePose2D> handle)
    {
        if (handle.IsBound)
        {
            _poseSlots[handle.SlotIndex] = SlotValue<FixturePose2D>.Invalid;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SlotValue<FixturePose2D> ReadPoseSlot(TerminalHandle<FixturePose2D> handle)
        => handle.IsBound ? _poseSlots[handle.SlotIndex] : SlotValue<FixturePose2D>.Invalid;

    /// <summary>
    /// Reads the <see cref="FixturePose2D"/> from <paramref name="handle"/> if bound; otherwise returns <see cref="FixturePose2D.Identity"/>.
    /// If the handle is bound to an upstream tool that failed detection (<c>IsValid == false</c>), returns <see langword="false"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryResolveInputFixture(TerminalHandle<FixturePose2D> handle, out FixturePose2D pose)
    {
        if (!handle.IsBound)
        {
            pose = FixturePose2D.Identity;
            return true;
        }

        SlotValue<FixturePose2D> slot = _poseSlots[handle.SlotIndex];
        if (!slot.IsValid || !slot.Value.IsValid)
        {
            pose = FixturePose2D.Identity;
            return false;
        }

        pose = slot.Value;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WritePoint(TerminalHandle<Point2D> handle, Point2D point, bool isValid = true)
    {
        if (handle.IsBound)
        {
            _pointSlots[handle.SlotIndex] = new SlotValue<Point2D>(point, isValid && point.IsFinite);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void InvalidatePoint(TerminalHandle<Point2D> handle)
    {
        if (handle.IsBound)
        {
            _pointSlots[handle.SlotIndex] = SlotValue<Point2D>.Invalid;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SlotValue<Point2D> ReadPointSlot(TerminalHandle<Point2D> handle)
        => handle.IsBound ? _pointSlots[handle.SlotIndex] : SlotValue<Point2D>.Invalid;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteLine(TerminalHandle<Line2D> handle, in Line2D line, bool isValid = true)
    {
        if (handle.IsBound)
        {
            _lineSlots[handle.SlotIndex] = new SlotValue<Line2D>(line, isValid && line.IsValid);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void InvalidateLine(TerminalHandle<Line2D> handle)
    {
        if (handle.IsBound)
        {
            _lineSlots[handle.SlotIndex] = SlotValue<Line2D>.Invalid;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SlotValue<Line2D> ReadLineSlot(TerminalHandle<Line2D> handle)
        => handle.IsBound ? _lineSlots[handle.SlotIndex] : SlotValue<Line2D>.Invalid;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteCircle(TerminalHandle<Circle2D> handle, in Circle2D circle, bool isValid = true)
    {
        if (handle.IsBound)
        {
            _circleSlots[handle.SlotIndex] = new SlotValue<Circle2D>(circle, isValid && circle.IsValid);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void InvalidateCircle(TerminalHandle<Circle2D> handle)
    {
        if (handle.IsBound)
        {
            _circleSlots[handle.SlotIndex] = SlotValue<Circle2D>.Invalid;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SlotValue<Circle2D> ReadCircleSlot(TerminalHandle<Circle2D> handle)
        => handle.IsBound ? _circleSlots[handle.SlotIndex] : SlotValue<Circle2D>.Invalid;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteScalar(TerminalHandle<double> handle, double value, bool isValid = true)
    {
        if (handle.IsBound)
        {
            _scalarSlots[handle.SlotIndex] = new SlotValue<double>(value, isValid && double.IsFinite(value));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void InvalidateScalar(TerminalHandle<double> handle)
    {
        if (handle.IsBound)
        {
            _scalarSlots[handle.SlotIndex] = SlotValue<double>.Invalid;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SlotValue<double> ReadScalarSlot(TerminalHandle<double> handle)
        => handle.IsBound ? _scalarSlots[handle.SlotIndex] : SlotValue<double>.Invalid;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteBool(TerminalHandle<bool> handle, bool value, bool isValid = true)
    {
        if (handle.IsBound)
        {
            _boolSlots[handle.SlotIndex] = new SlotValue<bool>(value, isValid);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void InvalidateBool(TerminalHandle<bool> handle)
    {
        if (handle.IsBound)
        {
            _boolSlots[handle.SlotIndex] = SlotValue<bool>.Invalid;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SlotValue<bool> ReadBoolSlot(TerminalHandle<bool> handle)
        => handle.IsBound ? _boolSlots[handle.SlotIndex] : SlotValue<bool>.Invalid;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteFinalResult(in ToolBlockInspectionResult result)
    {
        _finalResult = result;
    }
}
