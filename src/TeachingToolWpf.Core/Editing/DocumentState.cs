using System.Collections.Immutable;
using TeachingToolWpf.Core.Geometry;

namespace TeachingToolWpf.Core.Editing;

public sealed record Roi(Guid Id, string Name, RotatedRect Geometry, Guid? ParentId, bool IsGroup, long Order)
{
    public FilterSettings Filter { get; init; } = new(RoiFilterKind.None);
}
public sealed record ImageReference(string Key, string Name, int Width, int Height, string Version = "");

/// <summary>Immutable image-space geometry prevents precision drift during reparenting.</summary>
public sealed record DocumentState(
    ImmutableDictionary<Guid, Roi> Rois,
    ImmutableHashSet<Guid> Selection,
    ImageReference? Image,
    ViewportTransform Viewport)
{
    public static DocumentState Empty => new(ImmutableDictionary<Guid, Roi>.Empty,
        ImmutableHashSet<Guid>.Empty, null, ViewportTransform.Identity);
}
