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
