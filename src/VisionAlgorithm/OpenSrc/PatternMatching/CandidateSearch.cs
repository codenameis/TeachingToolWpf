using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

internal readonly record struct PoseCandidate(MatchPose Pose, double Score, int Template, VerifiedScore? Verification = null, double? DetailConsistency = null);
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
        var filterAngleSidelobes = (model.FineDetail is not null || (model.Options.BuildClutterModel && model.Options.UseFineDetailValidation)) && groups.Count > 1;
        // 세부 모델이 없는 큰 클러터 모델은 3D 자세 공간(X, Y, Theta 8방위 버킷)에서 이방성 반경으로 능선 중복 후보를 억제한다.
        var anisotropic = filterAngleSidelobes && model.FineDetail is null;
        var angleBucketCount = anisotropic ? 8 : (filterAngleSidelobes ? 1 : 0);
        var cellBuckets = new (double Score, int Group)[angleBucketCount][];
        for (var b = 0; b < angleBucketCount; b++)
        {
            cellBuckets[b] = new (double Score, int Group)[gridLength];
            Array.Fill(cellBuckets[b], (double.NegativeInfinity, -1));
        }
        var groupsPerBatch = Math.Max(1, ShapeCandidateScoreRequest.MaximumScoreCount / gridLength);
        // 1차 스캔은 짝수 각도 템플릿(및 기준 각도 템플릿)만 계산하고, 후보 위치에서만 홀수 이웃 각도를 점 단위로 복원한다.
        var stride2 = scorer is null && anisotropic && groups.Count >= 8 && model.Options.MinimumScale == model.Options.MaximumScale;
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
        var referenceCells = anisotropic ? new (double Score, int Group)[gridLength] : null;
        if (referenceCells is not null)
        {
            Array.Fill(referenceCells, (double.NegativeInfinity, -1));
        }
        var order = Enumerable.Range(0, groups.Count)
            .Where(index => !stride2 || index % 2 == 0 || Math.Abs(index - referenceGroup) <= 1)
            .ToArray();
        ShapeCandidateScoreBatch? batch = null;
        // 템플릿 그룹 점수는 서로 독립이므로 묶음 단위로 병렬 계산하고, 병합은 그룹 순서대로 해 결과를 동일하게 유지한다.
        var chunkBuffers = scorer is null && options.MaximumParallelism > 1 && order.Length > 1
            ? new ushort[Math.Min(options.MaximumParallelism, order.Length)][] : null;
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
                        Parallel.For(0, Math.Min(chunkBuffers.Length, order.Length - chunkStart), new ParallelOptions
                        {
                            MaxDegreeOfParallelism = chunkBuffers.Length,
                            CancellationToken = token
                        }, k =>
                        {
                            Array.Clear(chunkBuffers[k], 0, gridLength);
                            Accumulate(map, model.Templates[groups[order[chunkStart + k]][0]].Levels[top], chunkBuffers[k], gridWidth, gridHeight, options.UseSimd, token);
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
                var effectiveCandidateThreshold = filterAngleSidelobes && model.FineDetail is not null
                    ? Math.Max(options.CandidateThreshold, Math.Min(0.84, options.MinimumScore + (groups.Count > 12 ? 0.10 : 0.05)))
                    : options.CandidateThreshold - (stride2 ? 0.05 : 0);
                var threshold = effectiveCandidateThreshold * 100 * features.Length;
                var maximumSum = features.Length * 100;
                var bucketIndex = anisotropic ? GetAngleBucket(model, group[0]) : 0;
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
                            if (normalizedScore > cellBest[i].Score + 1e-9 ||
                                (Math.Abs(normalizedScore - cellBest[i].Score) <= 1e-9 && betterPose))
                            {
                                cellBest[i] = (normalizedScore, groupIndex);
                            }
                            if (referenceCells is not null && Math.Abs(groupIndex - referenceGroup) <= 1 &&
                                normalizedScore > referenceCells[i].Score + 1e-9)
                            {
                                referenceCells[i] = (normalizedScore, groupIndex);
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
                        if (cell.Group < 0 || !(anisotropic ? IsAnisotropicMaximum(cellBest, gridWidth, gridHeight, x, y, cell.Group, model, groups, map.Step * factor) : IsCellBestMaximum(cellBest, gridWidth, gridHeight, x, y, model.FineDetail is not null ? 2 : 1)))
                        {
                            continue;
                        }
                        if (stride2)
                        {
                            cell = RestoreOddAngles(map, model, groups, top, x, y, cell, bucketIndex);
                            if (cell.Score < options.CandidateThreshold - 0.03)
                            {
                                continue;
                            }
                        }
                        EnqueueGroup(groups[cell.Group], model, x, y, map.Step, factor, cell.Score, options, candidates, ref exceeded);
                    }
                }
            }
            if (referenceCells is not null)
            {
                for (var y = 0; y < gridHeight; y++)
                {
                    for (var x = 0; x < gridWidth; x++)
                    {
                        var i = (y * gridWidth) + x;
                        var refCell = referenceCells[i];
                        if (refCell.Group < 0 || refCell.Score < options.CandidateThreshold - 0.03)
                        {
                            continue;
                        }
                        if (!IsAnisotropicMaximum(referenceCells, gridWidth, gridHeight, x, y, refCell.Group, model, groups, map.Step * factor))
                        {
                            // 수직 방향으로 1~2칸(8~16px) 떨어진 셀이 가림 경계선/클러터 때문에 점수가 더 높더라도,
                            // 상위 피라미드 연속 방향 검증 점수가 현 셀에서 더 높다면 실제 객체 중심 후보로 보존한다.
                            if (refCell.Score > 0.90 ||
                                ((y < 1 || referenceCells[i - gridWidth].Score <= refCell.Score) &&
                                 (y + 1 >= gridHeight || referenceCells[i + gridWidth].Score <= refCell.Score)))
                            {
                                continue;
                            }
                            if (x >= 2 && x + 2 < gridWidth &&
                                refCell.Score < referenceCells[i - 1].Score &&
                                refCell.Score < referenceCells[i + 1].Score)
                            {
                                continue;
                            }
                            var herePose = new MatchPose((x * map.Step) + 0.5, (y * map.Step) + 0.5,
                                model.TrainingReferenceAngle, model.Templates[groups[refCell.Group][0]].Scale);
                            var hereVer = ContinuousVerifier.Score(image.Gradients[top], model, herePose, top, allowElastic: false);
                            if (hereVer.Score < 0.42)
                            {
                                hereVer = ProbeTemplateScore(image.Gradients[top], model, herePose, top, out _);
                            }
                            if (hereVer.Score < 0.45)
                            {
                                continue;
                            }
                            var suppressedByBetter = false;
                            for (var dy = -2; dy <= 2; dy++)
                            {
                                var ny = y + dy;
                                if (dy == 0 || ny < 0 || ny >= gridHeight)
                                {
                                    continue;
                                }
                                if (referenceCells[(ny * gridWidth) + x].Score > refCell.Score)
                                {
                                    var dyVer = ContinuousVerifier.Score(image.Gradients[top], model,
                                        herePose with { Y = herePose.Y + (dy * map.Step) }, top, allowElastic: false);
                                    if (dyVer.Score > hereVer.Score + 0.04)
                                    {
                                        suppressedByBetter = true;
                                        break;
                                    }
                                }
                            }
                            if (suppressedByBetter)
                            {
                                continue;
                            }
                            var effectiveScore = Math.Max(refCell.Score, hereVer.Score + 0.38);
                            EnqueueGroup(groups[refCell.Group], model, x, y, map.Step, factor, effectiveScore, options, candidates, ref exceeded);
                        }
                        else
                        {
                            EnqueueGroup(groups[refCell.Group], model, x, y, map.Step, factor, refCell.Score, options, candidates, ref exceeded);
                        }
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
        var gridTime = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        var ordered = new PoseCandidate[candidates.Count];
        for (var index = 0; index < ordered.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            ordered[index] = candidates.Dequeue();
        }
        if (anisotropic)
        {
            ordered = ClusterPoses(model, ordered);
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
        if (model.FineDetail is not null)
        {
            var deduplicated = new List<PoseCandidate>(refined.Count);
            foreach (var candidate in refined)
            {
                var duplicate = false;
                for (var i = 0; i < deduplicated.Count; i++)
                {
                    var dx = candidate.Pose.X - deduplicated[i].Pose.X;
                    var dy = candidate.Pose.Y - deduplicated[i].Pose.Y;
                    if ((dx * dx) + (dy * dy) <= 4.0)
                    {
                        duplicate = true;
                        break;
                    }
                }
                if (!duplicate)
                {
                    deduplicated.Add(candidate);
                }
            }
            return new(deduplicated, exceeded, groups.Count)
            {
                GridMilliseconds = gridTime,
                PoseMilliseconds = timer.Elapsed.TotalMilliseconds,
                CandidatesBeforeRefinement = ordered.Length
            };
        }
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
        Span<QuantizedFeature> quantizedBuffer = stackalloc QuantizedFeature[512];
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
            var modelFeatures = model.Levels[level].Features;
            scoped ReadOnlySpan<QuantizedFeature> features;
            if (optimizedLocalSearch && level == model.Levels.Length - 1)
            {
                features = model.Templates[candidate.Template].Levels[level].AsSpan();
            }
            else
            {
                for (var index = 0; index < modelFeatures.Length; index++)
                {
                    var feature = modelFeatures[index];
                    quantizedBuffer[index] = new QuantizedFeature(
                        (int)Math.Round(pose.Scale * ((cos * feature.X) - (sin * feature.Y))),
                        (int)Math.Round(pose.Scale * ((sin * feature.X) + (cos * feature.Y))),
                        OrientationMaps.Quantize((cos * feature.NormalX) - (sin * feature.NormalY),
                            (sin * feature.NormalX) + (cos * feature.NormalY)));
                }
                features = quantizedBuffer.Slice(0, modelFeatures.Length);
            }
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
            var plateauSumX = 0d;
            var plateauSumY = 0d;
            var plateauCount = 0;
            for (var y = top; y <= bottom; y++)
            {
                for (var x = left; x <= right; x++)
                {
                    var score = optimizedLocalSearch ? localScores[((y - top) * width) + x - left]
                        : Score(image.Maps[level], features, x, y, bestSum, options.Greediness);
                    if (score > bestSum)
                    {
                        bestSum = score;
                        plateauSumX = x + 0.5;
                        plateauSumY = y + 0.5;
                        plateauCount = 1;
                    }
                    else if (model.FineDetail is not null && score == bestSum && bestSum > 0)
                    {
                        plateauSumX += x + 0.5;
                        plateauSumY += y + 0.5;
                        plateauCount++;
                    }
                }
            }
            if (plateauCount > 0)
            {
                var mapPose = pose with
                {
                    X = plateauSumX / plateauCount,
                    Y = plateauSumY / plateauCount
                };
                if (options.Verify && !model.FeaturePartitions.IsDefault && model.Options.UseFineDetailValidation &&
                    (Math.Abs(mapPose.X - pose.X) > 1.5 || Math.Abs(mapPose.Y - pose.Y) > 1.5))
                {
                    var currentScore = ContinuousVerifier.Score(image.Gradients[level], model, pose, level, allowElastic: false);
                    var mapScore = ContinuousVerifier.Score(image.Gradients[level], model, mapPose, level, allowElastic: false);
                    if (mapScore.Score >= currentScore.Score - 0.01)
                    {
                        pose = mapPose;
                    }
                }
                else
                {
                    pose = mapPose;
                }
            }
            if (options.Verify)
            {
                if (level == model.Levels.Length - 1 && model.FineDetail is null && !model.FeaturePartitions.IsDefault &&
                    model.Options.UseFineDetailValidation && Math.Abs(pose.Angle - model.TrainingReferenceAngle) > 1e-4)
                {
                    var currentInitial = ProbeTemplateScore(image.Gradients[level], model, pose, level, out _);
                    if (currentInitial.Score < 0.72)
                    {
                        var refInitial = ProbeTemplateScore(image.Gradients[level], model,
                            pose with { Angle = model.TrainingReferenceAngle }, level, out var refBestPose);
                        if (refInitial.Score > currentInitial.Score + 0.01)
                        {
                            pose = refBestPose;
                        }
                    }
                }
                if (level == model.Levels.Length - 1 && model.Templates.Length > 1 && candidate.Score >= 0.88 &&
                    model.FineDetail is not null)
                {
                    var bestInitial = ProbeTemplateScore(image.Gradients[level], model, pose, level, out var bestProbePose);
                    pose = bestProbePose;
                    if (bestInitial.Score < 0.85 || model.Options.MaximumScale > model.Options.MinimumScale ||
                        Math.Abs(pose.Angle - model.TrainingReferenceAngle) > 1e-4)
                    {
                        var coarseThreshold = (int)Math.Max(0, (candidate.Score - 0.08) * 100 * modelFeatures.Length);
                        var centerPose = new MatchPose(cx + 0.5, cy + 0.5, pose.Angle, pose.Scale);
                        for (var t = 0; t < model.Templates.Length; t++)
                        {
                            if (t == candidate.Template)
                            {
                                continue;
                            }
                            var template = model.Templates[t];
                            var tFeatures = template.Levels[level].AsSpan();
                            if (Score(image.Maps[level], tFeatures, cx, cy, coarseThreshold) < coarseThreshold)
                            {
                                continue;
                            }
                            var tVerified = ProbeTemplateScore(image.Gradients[level], model,
                                centerPose with { Angle = template.Angle, Scale = template.Scale }, level, out var tBestPose);
                            if (tVerified.Score > bestInitial.Score + 1e-6 ||
                                (Math.Abs(tVerified.Score - bestInitial.Score) <= 1e-6 &&
                                 Math.Abs(template.Angle - model.TrainingReferenceAngle) + Math.Abs(template.Scale - 1) <
                                 Math.Abs(model.Templates[candidate.Template].Angle - model.TrainingReferenceAngle) +
                                 Math.Abs(model.Templates[candidate.Template].Scale - 1)))
                            {
                                bestInitial = tVerified;
                                pose = tBestPose;
                                candidate = candidate with { Template = t };
                            }
                        }
                    }
                }
                var result = ContinuousVerifier.Optimize(image.Gradients[level], model, pose, options, token, level);
                pose = result.Pose;
                if (level == 0)
                {
                    var verification = result.Score;
                    double? detailConsistency = null;
                    if (options.ClutterWeight > 0 && model.FineDetail is { } detail && image.Original is { } original &&
                        verification.Score >= options.MinimumScore && verification.Support >= options.MinimumSupport)
                    {
                        var initialDetail = detail.Score(original, pose, model.Options.Polarity);
                        var bestCombined = verification.Score * (1 - (options.ClutterWeight * (1 - initialDetail)));
                        var bestDetail = initialDetail;
                        if (detail.HasElasticPatches && bestDetail < 0.70 && model.Templates.Length > 1)
                        {
                            var angleStep = model.Options.AngleStepDegrees * Math.PI / 180;
                            foreach (var targetAngle in new[] { pose.Angle - angleStep, pose.Angle + angleStep, model.TrainingReferenceAngle })
                            {
                                var altInitial = pose with { Angle = targetAngle };
                                if (!ContinuousVerifier.Allowed(altInitial, model, options))
                                {
                                    continue;
                                }
                                var altOpt = ContinuousVerifier.Optimize(image.Gradients[0], model, altInitial, options, token, 0);
                                if (altOpt.Score.Score < options.MinimumScore || altOpt.Score.Support < options.MinimumSupport)
                                {
                                    continue;
                                }
                                var altDetail = detail.Score(original, altOpt.Pose, model.Options.Polarity);
                                var altCombined = altOpt.Score.Score * (1 - (options.ClutterWeight * (1 - altDetail)));
                                if (altCombined > bestCombined + 1e-9)
                                {
                                    pose = altOpt.Pose;
                                    verification = altOpt.Score;
                                    bestDetail = altDetail;
                                    bestCombined = altCombined;
                                }
                            }
                        }
                        if (options.Refine)
                        {
                            var detailResult = detail.Optimize(original, model, pose, options, token);
                            var directional = ContinuousVerifier.Score(image.Gradients[0], model, detailResult.Pose);
                            var refinedCombined = directional.Score * (1 - (options.ClutterWeight * (1 - detailResult.Score)));
                            if (directional.Score >= options.MinimumScore && directional.Support >= options.MinimumSupport &&
                                refinedCombined > bestCombined + 1e-9)
                            {
                                pose = detailResult.Pose;
                                verification = directional;
                                bestDetail = detailResult.Score;
                                bestCombined = refinedCombined;
                            }
                        }
                        detailConsistency = bestDetail;
                        verification = verification with
                        {
                            Score = bestCombined
                        };
                    }
                    candidate = candidate with
                    {
                        Verification = verification,
                        DetailConsistency = detailConsistency
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

    private static VerifiedScore ProbeTemplateScore(GradientImage gradient, ShapeModel model, MatchPose center, int level, out MatchPose bestPose)
    {
        bestPose = center;
        var best = ContinuousVerifier.Score(gradient, model, center, level, allowElastic: false);
        if (best.Score is < 0.35 or >= 0.88)
        {
            return best;
        }
        for (var dy = -1.5; dy <= 1.5; dy += 1.5)
        {
            for (var dx = -1.5; dx <= 1.5; dx += 1.5)
            {
                if (dx == 0 && dy == 0)
                {
                    continue;
                }
                var proposal = center with { X = center.X + dx, Y = center.Y + dy };
                var score = ContinuousVerifier.Score(gradient, model, proposal, level, allowElastic: false);
                if (score.Score > best.Score + 1e-6)
                {
                    best = score;
                    bestPose = proposal;
                }
            }
        }
        return best;
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

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int Score(OrientationMaps map, ReadOnlySpan<QuantizedFeature> features, int x, int y, int minimumSum, double greediness = 0)
    {
        var sum = 0;
        var count = features.Length;
        for (var index = 0; index < count; index++)
        {
            var feature = features[index];
            sum += map.At(feature.Bin, x + feature.X, y + feature.Y);
            if ((index & 3) == 3 && minimumSum >= 0)
            {
                var remainingMax = (count - index - 1) * 100;
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

    private static void EnqueueGroup(List<int> group, ShapeModel model, int x, int y, int step, int factor, double score,
        SearchOptions options, PriorityQueue<PoseCandidate, double> candidates, ref bool exceeded)
    {
        foreach (var index in group)
        {
            var template = model.Templates[index];
            var candidate = new PoseCandidate(new(((x * step) + 0.5) * factor,
                ((y * step) + 0.5) * factor, template.Angle, template.Scale),
                score, index);
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


    /// <summary>회전한 패턴 크기의 40%를 가로·세로 억제 반경으로 쓰고, 각도가 한 간격 이내인 더 높은 이웃만 억제로 본다.</summary>
    private static bool IsAnisotropicMaximum(ReadOnlySpan<(double Score, int Group)> cells, int width, int height, int x, int y,
        int group, ShapeModel model, List<List<int>> groups, int cellSize)
    {
        var template = model.Templates[groups[group][0]];
        var theta = template.Angle - model.TrainingReferenceAngle;
        var minX = double.MaxValue;
        var maxX = double.MinValue;
        var minY = double.MaxValue;
        var maxY = double.MinValue;
        foreach (var point in model.Bounds)
        {
            minX = Math.Min(minX, point.X);
            maxX = Math.Max(maxX, point.X);
            minY = Math.Min(minY, point.Y);
            maxY = Math.Max(maxY, point.Y);
        }
        var boundsWidth = (maxX - minX) * template.Scale;
        var boundsHeight = (maxY - minY) * template.Scale;
        var rotatedWidth = (Math.Abs(Math.Cos(theta)) * boundsWidth) + (Math.Abs(Math.Sin(theta)) * boundsHeight);
        var rotatedHeight = (Math.Abs(Math.Sin(theta)) * boundsWidth) + (Math.Abs(Math.Cos(theta)) * boundsHeight);
        var rx = Math.Max(1, (int)Math.Ceiling(rotatedWidth / 3.2 / cellSize));
        var ry = Math.Max(1, (int)Math.Floor(rotatedHeight / 3.5 / cellSize));
        var angleLimit = (model.Options.AngleStepDegrees * Math.PI / 180 * 4.01) + 1e-9;
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
                var other = model.Templates[groups[cells[j].Group][0]];
                if (Math.Abs(other.Angle - template.Angle) > angleLimit || Math.Abs(other.Scale - template.Scale) > 0.15)
                {
                    continue;
                }
                if (cells[j].Score > score + 1e-9 || (Math.Abs(cells[j].Score - score) <= 1e-9 && j < i))
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>정밀화 전에 (x, y, angle)이 가까운 후보를 점수가 가장 높은 하나로 병합한다. 능선 중복 후보가 정밀화 비용을 키우지 않게 한다.</summary>
    private static PoseCandidate[] ClusterPoses(ShapeModel model, PoseCandidate[] ascending)
    {
        var minX = double.MaxValue;
        var maxX = double.MinValue;
        var minY = double.MaxValue;
        var maxY = double.MinValue;
        foreach (var point in model.Bounds)
        {
            minX = Math.Min(minX, point.X);
            maxX = Math.Max(maxX, point.X);
            minY = Math.Min(minY, point.Y);
            maxY = Math.Max(maxY, point.Y);
        }
        var angleLimit = (model.Options.AngleStepDegrees * Math.PI / 180 * 2.01) + 1e-9;
        var kept = new List<PoseCandidate>();
        for (var index = ascending.Length - 1; index >= 0; index--)
        {
            var candidate = ascending[index];
            var theta = candidate.Pose.Angle - model.TrainingReferenceAngle;
            var width = ((Math.Abs(Math.Cos(theta)) * (maxX - minX)) + (Math.Abs(Math.Sin(theta)) * (maxY - minY))) * candidate.Pose.Scale;
            var height = ((Math.Abs(Math.Sin(theta)) * (maxX - minX)) + (Math.Abs(Math.Cos(theta)) * (maxY - minY))) * candidate.Pose.Scale;
            var rx = Math.Min(Math.Max(2, width / 3.2), 18.0);
            var ry = Math.Min(Math.Max(2, height / 4.0), 10.0);
            var duplicate = false;
            foreach (var other in kept)
            {
                if (Math.Abs(other.Pose.X - candidate.Pose.X) <= rx && Math.Abs(other.Pose.Y - candidate.Pose.Y) <= ry &&
                    Math.Abs(other.Pose.Angle - candidate.Pose.Angle) <= angleLimit &&
                    Math.Abs(other.Pose.Scale - candidate.Pose.Scale) <= 0.15)
                {
                    duplicate = true;
                    break;
                }
            }
            if (!duplicate)
            {
                kept.Add(candidate);
            }
        }
        kept.Reverse();
        return [.. kept];
    }

    /// <summary>NMS를 통과한 피크 셀에서만 양옆 홀수 각도 점수를 계산해 최고 각도를 복원한다. 8방위 버킷이 다른 이웃은 제외한다.</summary>
    private static (double Score, int Group) RestoreOddAngles(OrientationMaps map, ShapeModel model, List<List<int>> groups, int top,
        int x, int y, (double Score, int Group) current, int bucketIndex)
    {
        var best = current;
        foreach (var neighbor in new[] { current.Group - 1, current.Group + 1 })
        {
            if (neighbor < 0 || neighbor >= groups.Count || GetAngleBucket(model, groups[neighbor][0]) != bucketIndex)
            {
                continue;
            }
            var features = model.Templates[groups[neighbor][0]].Levels[top];
            var normalized = Score(map, features.AsSpan(), x * map.Step, y * map.Step, -1) / (100d * features.Length);
            var betterPose = Math.Abs(model.Templates[groups[neighbor][0]].Angle - model.TrainingReferenceAngle) <
                Math.Abs(model.Templates[groups[best.Group][0]].Angle - model.TrainingReferenceAngle);
            if (normalized > best.Score + 1e-9 || (Math.Abs(normalized - best.Score) <= 1e-9 && betterPose))
            {
                best = (normalized, neighbor);
            }
        }
        return best;
    }

    private static int GetAngleBucket(ShapeModel model, int template)
    {
        var diff = (model.Templates[template].Angle - model.TrainingReferenceAngle) % (2 * Math.PI);
        if (diff < 0)
        {
            diff += 2 * Math.PI;
        }
        return ((int)Math.Floor((diff + (Math.PI / 8)) / (Math.PI / 4))) & 7;
    }

    private static bool IsCellBestMaximum(ReadOnlySpan<(double Score, int Group)> cells, int width, int height, int x, int y, int fineRadius)
    {
        var i = (y * width) + x;
        var score = cells[i].Score;
        var radius = width * height >= 4096 ? fineRadius : 1;
        // 세부 모델이 없는 큰 모델은 이웃이 확실히(0.01 초과) 높을 때만 억제해 반복 패턴의 진짜 피크를 보존한다.
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