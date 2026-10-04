using System.Buffers;
using System.Collections.Immutable;
using System.Numerics;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

internal readonly record struct PoseCandidate(MatchPose Pose, double Score, int Template);
internal sealed record CandidateBatch(List<PoseCandidate> Candidates, bool BudgetExceeded, int EvaluatedTemplates);

internal static class CandidateSearch
{
    public static CandidateBatch Find(PreparedSearchImage image, ShapeModel model, SearchOptions options, CancellationToken token)
    {
        var top = model.Levels.Length - 1;
        var factor = model.Levels[top].Factor;
        var map = image.Maps[top];
        var gridWidth = (map.Width + map.Step - 1) / map.Step;
        var gridHeight = (map.Height + map.Step - 1) / map.Step;
        var groups = MakeGroups(model, top, options.GroupTemplates);
        var candidates = new PriorityQueue<PoseCandidate, double>();
        var exceeded = false;
        var scores = ArrayPool<ushort>.Shared.Rent(checked(gridWidth * gridHeight));
        try
        {
            foreach (var group in groups)
            {
                token.ThrowIfCancellationRequested();
                Array.Clear(scores, 0, gridWidth * gridHeight);
                var features = model.Templates[group[0]].Levels[top];
                Accumulate(map, features, scores, gridWidth, gridHeight, options.UseSimd, token);
                var threshold = options.CandidateThreshold * 100 * features.Length;
                for (var y = 0; y < gridHeight; y++)
                {
                    for (var x = 0; x < gridWidth; x++)
                    {
                        var i = (y * gridWidth) + x;
                        if (scores[i] < threshold || !IsMaximum(scores, gridWidth, gridHeight, x, y))
                        {
                            continue;
                        }
                        foreach (var index in group)
                        {
                            var template = model.Templates[index];
                            var candidate = new PoseCandidate(new(((x * map.Step) + 0.5) * factor,
                                ((y * map.Step) + 0.5) * factor, template.Angle, template.Scale),
                                scores[i] / (100d * features.Length), index);
                            candidates.Enqueue(candidate, candidate.Score);
                            if (candidates.Count > options.MaximumCandidates)
                            {
                                candidates.Dequeue();
                                exceeded = true;
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            ArrayPool<ushort>.Shared.Return(scores);
        }
        var refined = new List<PoseCandidate>(candidates.Count);
        while (candidates.TryDequeue(out var candidate, out _))
        {
            token.ThrowIfCancellationRequested();
            for (var level = top - 1; level >= 0; level--)
            {
                var nextFactor = model.Levels[level].Factor;
                var cx = (int)Math.Round((candidate.Pose.X / nextFactor) - 0.5);
                var cy = (int)Math.Round((candidate.Pose.Y / nextFactor) - 0.5);
                var bestScore = -1d;
                var bestX = cx;
                var bestY = cy;
                for (var y = cy - 4; y <= cy + 4; y++)
                {
                    for (var x = cx - 4; x <= cx + 4; x++)
                    {
                        if (x < 0 || y < 0 || x >= image.Maps[level].Width || y >= image.Maps[level].Height)
                        {
                            continue;
                        }
                        var score = Score(image.Maps[level], model.Templates[candidate.Template].Levels[level], x, y);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestX = x;
                            bestY = y;
                        }
                    }
                }
                candidate = candidate with
                {
                    Pose = candidate.Pose with
                    {
                        X = (bestX + 0.5) * nextFactor,
                        Y = (bestY + 0.5) * nextFactor
                    },
                    Score = bestScore
                };
            }
            if (candidate.Score >= options.CandidateThreshold)
            {
                refined.Add(candidate);
            }
        }
        refined.Sort((a, b) => b.Score != a.Score ? b.Score.CompareTo(a.Score) :
            a.Pose.Y != b.Pose.Y ? a.Pose.Y.CompareTo(b.Pose.Y) :
            a.Pose.X != b.Pose.X ? a.Pose.X.CompareTo(b.Pose.X) : a.Template.CompareTo(b.Template));
        return new(refined, exceeded, groups.Count);
    }

    internal static void Accumulate(OrientationMaps map, ImmutableArray<QuantizedFeature> features,
        ushort[] scores, int gridWidth, int gridHeight, bool simd, CancellationToken token)
    {
        foreach (var feature in features)
        {
            token.ThrowIfCancellationRequested();
            var startX = Math.Max(0, (int)Math.Ceiling(-feature.X / (double)map.Step));
            var endX = Math.Min(gridWidth, (int)Math.Ceiling((map.Width - feature.X) / (double)map.Step));
            var startY = Math.Max(0, (int)Math.Ceiling(-feature.Y / (double)map.Step));
            var endY = Math.Min(gridHeight, (int)Math.Ceiling((map.Height - feature.Y) / (double)map.Step));
            if (startX >= endX)
            {
                continue;
            }
            for (var y = startY; y < endY; y++)
            {
                var values = map.Row(feature.Bin, (startX * map.Step) + feature.X, (y * map.Step) + feature.Y, endX - startX);
                var output = scores.AsSpan((y * gridWidth) + startX, endX - startX);
                var x = 0;
                if (simd && Vector.IsHardwareAccelerated)
                {
                    for (; x <= values.Length - Vector<byte>.Count; x += Vector<byte>.Count)
                    {
                        Vector.Widen(new Vector<byte>(values.Slice(x)), out Vector<ushort> low, out Vector<ushort> high);
                        (new Vector<ushort>(output.Slice(x)) + low).CopyTo(output.Slice(x));
                        var next = x + Vector<ushort>.Count;
                        (new Vector<ushort>(output.Slice(next)) + high).CopyTo(output.Slice(next));
                    }
                }
                for (; x < values.Length; x++)
                {
                    output[x] += values[x];
                }
            }
        }
    }

    private static double Score(OrientationMaps map, ImmutableArray<QuantizedFeature> features, int x, int y)
    {
        var sum = 0;
        foreach (var feature in features)
        {
            sum += map.At(feature.Bin, x + feature.X, y + feature.Y);
        }
        return sum / (100d * features.Length);
    }

    private static bool IsMaximum(ushort[] scores, int width, int height, int x, int y)
    {
        var i = (y * width) + x;
        for (var dy = Math.Max(0, y - 1); dy <= Math.Min(height - 1, y + 1); dy++)
        {
            for (var dx = Math.Max(0, x - 1); dx <= Math.Min(width - 1, x + 1); dx++)
            {
                var j = (dy * width) + dx;
                if (scores[j] > scores[i] || (scores[j] == scores[i] && j < i))
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static List<List<int>> MakeGroups(ShapeModel model, int level, bool enabled)
    {
        var groups = new List<List<int>>();
        var keys = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < model.Templates.Length; i++)
        {
            if (!enabled)
            {
                groups.Add([i]);
                continue;
            }
            // P3 착안의 보수적인 제한판: 완전히 같은 양자화 템플릿만 묶으므로 모든 멤버의 점수가 동일하다.
            var key = string.Join(';', model.Templates[i].Levels[level]
                .OrderBy(f => f.X).ThenBy(f => f.Y).ThenBy(f => f.Bin).Select(f => $"{f.X},{f.Y},{f.Bin}"));
            if (!keys.TryGetValue(key, out var group))
            {
                group = [];
                keys.Add(key, group);
                groups.Add(group);
            }
            group.Add(i);
        }
        return groups;
    }
}
