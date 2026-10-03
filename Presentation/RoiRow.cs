using TeachingToolWpf.Core.Editing;

namespace TeachingToolWpf.Presentation;

public sealed record RoiRow(Guid Id, string Label, string Kind, int Depth)
{
    internal static string Describe(Roi roi, int children) => roi.IsGroup ? $"그룹 {children}" : children > 0 ? $"부모 {children}"
        : roi.Geometry.Shape.Kind == Core.Geometry.RoiShapeKind.Rectangle ? "ROI" : roi.Geometry.ShapeName;

    public double IndentWidth => Depth * 12;

    public string IndentedLabel => new string(' ', Depth * 3) + (Depth == 0 ? "▣  " : "└  ") + Label;

    public static IReadOnlyList<RoiRow> Build(DocumentState state, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var children = state.Rois.Values.Where(r =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return r.ParentId is not null;
        }).ToLookup(r => r.ParentId!.Value);
        var result = new List<RoiRow>(state.Rois.Count);
        void Add(Roi roi, int depth)
        {
            var count = children[roi.Id].Count();
            result.Add(new(roi.Id, roi.Name, Describe(roi, count), depth));
            foreach (var child in children[roi.Id].OrderBy(r => r.Order))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Add(child, depth + 1);
            }
        }
        foreach (var roi in state.Rois.Values.Where(r => r.ParentId is null).OrderBy(r => r.Order))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Add(roi, 0);
        }
        return result;
    }
}
