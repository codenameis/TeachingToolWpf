using System.Collections.Immutable;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Core.Editing;

/// <summary>Prepares an immutable paste result without touching the session, UI or history.</summary>
internal static class RoiPaste
{
    public static DocumentState Prepare(DocumentState before, RoiClipboardContent content, ImageReference image,
        PointD step, long order, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var templates = content.Entries;
        var placement = content.GetPlacement(image, step);
        var ids = templates.ToDictionary(r => r.Id, _ => Guid.NewGuid());
        var rois = before.Rois.ToBuilder();
        var roots = new HashSet<Guid>();
        var batches = before.RepeatedRois;
        var selected = ImmutableDictionary.CreateBuilder<Guid, RoiCellSet>();
        var repeatedIndex = 0;
        foreach (var template in templates)
        {
            AddRepeatedBefore(template.Order);
            token.ThrowIfCancellationRequested();
            var id = ids[template.Id];
            if (template.ParentId is not { } parent || !ids.ContainsKey(parent))
            {
                roots.Add(id);
            }

            rois.Add(id, template with
            {
                Id = id,
                Geometry = placement.Apply(template.Geometry),
                ParentId = template.ParentId is { } originalParent && ids.TryGetValue(originalParent, out var copiedParent) ? copiedParent : null,
                Order = ++order,
                Name = template.Name[..Math.Min(template.Name.Length, 253)] + " 복사"
            });
        }
        AddRepeatedBefore(long.MaxValue);
        var result = before.WithRois(rois.ToImmutable(), ids.Values) with
        {
            Selection = RoiIdSet.CreateRange(roots),
            RepeatedRois = batches,
            RepeatedSelection = selected.ToImmutable()
        };
        token.ThrowIfCancellationRequested();
        return result;

        void AddRepeatedBefore(long nextOrder)
        {
            while (repeatedIndex < content.Repeated.Length && content.Repeated[repeatedIndex].Order <= nextOrder)
            {
                token.ThrowIfCancellationRequested();
                var batch = content.Repeated[repeatedIndex++].Place(Guid.NewGuid(), ++order, placement);
                batches = batches.Add(batch.Id, batch);
                selected.Add(batch.Id, batch.Cells);
            }
        }
    }
}
