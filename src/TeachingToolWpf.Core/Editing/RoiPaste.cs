using System.Collections.Immutable;

namespace TeachingToolWpf.Core.Editing;

/// <summary>Prepares an immutable paste result without touching the session, UI or history.</summary>
internal static class RoiPaste
{
    public static DocumentState Prepare(DocumentState before, ImmutableArray<Roi> templates, long order, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var ids = templates.ToDictionary(r => r.Id, _ => Guid.NewGuid());
        var rois = before.Rois.ToBuilder();
        var roots = ImmutableHashSet.CreateBuilder<Guid>();
        foreach (var template in templates)
        {
            token.ThrowIfCancellationRequested();
            var id = ids[template.Id];
            if (template.ParentId is not { } parent || !ids.ContainsKey(parent))
            {
                roots.Add(id);
            }

            rois.Add(id, template with
            {
                Id = id,
                ParentId = null,
                Order = ++order,
                Name = template.Name[..Math.Min(template.Name.Length, 253)] + " 복사"
            });
        }
        var result = RoiHierarchy.Rebuild(before, before.WithRois(rois.ToImmutable(), ids.Values) with
        {
            Selection = roots.ToImmutable()
        }, token);
        token.ThrowIfCancellationRequested();
        return result;
    }
}
