using System.Collections.Immutable;
using TeachingToolWpf.Core.Geometry;

namespace TeachingToolWpf.Core.Editing;

/// <summary>In-app value snapshot; retains neither source images nor edit history.</summary>
public sealed class RoiClipboard
{
    private ImmutableArray<Roi> entries = [];
    private BoundsD bounds;
    private int pastes;
    public bool HasContent => !entries.IsEmpty;
    public int Count => entries.Length;
    public int Copy(DocumentState state)
    {
        var ids = RoiHierarchy.Subtree(state, RoiHierarchy.EffectiveRoots(state));
        if (ids.Count == 0) return 0;
        var copied = state.Rois.Values.Where(r => ids.Contains(r.Id)).OrderBy(r => r.Order).ToImmutableArray();
        var combined = copied[0].Geometry.Bounds;
        for (var i = 1; i < copied.Length; i++)
        {
            var next = copied[i].Geometry.Bounds;
            combined = new(Math.Min(combined.Left, next.Left), Math.Min(combined.Top, next.Top),
                Math.Max(combined.Right, next.Right), Math.Max(combined.Bottom, next.Bottom));
        }
        entries = copied; bounds = combined;
        pastes = 0;
        return entries.Length;
    }
    /// <summary>Captures shared immutable entries and the next paste number without traversing any ROI.</summary>
    public RoiClipboardContent Capture() => new(entries, bounds, pastes + 1);
    internal ImmutableArray<Roi> Place(ImageReference image, PointD step) => Capture().Place(image, step);
    internal void Pasted() => pastes++;
}

/// <summary>Worker-owned placement input, independent of later copy or paste actions.</summary>
public sealed class RoiClipboardContent
{
    public ImmutableArray<Roi> Entries { get; }
    public BoundsD Bounds { get; }
    /// <summary>One-based sequence number of the next paste at capture time.</summary>
    public int PasteNumber { get; }
    internal RoiClipboardContent(ImmutableArray<Roi> entries, BoundsD bounds, int pasteNumber)
    { Entries = entries; Bounds = bounds; PasteNumber = pasteNumber; }

    public ImmutableArray<Roi> Place(ImageReference image, PointD step, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (Entries.IsEmpty) return [];
        var bounds = Bounds;
        var scale = Math.Min(1, Math.Min(image.Width / bounds.Width, image.Height / bounds.Height));
        var halfWidth = bounds.Width * scale / 2; var halfHeight = bounds.Height * scale / 2;
        var desired = bounds.Center + step * PasteNumber;
        var center = new PointD(Math.Clamp(desired.X, halfWidth, image.Width - halfWidth),
            Math.Clamp(desired.Y, halfHeight, image.Height - halfHeight));
        var placed = ImmutableArray.CreateBuilder<Roi>(Entries.Length);
        foreach (var roi in Entries)
        {
            token.ThrowIfCancellationRequested();
            placed.Add(roi with
            {
                Geometry = roi.Geometry.WithFrame(center + (roi.Geometry.Center - bounds.Center) * scale,
                    roi.Geometry.Width * scale, roi.Geometry.Height * scale, roi.Geometry.Angle)
            });
        }
        token.ThrowIfCancellationRequested();
        return placed.MoveToImmutable();
    }
}
