using VisionCanvas.Core.Editing;

namespace TeachingToolWpf.Presentation;

public sealed record RoiRow(Guid Id, string Label, string Kind, int Depth)
{
    internal static string Describe(Roi roi, int children) => roi.IsGroup ? $"그룹 {children}" : children > 0 ? $"부모 {children}"
        : $"{(roi.ParentId is null ? "부모" : "자식")} · {roi.Geometry.ShapeName}";

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
        var pending = new Stack<IEnumerator<Roi>>();
        pending.Push(state.Rois.Values.Where(r => r.ParentId is null).OrderBy(r => r.Order).GetEnumerator());
        try
        {
            // 형제 순회만 깊이별로 보관해 큰 목록 복사와 깊은 계층의 호출 스택 고갈을 피한다.
            while (pending.TryPeek(out var siblings))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!siblings.MoveNext())
                {
                    pending.Pop().Dispose();
                    continue;
                }

                var roi = siblings.Current;
                var directChildren = children[roi.Id];
                var count = directChildren.Count();
                result.Add(new(roi.Id, roi.Name, Describe(roi, count), pending.Count - 1));
                if (count > 0)
                {
                    pending.Push(directChildren.OrderBy(r => r.Order).GetEnumerator());
                }
            }
        }
        finally
        {
            while (pending.TryPop(out var siblings))
            {
                siblings.Dispose();
            }
        }
        return result;
    }
}
