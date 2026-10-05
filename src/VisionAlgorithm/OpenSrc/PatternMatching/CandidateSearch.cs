using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

internal readonly record struct PoseCandidate(MatchPose Pose, double Score, int Template, VerifiedScore? Verification = null);
internal sealed record CandidateBatch(List<PoseCandidate> Candidates, bool BudgetExceeded, int EvaluatedTemplates)
{
    public double GridMilliseconds
    {
        get; init;
    }
    public double PoseMilliseconds
    {
        get; init;
    }
    public int CandidatesBeforeRefinement
    {
        get; init;
    }
}

internal static class CandidateSearch
{
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static CandidateBatch Find(PreparedSearchImage image, ShapeModel model, SearchOptions options, CancellationToken token,
        IShapeCandidateScorer? scorer = null, bool optimizedLocalSearch = false)
    {
        var timer = Stopwatch.StartNew();
        var top = model.Levels.Length - 1;
        var factor = model.Levels[top].Factor;
        var map = image.Maps[top];
        var gridWidth = (map.Width + map.Step - 1) / map.Step;
        var gridHeight = (map.Height + map.Step - 1) / map.Step;
        var groups = MakeGroups(model, top, options.GroupTemplates);
        var candidates = new PriorityQueue<PoseCandidate, double>();
        var exceeded = false;
        var gridLength = checked(gridWidth * gridHeight);
        var scores = scorer is null ? ArrayPool<ushort>.Shared.Rent(gridLength) : [];
        var groupsPerBatch = Math.Max(1, ShapeCandidateScoreRequest.MaximumScoreCount / gridLength);
        ShapeCandidateScoreBatch? batch = null;
        try
        {
            for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
            {
                token.ThrowIfCancellationRequested();
                var group = groups[groupIndex];
                var features = model.Templates[group[0]].Levels[top];
                ReadOnlySpan<ushort> currentScores;
                if (scorer is null)
                {
                    Array.Clear(scores, 0, gridLength);
                    Accumulate(map, features, scores, gridWidth, gridHeight, options.UseSimd, token);
                    currentScores = scores.AsSpan(0, gridLength);
                }
                else
                {
                    var batchIndex = groupIndex % groupsPerBatch;
                    if (batchIndex == 0)
                    {
                        var count = Math.Min(groupsPerBatch, groups.Count - groupIndex);
                        var templates = ImmutableArray.CreateBuilder<ImmutableArray<CandidateScoreFeature>>(count);
                        for (var index = 0; index < count; index++)
                        {
                            templates.Add(model.Templates[groups[groupIndex + index][0]].Levels[top]
                                .Select(feature => new CandidateScoreFeature(feature.X, feature.Y, feature.Bin)).ToImmutableArray());
                        }
                        batch = scorer.Score(new(image.Gradients[top].Bits, map.Width, map.Height, 2,
                            map.Step, templates.MoveToImmutable()), token);
                        if (batch is null || batch.GridWidth != gridWidth || batch.GridHeight != gridHeight || batch.TemplateCount != count ||
                            batch.Scores is null || batch.Scores.Length != checked(gridLength * count))
                        {
                            throw new InvalidOperationException("후보 점수 공급자가 요청과 다른 크기의 격자를 반환했습니다.");
                        }
                    }
                    currentScores = batch!.Scores.AsSpan(batchIndex * gridLength, gridLength);
                }
                var threshold = options.CandidateThreshold * 100 * features.Length;
                var maximumSum = features.Length * 100;
                for (var y = 0; y < gridHeight; y++)
                {
                    for (var x = 0; x < gridWidth; x++)
                    {
                        var i = (y * gridWidth) + x;
                        if (scorer is not null && currentScores[i] > maximumSum)
                        {
                            throw new InvalidOperationException("후보 점수 공급자가 특징 수의 최대 정수 합을 넘는 값을 반환했습니다.");
                        }
                        if (currentScores[i] < threshold || !IsMaximum(currentScores, gridWidth, gridHeight, x, y))
                        {
                            continue;
                        }
                        foreach (var index in group)
                        {
                            var template = model.Templates[index];
                            var candidate = new PoseCandidate(new(((x * map.Step) + 0.5) * factor,
                                ((y * map.Step) + 0.5) * factor, template.Angle, template.Scale),
                                currentScores[i] / (100d * features.Length), index);
                            if (!(options.Region?.ContainsWithin(candidate.Pose.X, candidate.Pose.Y, 4 * factor) ?? true))
                            {
                                continue;
                            }
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
            if (scorer is null)
            {
                ArrayPool<ushort>.Shared.Return(scores);
            }
        }
        var gridTime = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        var ordered = new PoseCandidate[candidates.Count];
        for (var index = 0; index < ordered.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            ordered[index] = candidates.Dequeue();
        }
        if (options.MaximumParallelism == 1 || ordered.Length <= 1)
        {
            for (var index = 0; index < ordered.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                ordered[index] = Refine(image, model, ordered[index], options, token, optimizedLocalSearch);
            }
        }
        else
        {
            // 전처리는 불변이며 각 worker는 고정된 결과 슬롯만 쓴다. 완료 순서가 후보 우선순위를 바꾸지 않는다.
            Parallel.For(0, ordered.Length, new ParallelOptions
            {
                MaxDegreeOfParallelism = options.MaximumParallelism,
                CancellationToken = token
            }, index =>
            {
                ordered[index] = Refine(image, model, ordered[index], options, token, optimizedLocalSearch);
            });
        }
        var refined = new List<PoseCandidate>(ordered.Length);
        foreach (var candidate in ordered)
        {
            token.ThrowIfCancellationRequested();
            if (candidate.Verification is { } verification && (verification.Score < options.MinimumScore || verification.Support < options.MinimumSupport))
            {
                continue;
            }
            if (options.Verify || candidate.Score >= options.CandidateThreshold)
            {
                refined.Add(candidate);
            }
        }
        refined.Sort((a, b) => (b.Verification?.Score ?? b.Score) != (a.Verification?.Score ?? a.Score)
            ? (b.Verification?.Score ?? b.Score).CompareTo(a.Verification?.Score ?? a.Score) :
            a.Pose.Y != b.Pose.Y ? a.Pose.Y.CompareTo(b.Pose.Y) :
            a.Pose.X != b.Pose.X ? a.Pose.X.CompareTo(b.Pose.X) : a.Template.CompareTo(b.Template));
        // 다른 템플릿에서 완전히 같은 연속 자세로 수렴한 후보는 동일한 정밀화 결과를 낸다.
        // 근접 자세를 반올림하거나 IoU로 미리 제거하지 않는다.
        return new(refined.DistinctBy(candidate => candidate.Pose).ToList(), exceeded, groups.Count)
        {
            GridMilliseconds = gridTime,
            PoseMilliseconds = timer.Elapsed.TotalMilliseconds,
            CandidatesBeforeRefinement = ordered.Length
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static PoseCandidate Refine(PreparedSearchImage image, ShapeModel model, PoseCandidate candidate,
        SearchOptions options, CancellationToken token, bool optimizedLocalSearch = false)
    {
        Span<ushort> localScores = stackalloc ushort[81];
        // 각 단계에서 위치뿐 아니라 연속 각도·배율도 보정한 후 다음 해상도로 내려간다.
        for (var level = model.Levels.Length - 1; level >= 0; level--)
        {
            token.ThrowIfCancellationRequested();
            var factor = model.Levels[level].Factor;
            var pose = candidate.Pose with
            {
                X = candidate.Pose.X / factor,
                Y = candidate.Pose.Y / factor
            };
            var cx = (int)Math.Round(pose.X - 0.5);
            var cy = (int)Math.Round(pose.Y - 0.5);
            var cos = Math.Cos(pose.Angle);
            var sin = Math.Sin(pose.Angle);
            var features = optimizedLocalSearch && level == model.Levels.Length - 1
                ? model.Templates[candidate.Template].Levels[level]
                : model.Levels[level].Features.Select(feature => new QuantizedFeature(
                (int)Math.Round(pose.Scale * ((cos * feature.X) - (sin * feature.Y))),
                (int)Math.Round(pose.Scale * ((sin * feature.X) + (cos * feature.Y))),
                OrientationMaps.Quantize((cos * feature.NormalX) - (sin * feature.NormalY),
                    (sin * feature.NormalX) + (cos * feature.NormalY)))).ToImmutableArray();
            var bestSum = -1;
            var left = Math.Max(0, cx - 4);
            var top = Math.Max(0, cy - 4);
            var right = Math.Min(image.Maps[level].Width - 1, cx + 4);
            var bottom = Math.Min(image.Maps[level].Height - 1, cy + 4);
            var width = right - left + 1;
            if (optimizedLocalSearch)
            {
                image.Maps[level].AccumulateLocal(features, left, top, width, bottom - top + 1, localScores, options.UseSimd);
            }
            for (var y = top; y <= bottom; y++)
            {
                for (var x = left; x <= right; x++)
                {
                    var score = optimizedLocalSearch ? localScores[((y - top) * width) + x - left]
                        : Score(image.Maps[level], features, x, y, bestSum);
                    if (score > bestSum)
                    {
                        bestSum = score;
                        pose = pose with
                        {
                            X = x + 0.5,
                            Y = y + 0.5
                        };
                    }
                }
            }
            if (options.Verify)
            {
                var result = ContinuousVerifier.Optimize(image.Gradients[level], model, pose, options, token, level);
                pose = result.Pose;
                if (level == 0)
                {
                    candidate = candidate with
                    {
                        Verification = result.Score
                    };
                }
            }
            candidate = candidate with
            {
                Pose = pose with
                {
                    X = pose.X * factor,
                    Y = pose.Y * factor
                },
                Score = bestSum / (100d * features.Length)
            };
        }
        return candidate;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
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

    private static int Score(OrientationMaps map, ImmutableArray<QuantizedFeature> features, int x, int y, int minimumSum)
    {
        var sum = 0;
        for (var index = 0; index < features.Length; index++)
        {
            var feature = features[index];
            sum += map.At(feature.Bin, x + feature.X, y + feature.Y);
            if ((index & 7) == 7 && sum + ((features.Length - index - 1) * 100) <= minimumSum)
            {
                return -1;
            }
        }
        return sum;
    }

    private static bool IsMaximum(ReadOnlySpan<ushort> scores, int width, int height, int x, int y)
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
