using System.Collections.Immutable;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Core.Editing;

/// <summary>Worker-owned placement input, independent of later copy or paste actions.</summary>
public sealed class RoiClipboardContent
{
    internal long OrderCount => Entries.Length;

    public ImmutableArray<Roi> Entries { get; }

    public BoundsD Bounds { get; }

    /// <summary>One-based sequence number of the next paste at capture time.</summary>
    public int PasteNumber { get; }

    internal RoiClipboardContent(ImmutableArray<Roi> entries, BoundsD bounds, int pasteNumber)
    {
        Entries = entries;
        Bounds = bounds;
        PasteNumber = pasteNumber;
    }

    public ImmutableArray<Roi> Place(ImageReference image, PointD step, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        token.ThrowIfCancellationRequested();
        if (Entries.IsEmpty)
        {
            return [];
        }

        var placement = GetPlacement(image, step);
        var placed = ImmutableArray.CreateBuilder<Roi>(Entries.Length);
        foreach (var roi in Entries)
        {
            token.ThrowIfCancellationRequested();
            placed.Add(roi with
            {
                Geometry = placement.Apply(roi.Geometry)
            });
        }

        token.ThrowIfCancellationRequested();
        return placed.MoveToImmutable();
    }

    internal Placement GetPlacement(ImageReference image, PointD step)
    {
        var scale = Math.Min(1, Math.Min(image.Width / Bounds.Width, image.Height / Bounds.Height));
        var halfWidth = Bounds.Width * scale / 2;
        var halfHeight = Bounds.Height * scale / 2;
        var desired = Bounds.Center + (step * PasteNumber);
        var center = new PointD(
            Math.Clamp(desired.X, halfWidth, image.Width - halfWidth),
            Math.Clamp(desired.Y, halfHeight, image.Height - halfHeight));
        return new(Bounds.Center, center, scale);
    }

    internal readonly record struct Placement(PointD SourceCenter, PointD TargetCenter, double Scale)
    {
        public RoiGeometry Apply(RoiGeometry geometry)
            => geometry.WithFrame(
                TargetCenter + ((geometry.Center - SourceCenter) * Scale),
                geometry.Width * Scale,
                geometry.Height * Scale,
                geometry.Angle);
    }
}
