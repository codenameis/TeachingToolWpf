using System.Buffers;
using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

/// <summary>
/// Evaluates top-level quantized orientation-map grids across grouped pose templates, applies cell/anisotropic NMS,
/// and emits bounded priority-queue coarse candidates.
/// </summary>
internal static class CoarseLevelScanner
{
    private const int AnisotropicBucketCount = 8;
    private const int MinGroupsForStride2 = 8;
    private const int LargeGroupCountThreshold = 12;
    private const double FineDetailMaxCandidateGate = 0.84;
    private const double LargeGroupMinScoreOffset = 0.10;
    private const double SmallGroupMinScoreOffset = 0.05;
    private const double Stride2ThresholdDiscount = 0.05;
    private const double OddAngleRestoreMargin = 0.03;
    private const double MaxOccludedCellScore = 0.90;
    private const double TieEpsilon = 1e-9;
    private const int QuantizedFeatureMaxScore = 100;

    internal readonly record struct CoarseScanOutcome(
        PoseCandidate[] OrderedCandidates,
        bool BudgetExceeded,
        int EvaluatedTemplateGroups,
        bool IsAnisotropic);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static CoarseScanOutcome ScanTopLevel(
        PreparedSearchImage image,
        ShapeModel model,
        ShapeSearchOptions options,
        CancellationToken token,
        IShapeCandidateScorer? scorer)
    {
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
        var filterAngleSidelobes = (model.FineDetail is not null || (model.Options.BuildClutterModel && model.Options.UseFineDetailValidation)) && groups.Count > 1;
        var anisotropic = filterAngleSidelobes && model.FineDetail is null;
        var angleBucketCount = anisotropic ? AnisotropicBucketCount : (filterAngleSidelobes ? 1 : 0);
        var cellBuckets = new (double Score, int Group)[angleBucketCount][];
        for (var b = 0; b < angleBucketCount; b++)
        {
            cellBuckets[b] = new (double Score, int Group)[gridLength];
            Array.Fill(cellBuckets[b], (double.NegativeInfinity, -1));
        }

        var groupsPerBatch = Math.Max(1, ShapeCandidateScoreRequest.MaximumScoreCount / gridLength);
        var stride2 = scorer is null &&
                      anisotropic &&
                      groups.Count >= MinGroupsForStride2 &&
                      model.Options.MinimumScale == model.Options.MaximumScale;
        var referenceGroup = FindReferenceGroup(model, groups);
        var order = Enumerable.Range(0, groups.Count)
            .Where(index => !stride2 || index % 2 == 0 || Math.Abs(index - referenceGroup) <= 1)
            .ToArray();

        ShapeCandidateScoreBatch? batch = null;
        var chunkBuffers = scorer is null && options.MaximumParallelism > 1 && order.Length > 1
            ? new ushort[Math.Min(options.MaximumParallelism, order.Length)][]
            : null;
        if (chunkBuffers is not null)
        {
            for (var slot = 0; slot < chunkBuffers.Length; slot++)
            {
                chunkBuffers[slot] = ArrayPool<ushort>.Shared.Rent(gridLength);
            }
        }

        try
        {
            for (var orderIndex = 0; orderIndex < order.Length; orderIndex++)
            {
                var groupIndex = order[orderIndex];
                token.ThrowIfCancellationRequested();
                var group = groups[groupIndex];
                var features = model.Templates[group[0]].Levels[top];
                ReadOnlySpan<ushort> currentScores;
                if (chunkBuffers is not null)
                {
                    var slot = orderIndex % chunkBuffers.Length;
                    if (slot == 0)
                    {
                        var chunkStart = orderIndex;
                        Parallel.For(
                            0,
                            Math.Min(chunkBuffers.Length, order.Length - chunkStart),
                            new ParallelOptions
                            {
                                MaxDegreeOfParallelism = chunkBuffers.Length,
                                CancellationToken = token
                            },
                            k =>
                            {
                                Array.Clear(chunkBuffers[k], 0, gridLength);
                                Accumulate(
                                    map,
                                    model.Templates[groups[order[chunkStart + k]][0]].Levels[top],
                                    chunkBuffers[k],
                                    gridWidth,
                                    gridHeight,
                                    options.UseSimd,
                                    token);
                            });
                    }

                    currentScores = chunkBuffers[slot].AsSpan(0, gridLength);
                }
                else if (scorer is null)
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
                                .Select(static feature => new CandidateScoreFeature(feature.X, feature.Y, feature.Bin))
                                .ToImmutableArray());
                        }

                        batch = scorer.Score(
                            new(image.Gradients[top].Bits, map.Width, map.Height, 2, map.Step, templates.MoveToImmutable()),
                            token);
                        if (batch is null ||
                            batch.GridWidth != gridWidth ||
                            batch.GridHeight != gridHeight ||
                            batch.TemplateCount != count ||
                            batch.Scores is null ||
                            batch.Scores.Length != checked(gridLength * count))
                        {
                            throw new InvalidOperationException("후보 점수 공급자가 요청과 다른 크기의 격자를 반환했습니다.");
                        }
                    }

                    currentScores = batch!.Scores.AsSpan(batchIndex * gridLength, gridLength);
                }

                var effectiveCandidateThreshold = filterAngleSidelobes && model.FineDetail is not null
                    ? Math.Max(
                        options.CandidateThreshold,
                        Math.Min(FineDetailMaxCandidateGate, options.MinimumScore + (groups.Count > LargeGroupCountThreshold ? LargeGroupMinScoreOffset : SmallGroupMinScoreOffset)))
                    : options.CandidateThreshold - (stride2 ? Stride2ThresholdDiscount : 0);
                var threshold = effectiveCandidateThreshold * QuantizedFeatureMaxScore * features.Length;
                var maximumSum = features.Length * QuantizedFeatureMaxScore;
                var bucketIndex = anisotropic ? PoseClusterer.GetAngleBucket(model, group[0]) : 0;

                for (var y = 0; y < gridHeight; y++)
                {
                    for (var x = 0; x < gridWidth; x++)
                    {
                        var i = (y * gridWidth) + x;
                        if (scorer is not null && currentScores[i] > maximumSum)
                        {
                            throw new InvalidOperationException("후보 점수 공급자가 특징 수의 최대 정수 합을 넘는 값을 반환했습니다.");
                        }

                        if (currentScores[i] < threshold)
                        {
                            continue;
                        }

                        var normalizedScore = currentScores[i] / (100d * features.Length);
                        if (filterAngleSidelobes)
                        {
                            var cellBest = cellBuckets[bucketIndex];
                            var betterPose = cellBest[i].Group >= 0 &&
                                (Math.Abs(model.Templates[group[0]].Angle - model.TrainingReferenceAngle) +
                                 Math.Abs(model.Templates[group[0]].Scale - 1) <
                                 Math.Abs(model.Templates[groups[cellBest[i].Group][0]].Angle - model.TrainingReferenceAngle) +
                                 Math.Abs(model.Templates[groups[cellBest[i].Group][0]].Scale - 1));
                            if (normalizedScore > cellBest[i].Score + TieEpsilon ||
                                (Math.Abs(normalizedScore - cellBest[i].Score) <= TieEpsilon && betterPose))
                            {
                                cellBest[i] = (normalizedScore, groupIndex);
                            }

                            continue;
                        }

                        if (!IsMaximum(currentScores, gridWidth, gridHeight, x, y))
                        {
                            continue;
                        }

                        EnqueueGroup(groups[groupIndex], model, x, y, map.Step, factor, normalizedScore, options, candidates, ref exceeded);
                    }
                }
            }

            for (var bucketIndex = 0; bucketIndex < angleBucketCount; bucketIndex++)
            {
                var cellBest = cellBuckets[bucketIndex];
                for (var y = 0; y < gridHeight; y++)
                {
                    for (var x = 0; x < gridWidth; x++)
                    {
                        var i = (y * gridWidth) + x;
                        var cell = cellBest[i];
                        if (cell.Group < 0)
                        {
                            continue;
                        }

                        var isMax = anisotropic
                            ? IsAnisotropicMaximum(cellBest, gridWidth, gridHeight, x, y, cell.Group, model, groups, map.Step * factor)
                            : IsCellBestMaximum(cellBest, gridWidth, gridHeight, x, y, model.FineDetail is not null ? 2 : 1);
                        if (!isMax)
                        {
                            if (anisotropic &&
                                !model.FeaturePartitions.IsDefault &&
                                model.Options.UseFineDetailValidation &&
                                (options.MaxOcclusionRatio > 0 || !model.FeatureAsymmetryFlags.IsDefault) &&
                                cell.Score >= options.CandidateThreshold - OddAngleRestoreMargin &&
                                cell.Score <= MaxOccludedCellScore)
                            {
                                var recovered = OccludedCandidateRecovery.TryRecoverCoarseCell(
                                    image,
                                    map,
                                    model,
                                    groups,
                                    top,
                                    gridWidth,
                                    gridHeight,
                                    cellBest,
                                    x,
                                    y,
                                    cell,
                                    bucketIndex,
                                    stride2,
                                    options.MaxOcclusionRatio);
                                if (recovered.Succeeded)
                                {
                                    EnqueueGroup(
                                        groups[recovered.Cell.Group],
                                        model,
                                        x,
                                        y,
                                        map.Step,
                                        factor,
                                        recovered.EffectiveScore,
                                        options,
                                        candidates,
                                        ref exceeded,
                                        anisotropic);
                                }
                            }

                            continue;
                        }

                        if (stride2)
                        {
                            cell = RestoreOddAngles(map, model, groups, top, x, y, cell, bucketIndex);
                            if (cell.Score < options.CandidateThreshold - OddAngleRestoreMargin)
                            {
                                continue;
                            }
                        }

                        EnqueueGroup(groups[cell.Group], model, x, y, map.Step, factor, cell.Score, options, candidates, ref exceeded, anisotropic);
                    }
                }
            }
        }
        finally
        {
            if (chunkBuffers is not null)
            {
                foreach (var buffer in chunkBuffers)
                {
                    if (buffer is not null)
                    {
                        ArrayPool<ushort>.Shared.Return(buffer);
                    }
                }
            }

            if (scorer is null)
            {
                ArrayPool<ushort>.Shared.Return(scores);
            }
        }

        var ordered = new PoseCandidate[candidates.Count];
        for (var index = 0; index < ordered.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            ordered[index] = candidates.Dequeue();
        }

        return new CoarseScanOutcome(ordered, exceeded, groups.Count, anisotropic);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Accumulate(
        OrientationMaps map,
        ImmutableArray<QuantizedFeature> features,
        ushort[] scores,
        int gridWidth,
        int gridHeight,
        bool simd,
        CancellationToken token)
    {
        // [Perf Why]: Widen byte orientation responses into Vector<ushort> lanes to sum grid rows without scalar branching.
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

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static int ScoreQuantized(
        OrientationMaps map,
        ReadOnlySpan<QuantizedFeature> features,
        int x,
        int y,
        int minimumSum,
        double greediness = 0)
    {
        var sum = 0;
        var count = features.Length;
        for (var index = 0; index < count; index++)
        {
            var feature = features[index];
            sum += map.At(feature.Bin, x + feature.X, y + feature.Y);
            if ((index & 3) == 3 && minimumSum >= 0)
            {
                var remainingMax = (count - index - 1) * QuantizedFeatureMaxScore;
                if (greediness <= 0)
                {
                    if (sum + remainingMax <= minimumSum)
                    {
                        return -1;
                    }
                }
                else
                {
                    var safeThreshold = minimumSum - remainingMax;
                    var linearThreshold = minimumSum * ((index + 1) / (double)count);
                    if (sum <= ((1.0 - greediness) * safeThreshold) + (greediness * linearThreshold))
                    {
                        return -1;
                    }
                }
            }
        }

        return sum;
    }

    public static (double Score, int Group) RestoreOddAngles(
        OrientationMaps map,
        ShapeModel model,
        List<List<int>> groups,
        int top,
        int x,
        int y,
        (double Score, int Group) current,
        int bucketIndex)
    {
        var best = current;
        Span<int> neighbors = [current.Group - 1, current.Group + 1];
        foreach (var neighbor in neighbors)
        {
            if (neighbor < 0 || neighbor >= groups.Count || PoseClusterer.GetAngleBucket(model, groups[neighbor][0]) != bucketIndex)
            {
                continue;
            }

            var features = model.Templates[groups[neighbor][0]].Levels[top];
            var normalized = ScoreQuantized(map, features.AsSpan(), x * map.Step, y * map.Step, -1) / (100d * features.Length);
            var betterPose = Math.Abs(model.Templates[groups[neighbor][0]].Angle - model.TrainingReferenceAngle) <
                             Math.Abs(model.Templates[groups[best.Group][0]].Angle - model.TrainingReferenceAngle);
            if (normalized > best.Score + TieEpsilon || (Math.Abs(normalized - best.Score) <= TieEpsilon && betterPose))
            {
                best = (normalized, neighbor);
            }
        }

        return best;
    }

    private static int FindReferenceGroup(ShapeModel model, List<List<int>> groups)
    {
        var referenceGroup = 0;
        var bestAngleDist = double.PositiveInfinity;
        for (var g = 0; g < groups.Count; g++)
        {
            var dist = Math.Abs(model.Templates[groups[g][0]].Angle - model.TrainingReferenceAngle);
            if (dist < bestAngleDist)
            {
                bestAngleDist = dist;
                referenceGroup = g;
            }
        }

        return referenceGroup;
    }

    private static void EnqueueGroup(
        List<int> group,
        ShapeModel model,
        int x,
        int y,
        int step,
        int factor,
        double score,
        ShapeSearchOptions options,
        PriorityQueue<PoseCandidate, double> candidates,
        ref bool exceeded,
        bool anisotropic = false)
    {
        var maxQueue = anisotropic ? options.MaximumCandidates * 4 : options.MaximumCandidates;
        foreach (var index in group)
        {
            var template = model.Templates[index];
            var candidate = new PoseCandidate(
                new(((x * step) + 0.5) * factor, ((y * step) + 0.5) * factor, template.Angle, template.Scale),
                score,
                index);
            if (!(options.Region?.ContainsWithin(candidate.Pose.X, candidate.Pose.Y, 4 * factor) ?? true))
            {
                continue;
            }

            candidates.Enqueue(candidate, candidate.Score);
            if (candidates.Count > maxQueue)
            {
                candidates.Dequeue();
                if (!anisotropic)
                {
                    exceeded = true;
                }
            }
        }
    }

    private static bool IsAnisotropicMaximum(
        ReadOnlySpan<(double Score, int Group)> cells,
        int width,
        int height,
        int x,
        int y,
        int group,
        ShapeModel model,
        List<List<int>> groups,
        int cellSize)
    {
        var template = model.Templates[groups[group][0]];
        var theta = template.Angle - model.TrainingReferenceAngle;
        var boundsWidth = model.BoundsWidth * template.Scale;
        var boundsHeight = model.BoundsHeight * template.Scale;
        var cos = Math.Cos(theta);
        var sin = Math.Sin(theta);
        var rWidth = Math.Min(2.2, Math.Max(1.0, Math.Ceiling(boundsWidth / 3.2 / cellSize)));
        var rHeight = Math.Min(1.2, Math.Max(1.0, Math.Floor(boundsHeight / 3.5 / cellSize)));
        var rx = Math.Min(3, Math.Max(1, (int)Math.Ceiling((Math.Abs(cos) * rWidth) + (Math.Abs(sin) * rHeight))));
        var ry = Math.Min(3, Math.Max(1, (int)Math.Ceiling((Math.Abs(sin) * rWidth) + (Math.Abs(cos) * rHeight))));
        var angleLimit = (model.Options.AngleStepDegrees * Math.PI / 180 * 4.01) + TieEpsilon;
        var i = (y * width) + x;
        var score = cells[i].Score;
        for (var dy = Math.Max(0, y - ry); dy <= Math.Min(height - 1, y + ry); dy++)
        {
            for (var dx = Math.Max(0, x - rx); dx <= Math.Min(width - 1, x + rx); dx++)
            {
                var j = (dy * width) + dx;
                if (j == i || cells[j].Group < 0)
                {
                    continue;
                }

                var relX = dx - x;
                var relY = dy - y;
                var localX = Math.Abs((relX * cos) + (relY * sin));
                var localY = Math.Abs((-relX * sin) + (relY * cos));
                if (localX > rWidth + 0.35 || localY > rHeight + 0.35)
                {
                    continue;
                }

                var other = model.Templates[groups[cells[j].Group][0]];
                if (PoseClusterer.AngleDistance(other.Angle, template.Angle) > angleLimit || Math.Abs(other.Scale - template.Scale) > 0.15)
                {
                    continue;
                }

                if (cells[j].Score > score + TieEpsilon || (Math.Abs(cells[j].Score - score) <= TieEpsilon && j < i))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsCellBestMaximum(
        ReadOnlySpan<(double Score, int Group)> cells,
        int width,
        int height,
        int x,
        int y,
        int fineRadius)
    {
        var i = (y * width) + x;
        var score = cells[i].Score;
        var radius = width * height >= 4096 ? fineRadius : 1;
        var margin = fineRadius == 1 ? 0.01 : 0d;
        for (var dy = Math.Max(0, y - radius); dy <= Math.Min(height - 1, y + radius); dy++)
        {
            for (var dx = Math.Max(0, x - radius); dx <= Math.Min(width - 1, x + radius); dx++)
            {
                var j = (dy * width) + dx;
                if (cells[j].Score > score + margin || (margin == 0 && cells[j].Score == score && j < i))
                {
                    return false;
                }
            }
        }

        return true;
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

            var key = string.Join(
                ';',
                model.Templates[i].Levels[level]
                    .OrderBy(static f => f.X)
                    .ThenBy(static f => f.Y)
                    .ThenBy(static f => f.Bin)
                    .Select(static f => $"{f.X},{f.Y},{f.Bin}"));
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
