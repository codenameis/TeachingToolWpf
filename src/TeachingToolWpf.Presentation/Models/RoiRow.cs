using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Media;
using VisionCanvas.Core.Editing;
using VisionGeometry;

namespace TeachingToolWpf.Models;

public sealed record RoiRow(Guid Id, string Label, string Kind, int Depth)
{
    public string ShapeGlyph { get; init; } = "▭";

    public string AlgorithmBadge { get; init; } = "미지정";

    public string AlgorithmAccent { get; init; } = "#64748B";

    public string VerdictBadge { get; init; } = "";

    public string VerdictAccent { get; init; } = "#64748B";

    public ImageSource? PatternThumbnail { get; init; }

    public bool HasAlgorithm => !string.IsNullOrEmpty(AlgorithmBadge) && AlgorithmBadge != "미지정";

    public bool HasVerdict => !string.IsNullOrEmpty(VerdictBadge);

    public bool HasPatternThumbnail => PatternThumbnail is not null;

    internal static string Describe(Roi roi, int children, int level) => roi.IsGroup || children > 0
        ? $"{RoiHierarchy.LevelName(Math.Max(1, level))} {children}"
        : $"{(roi.SearchRange is null ? "일반" : "검색 범위")} · {roi.Geometry.ShapeName}";

    internal static string ResolveShapeGlyph(Roi roi, int children)
    {
        if (roi.IsGroup || children > 0)
        {
            return "📁";
        }

        return roi.Geometry.Shape.Kind switch
        {
            RoiShapeKind.Circle => "◯",
            RoiShapeKind.Triangle => "△",
            RoiShapeKind.Polygon => "⬠",
            _ => "▭"
        };
    }

    public double IndentWidth => Depth * 12;

    public string IndentedLabel => new string(' ', Depth * 3) + (Depth == 0 ? "▣  " : "└  ") + Label;

    public static IReadOnlyList<RoiRow> Build(
        DocumentState state,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<Guid, RoiAlgorithmAssignment>? assignments = null)
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
                var row = new RoiRow(
                    roi.Id,
                    roi.Name,
                    Describe(roi, count, state.Snapshot.GroupLevels.GetValueOrDefault(roi.Id)),
                    pending.Count - 1)
                {
                    ShapeGlyph = ResolveShapeGlyph(roi, count)
                };

                if (assignments is not null && assignments.TryGetValue(roi.Id, out var assignment))
                {
                    row = row with
                    {
                        AlgorithmBadge = assignment.AlgorithmBadge,
                        AlgorithmAccent = assignment.AlgorithmAccent,
                        VerdictBadge = assignment.VerdictBadge,
                        VerdictAccent = assignment.VerdictAccent,
                        PatternThumbnail = assignment.PatternThumbnail
                    };
                }

                result.Add(row);
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
