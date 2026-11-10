// File: src/VisionAlgorithm/OpenSrc/ToolBlock/SlotValue.cs
using System.Runtime.CompilerServices;

namespace VisionAlgorithm.OpenSrc.ToolBlock;

/// <summary>
/// Bundles a strongly-typed terminal register value with its runtime validity state (<see cref="IsValid"/>)
/// so downstream linked nodes can immediately detect upstream ROI detection failure (<c>Found == false</c>) without boxing.
/// </summary>
/// <typeparam name="T">The strongly-typed terminal value primitive.</typeparam>
public readonly record struct SlotValue<T>(T Value, bool IsValid)
    where T : struct
{
    /// <summary>Gets an invalid (missing/unfound) slot entry.</summary>
    public static SlotValue<T> Invalid
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(default, false);
    }

    /// <summary>Creates a valid slot entry containing <paramref name="value"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SlotValue<T> CreateValid(in T value)
        => new(value, true);
}
