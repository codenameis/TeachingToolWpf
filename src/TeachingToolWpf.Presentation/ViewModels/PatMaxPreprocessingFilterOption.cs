using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using TeachingToolWpf.Commands;
using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Models;
using VisionAlgorithm.OpenSrc.PatMax;
using VisionCanvas.Imaging.Filters;
using VisionCanvas.Rendering;
using VisionGeometry;

namespace TeachingToolWpf.ViewModels;

public sealed record PatMaxPreprocessingFilterOption(
    PatMaxPreprocessingFilterKind Kind,
    string Name,
    string Description)
{
    public override string ToString() => Name;
}
