// File: src/VisionAlgorithm/OpenSrc/ToolBlock/TerminalHandle.cs
using System.Runtime.CompilerServices;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.ToolBlock;

/// <summary>
/// Represents an unboxed $O(1)$ integer slot index into a typed register bank inside <see cref="ToolBlockContext"/>.
/// An uninitialized or unbound handle has <see cref="IsBound"/> equal to <see langword="false"/>.
/// </summary>
/// <typeparam name="T">The strongly-typed terminal value primitive.</typeparam>
public readonly record struct TerminalHandle<T>
    where T : struct
{
    private readonly int _slotIndexPlusOne;

    /// <summary>Gets the 0-based register bank slot index, or <c>-1</c> when unbound.</summary>
    public int SlotIndex => _slotIndexPlusOne - 1;

    /// <summary>Gets a value indicating whether this handle points to a valid allocated slot (<c>SlotIndex &gt;= 0</c>).</summary>
    public bool IsBound => _slotIndexPlusOne > 0;

    public TerminalHandle(int slotIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slotIndex);
        _slotIndexPlusOne = slotIndex + 1;
    }

    /// <summary>Gets an unbound sentinel handle (<c>SlotIndex == -1</c>).</summary>
    public static TerminalHandle<T> Unbound => default;

    /// <summary>Resolves the compile-time <see cref="TerminalDataKind"/> corresponding to <typeparamref name="T"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TerminalDataKind ResolveDataKind()
    {
        if (typeof(T) == typeof(FixturePose2D))
        {
            return TerminalDataKind.Pose;
        }

        if (typeof(T) == typeof(Point2D))
        {
            return TerminalDataKind.Point;
        }

        if (typeof(T) == typeof(Line2D))
        {
            return TerminalDataKind.Line;
        }

        if (typeof(T) == typeof(Circle2D))
        {
            return TerminalDataKind.Circle;
        }

        if (typeof(T) == typeof(double))
        {
            return TerminalDataKind.Scalar;
        }

        if (typeof(T) == typeof(bool))
        {
            return TerminalDataKind.Boolean;
        }

        return TerminalDataKind.None;
    }
}
