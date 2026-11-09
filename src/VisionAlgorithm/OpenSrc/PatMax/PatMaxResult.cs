// File: src/VisionAlgorithm/OpenSrc/PatMax/PatMaxResult.cs
using System.Runtime.InteropServices;

namespace VisionAlgorithm.OpenSrc.PatMax;

/// <summary>
/// Sub-pixel geometric pose, similarity score, coverage, and clutter breakdown from PatMax alignment.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct PatMaxResult(
    float X,
    float Y,
    float AngleRadians,
    float Scale,
    float AspectRatio,
    float Score,
    float Coverage,
    float Clutter,
    int MatchedDipoleCount,
    int IterationCount)
{
    public bool IsValid => Score > 0f && Scale > 0f && MatchedDipoleCount > 0;

    public static PatMaxResult Empty => default;
}
