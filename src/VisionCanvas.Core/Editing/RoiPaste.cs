using System.Collections.Immutable;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Core.Editing;

/// <summary>Prepares an immutable paste result without touching the session, UI or history.</summary>
internal static class RoiPaste
{
    public static DocumentState Prepare(
        DocumentState before,
        RoiClipboardContent content,
        ImageReference image,
        PointD step,
        long order,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var templates = content.Entries;
        var placement = content.GetPlacement(image, step);
        var ids = templates.ToDictionary(r => r.Id, _ => Guid.NewGuid());
        var rois = before.Rois.ToBuilder();
        var roots = new HashSet<Guid>();
        foreach (var template in templates)
        {
            token.ThrowIfCancellationRequested();
            var id = ids[template.Id];
            if (template.ParentId is not { } parent || !ids.ContainsKey(parent))
            {
                roots.Add(id);
            }

            Guid? resolvedParent = null;
            if (template.ParentId is { } originalParent && ids.TryGetValue(originalParent, out var copiedParent))
            {
                resolvedParent = copiedParent;
            }

            rois.Add(id, template with
            {
                Id = id,
                Geometry = placement.Apply(template.Geometry),
                ParentId = resolvedParent,
                Order = ++order,
                Name = RoiName.CreateCopyName(template.Name)
            });
        }

        foreach (var id in ids.Values)
        {
            token.ThrowIfCancellationRequested();
            if (RoiHierarchy.Depth(rois, id) > RoiHierarchy.MaximumDepth)
            {
                throw new InvalidOperationException("ROI 계층은 RootGroup을 포함하여 최대 4단계입니다.");
            }
        }

        var result = before.WithRois(rois.ToImmutable(), ids.Values)
            .WithSelection(RoiIdSet.CreateRange(roots));
        token.ThrowIfCancellationRequested();
        return result;
    }
}
