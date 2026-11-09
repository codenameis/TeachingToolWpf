// File: src/VisionAlgorithm/OpenSrc/PatternMatching/StegerShape/CandidateSearch.cs
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
                            if (anisotropic && !model.FeaturePartitions.IsDefault && model.Options.UseFineDetailValidation &&
                                (options.MaxOcclusionRatio > 0 || !model.FeatureAsymmetryFlags.IsDefault) &&
                                cell.Score >= options.CandidateThreshold - 0.03 && cell.Score <= 0.90 &&
                                TryRecoverOccludedAnisotropicCandidate(image, map, model, groups, top, gridWidth, gridHeight,
                                    cellBest, x, y, cell, bucketIndex, stride2, options.MaxOcclusionRatio, out var recoveredCell, out var effectiveScore))
                            {
                                EnqueueGroup(groups[recoveredCell.Group], model, x, y, map.Step, factor, effectiveScore, options, candidates, ref exceeded, anisotropic);
                            }
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
            if (ordered.Length > options.MaximumCandidates)
            {
                ordered = ordered.AsSpan(ordered.Length - options.MaximumCandidates).ToArray();
            }
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
        if (model.FineDetail is not null || (!model.FeaturePartitions.IsDefault && model.Options.UseFineDetailValidation))
        {
            var deduplicated = new List<PoseCandidate>(refined.Count);
            foreach (var candidate in refined)
            {
                var duplicate = false;
                for (var i = 0; i < deduplicated.Count; i++)
                {
                    var dx = candidate.Pose.X - deduplicated[i].Pose.X;
                    var dy = candidate.Pose.Y - deduplicated[i].Pose.Y;
                    if ((dx * dx) + (dy * dy) <= 4.0 &&
                        (model.FineDetail is not null || AngleDistance(candidate.Pose.Angle, deduplicated[i].Pose.Angle) <= 0.15))
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
            if (options.Verify && level == model.Levels.Length - 1 && model.FineDetail is null &&
                !model.FeaturePartitions.IsDefault && model.Options.UseFineDetailValidation && model.Templates.Length > 1)
            {
                var currentInitial = ProbeTemplateScore(image.Gradients[level], model, pose, level, out var bestBucketPose, fullSubCell: true);
                pose = bestBucketPose;
                if (currentInitial.Score < 0.78)
                {
                    var candidateBucket = GetAngleBucket(model, candidate.Template);
                    var bestBucketScore = currentInitial;
                    for (var t = 0; t < model.Templates.Length; t++)
                    {
                        if (t == candidate.Template || GetAngleBucket(model, t) != candidateBucket)
                        {
                            continue;
                        }
                        var tAngle = model.Templates[t].Angle;
                        var tInitial = ProbeTemplateScore(image.Gradients[level], model,
                            candidate.Pose with { X = candidate.Pose.X / factor, Y = candidate.Pose.Y / factor, Angle = tAngle },
                            level, out var tBestPose, fullSubCell: true);
                        if (tInitial.Score > bestBucketScore.Score + 0.01)
                        {
                            bestBucketScore = tInitial;
                            pose = tBestPose;
                            candidate = candidate with { Template = t };
                        }
                    }
                }
            }
            var cos = Math.Cos(pose.Angle);
            var sin = Math.Sin(pose.Angle);
            var modelFeatures = model.Levels[level].Features;
            scoped ReadOnlySpan<QuantizedFeature> features;
            if (optimizedLocalSearch && level == model.Levels.Length - 1 &&
                Math.Abs(pose.Angle - model.Templates[candidate.Template].Angle) <= 1e-6)
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
                if (options.Verify && model.FineDetail is null && !model.FeaturePartitions.IsDefault && model.Options.UseFineDetailValidation &&
                    (((mapPose.X - pose.X) * (mapPose.X - pose.X)) + ((mapPose.Y - pose.Y) * (mapPose.Y - pose.Y)) > 0.25))
                {
                    var horizontalMajor = model.BoundsWidth >= model.BoundsHeight;
                    var theta = (pose.Angle - model.TrainingReferenceAngle) + (horizontalMajor ? 0.0 : Math.PI / 2);
                    var ux = Math.Cos(theta);
                    var uy = Math.Sin(theta);
                    var vx = -uy;
                    var vy = ux;
                    var dx = mapPose.X - pose.X;
                    var dy = mapPose.Y - pose.Y;
                    var projU = (dx * ux) + (dy * uy);
                    var projV = (dx * vx) + (dy * vy);
                    var minorOnlyPose = pose with { X = pose.X + (projV * vx), Y = pose.Y + (projV * vy) };
                    var majorOnlyPose = pose with { X = pose.X + (projU * ux), Y = pose.Y + (projU * uy) };
                    var bestPoseCandidate = pose;
                    var bestScoreCandidate = ContinuousVerifier.Score(image.Gradients[level], model, pose, level, allowElastic: false, options.MaxOcclusionRatio, options.StrictContext);
                    Span<MatchPose> trials = [minorOnlyPose, majorOnlyPose, mapPose];
                    foreach (var trialPose in trials)
                    {
                        var trialScore = ContinuousVerifier.Score(image.Gradients[level], model, trialPose, level, allowElastic: false, options.MaxOcclusionRatio, options.StrictContext);
                        var margin = trialPose.Equals(mapPose) ? 0.01 : -0.005;
                        if (trialScore.Score >= bestScoreCandidate.Score + margin)
                        {
                            bestScoreCandidate = trialScore;
                            bestPoseCandidate = trialPose;
                        }
                    }
                    pose = bestPoseCandidate;
                }
                else
                {
                    pose = mapPose;
                }
            }
            if (options.Verify)
            {
                if (level == model.Levels.Length - 1 && model.FineDetail is null && !model.FeaturePartitions.IsDefault &&
                    model.Options.UseFineDetailValidation && model.Templates.Length > 1)
                {
                    var currentInitial = ProbeTemplateScore(image.Gradients[level], model, pose, level, out var bestBucketPose, fullSubCell: true);
                    if (currentInitial.Score < 0.72)
                    {
                        var candidateBucket = GetAngleBucket(model, candidate.Template);
                        var bestBucketScore = currentInitial;
                        pose = bestBucketPose;
                        for (var t = 0; t < model.Templates.Length; t++)
                        {
                            if (t == candidate.Template || GetAngleBucket(model, t) != candidateBucket)
                            {
                                continue;
                            }
                            var tAngle = model.Templates[t].Angle;
                            if (Math.Abs(tAngle - pose.Angle) < 1e-6)
                            {
                                continue;
                            }
                            var tInitial = ProbeTemplateScore(image.Gradients[level], model,
                                pose with { Angle = tAngle }, level, out var tBestPose, fullSubCell: true);
                            if (tInitial.Score > bestBucketScore.Score + 0.01)
                            {
                                bestBucketScore = tInitial;
                                pose = tBestPose;
                                candidate = candidate with { Template = t };
                            }
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
                if ((options.MaxOcclusionRatio > 0 || !model.FeatureAsymmetryFlags.IsDefault) &&
                    level == 0 && model.FineDetail is null && !model.FeaturePartitions.IsDefault &&
                    model.Options.UseFineDetailValidation && result.Score.Score is >= 0.45 and < 0.82)
                {
                    // 가림 또는 비대칭 문맥에 의해 중심이 주축(Major Axis) 방향으로 밀린 경우,
                    // 가로/세로 방향에 무관한 실제 주축 각도(majorAngle)와 모델 주축 길이에 비례하는 탐색 반경(slideSpan)으로 복원한다.
                    var horizontalMajor = model.BoundsWidth >= model.BoundsHeight;
                    var majorSpan = Math.Max(model.BoundsWidth, model.BoundsHeight) * result.Pose.Scale;
                    var slideSpan = Math.Clamp((int)Math.Round(majorSpan * 0.045), 2, 12);
                    var majorAngle = (result.Pose.Angle - model.TrainingReferenceAngle) + (horizontalMajor ? 0.0 : Math.PI / 2);
                    var ux = Math.Cos(majorAngle);
                    var uy = Math.Sin(majorAngle);
                    var bestMajorPose = result.Pose;
                    var bestMajorScore = result.Score;
                    for (var du = -slideSpan; du <= slideSpan; du++)
                    {
                        if (du == 0)
                        {
                            continue;
                        }
                        var trialPose = result.Pose with
                        {
                            X = result.Pose.X + (du * ux),
                            Y = result.Pose.Y + (du * uy)
                        };
                        var trialScore = ContinuousVerifier.Score(image.Gradients[0], model, trialPose, 0, allowElastic: false, options.MaxOcclusionRatio, options.StrictContext);
                        if (trialScore.Score > bestMajorScore.Score + 0.01)
                        {
                            bestMajorScore = trialScore;
                            bestMajorPose = trialPose;
                        }
                    }
                    if (!bestMajorPose.Equals(result.Pose))
                    {
                        var reoptimized = ContinuousVerifier.Optimize(image.Gradients[0], model, bestMajorPose, options, token, 0);
                        if (reoptimized.Score.Score > result.Score.Score + 0.01)
                        {
                            result = reoptimized;
                        }
                    }
                }
                if (options.MaxOcclusionRatio > 0 && level == 0 && model.FineDetail is null && !model.FeaturePartitions.IsDefault &&
                    model.Options.UseFineDetailValidation && result.Score.Score >= options.MinimumScore)
                {
                    // 가림 허용 모드에서 수렴한 최종 자세 주변의 주축/부축 서브픽셀(±0.25, ±0.50px) 정합도를 한 번 더 점검해
                    // 회전된 부분 가림 부품의 중심 위상 오차를 0.5px 이내로 정밀 정렬한다.
                    var horizontalMajor = model.BoundsWidth >= model.BoundsHeight;
                    var majorAngle = (result.Pose.Angle - model.TrainingReferenceAngle) + (horizontalMajor ? 0.0 : Math.PI / 2);
                    var ux = Math.Cos(majorAngle);
                    var uy = Math.Sin(majorAngle);
                    var vx = -uy;
                    var vy = ux;
                    var bestSubPose = result.Pose;
                    var bestSubScore = ContinuousVerifier.Score(image.Gradients[0], model, result.Pose, 0, allowElastic: false, 0, false);
                    for (var iv = -2; iv <= 2; iv++)
                    {
                        var dv = iv * 0.25;
                        for (var iu = -2; iu <= 2; iu++)
                        {
                            if (iu == 0 && iv == 0)
                            {
                                continue;
                            }
                            var du = iu * 0.25;
                            var subPose = result.Pose with
                            {
                                X = result.Pose.X + (du * ux) + (dv * vx),
                                Y = result.Pose.Y + (du * uy) + (dv * vy)
                            };
                            var subOcc = ContinuousVerifier.Score(image.Gradients[0], model, subPose, 0, allowElastic: true, options.MaxOcclusionRatio, options.StrictContext);
                            if (subOcc.Score < result.Score.Score - 0.002)
                            {
                                continue;
                            }
                            var subRigid = ContinuousVerifier.Score(image.Gradients[0], model, subPose, 0, allowElastic: false, 0, false);
                            if (subRigid.Score > bestSubScore.Score + 1e-4)
                            {
                                bestSubScore = subRigid;
                                bestSubPose = subPose;
                                result = (subPose, subOcc);
                            }
                        }
                    }
                }
                pose = result.Pose;
                if (level == 0)
                {
                    var verification = result.Score;
                    double? detailConsistency = null;
                    if (options.StrictContext && options.MaxOcclusionRatio <= 0.2 && !model.FeatureAsymmetryFlags.IsDefault &&
                        verification.Score < options.MinimumScore && model.Templates.Length > 1)
                    {
                        // 회전 대칭 코어를 가진 비대칭 문맥 부품에서 Coarse 양자화 단계가 잘못된 위상 각도를 먼저 잡았거나
                        // 레벨 1에서 문맥 테두리가 중심을 1~2px 끌어당겨 레벨 0 검증에서 기각된 경우,
                        // 동일 위치의 기준/직교/반전 위상 각도(0°, ±90°, 180°)를 대칭적으로 재검증한다.
                        var basePose0 = pose with { X = cx + 0.5, Y = cy + 0.5 };
                        Span<double> phaseOffsets = [0.0, Math.PI / 2, -Math.PI / 2, Math.PI];
                        foreach (var offset in phaseOffsets)
                        {
                            var altAngle = offset == 0.0
                                ? model.TrainingReferenceAngle
                                : model.TrainingReferenceAngle + Math.IEEERemainder((pose.Angle + offset) - model.TrainingReferenceAngle, 2 * Math.PI);
                            var altInitial = pose with { Angle = altAngle };
                            if (ContinuousVerifier.Allowed(altInitial, model, options))
                            {
                                var altOpt = ContinuousVerifier.Optimize(image.Gradients[0], model, altInitial, options, token, 0);
                                if (altOpt.Score.Score > verification.Score + 1e-9)
                                {
                                    pose = altOpt.Pose;
                                    verification = altOpt.Score;
                                }
                            }
                            var altBase = basePose0 with { Angle = altAngle };
                            if (ContinuousVerifier.Allowed(altBase, model, options))
                            {
                                var altBaseOpt = ContinuousVerifier.Optimize(image.Gradients[0], model, altBase, options, token, 0);
                                if (altBaseOpt.Score.Score > verification.Score + 1e-9)
                                {
                                    pose = altBaseOpt.Pose;
                                    verification = altBaseOpt.Score;
                                }
                            }
                        }
                    }
                    if (options.ClutterWeight > 0 && model.FineDetail is { } detail && image.Original is { } original &&
                        verification.Score >= options.MinimumScore && verification.Support >= options.MinimumSupport)
                    {
                        var initialDetail = detail.Score(original, pose, model.Options.Polarity);
                        var bestCombined = verification.Score * (1 - (options.ClutterWeight * (1 - initialDetail)));
                        var bestDetail = initialDetail;
                        if ((detail.HasElasticPatches && bestDetail < 0.70 ||
                             !model.FeatureAsymmetryFlags.IsDefault && bestDetail < 0.85) &&
                            model.Templates.Length > 1)
                        {
                            var angleStep = model.Options.AngleStepDegrees * Math.PI / 180;
                            Span<double> targetAngles = [
                                pose.Angle - angleStep,
                                pose.Angle + angleStep,
                                model.TrainingReferenceAngle + Math.IEEERemainder((pose.Angle + (Math.PI / 2)) - model.TrainingReferenceAngle, 2 * Math.PI),
                                model.TrainingReferenceAngle + Math.IEEERemainder((pose.Angle - (Math.PI / 2)) - model.TrainingReferenceAngle, 2 * Math.PI),
                                model.TrainingReferenceAngle + Math.IEEERemainder((pose.Angle + Math.PI) - model.TrainingReferenceAngle, 2 * Math.PI)
                            ];
                            foreach (var targetAngle in targetAngles)
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
                            var directional = ContinuousVerifier.Score(image.Gradients[0], model, detailResult.Pose, 0, allowElastic: true, options.MaxOcclusionRatio, options.StrictContext);
                            var refinedCombined = directional.Score * (1 - (options.ClutterWeight * (1 - detailResult.Score)));
                            if (directional.Score >= options.MinimumScore && directional.Support >= options.MinimumSupport &&
                                refinedCombined > bestCombined + 1e-9 &&
                                ((detailResult.Pose.X - pose.X) * (detailResult.Pose.X - pose.X)) + ((detailResult.Pose.Y - pose.Y) * (detailResult.Pose.Y - pose.Y)) <= 0.36)
                            {
                                pose = detailResult.Pose;
                                verification = directional;
                                bestDetail = detailResult.Score;
                                bestCombined = refinedCombined;
                            }
                            else if (detailResult.Score > bestDetail)
                            {
                                bestDetail = detailResult.Score;
                                bestCombined = verification.Score * (1 - (options.ClutterWeight * (1 - bestDetail)));
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

    private static VerifiedScore ProbeTemplateScore(GradientImage gradient, ShapeModel model, MatchPose center, int level, out MatchPose bestPose, bool fullSubCell = false)
    {
        bestPose = center;
        var best = ContinuousVerifier.Score(gradient, model, center, level, allowElastic: false);
        var minScore = model.FineDetail is null ? 0.25 : 0.30;
        if (best.Score < minScore || best.Score >= 0.88)
        {
            return best;
        }
        if (fullSubCell && model.FineDetail is null)
        {
            ReadOnlySpan<double> coarseGrid = [-1.5, 0.0, 1.5];
            foreach (var dy in coarseGrid)
            {
                foreach (var dx in coarseGrid)
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
            if (best.Score < 0.35)
            {
                return best;
            }
            ReadOnlySpan<double> fullOffsets = [-1.5, -0.5, 0.0, 0.5, 1.5];
            foreach (var dy in fullOffsets)
            {
                foreach (var dx in fullOffsets)
                {
                    if ((dx == -1.5 || dx == 0.0 || dx == 1.5) && (dy == -1.5 || dy == 0.0 || dy == 1.5))
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
            if (best.Score < 0.34 || model.Templates.Length <= 1)
            {
                return best;
            }
            var halfStep = model.Options.AngleStepDegrees * Math.PI / 360.0;
            Span<double> angleDeltas = [-halfStep, halfStep];
            foreach (var da in angleDeltas)
            {
                var angle = center.Angle + da;
                if (angle - model.TrainingReferenceAngle < (model.Options.MinimumAngleDegrees * Math.PI / 180) - 1e-12 ||
                    angle - model.TrainingReferenceAngle > (model.Options.MaximumAngleDegrees * Math.PI / 180) + 1e-12)
                {
                    continue;
                }
                foreach (var dy in fullOffsets)
                {
                    foreach (var dx in fullOffsets)
                    {
                        var proposal = center with { X = center.X + dx, Y = center.Y + dy, Angle = angle };
                        var score = ContinuousVerifier.Score(gradient, model, proposal, level, allowElastic: false);
                        if (score.Score > best.Score + 1e-6)
                        {
                            best = score;
                            bestPose = proposal;
                        }
                    }
                }
            }
            return best;
        }
        ReadOnlySpan<double> coarseOffsets = [-1.5, 0.0, 1.5];
        foreach (var dy in coarseOffsets)
        {
            foreach (var dx in coarseOffsets)
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
        if (model.FineDetail is not null)
        {
            return best;
        }
        ReadOnlySpan<double> fineOffsets = [-0.5, 0.0, 0.5];
        foreach (var dy in fineOffsets)
        {
            foreach (var dx in fineOffsets)
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
        if (model.Templates.Length > 1)
        {
            var halfStep = model.Options.AngleStepDegrees * Math.PI / 360.0;
            var spatialBest = bestPose;
            Span<double> angleDeltas = [-halfStep, halfStep];
            foreach (var da in angleDeltas)
            {
                var angle = center.Angle + da;
                if (angle - model.TrainingReferenceAngle < (model.Options.MinimumAngleDegrees * Math.PI / 180) - 1e-12 ||
                    angle - model.TrainingReferenceAngle > (model.Options.MaximumAngleDegrees * Math.PI / 180) + 1e-12)
                {
                    continue;
                }
                Span<(double dx, double dy)> spatialOffsets = [
                    (spatialBest.X - center.X, spatialBest.Y - center.Y),
                    (0.0, 0.0),
                    (-0.5, -0.5),
                    (-0.5, 0.5),
                    (0.5, -0.5),
                    (0.5, 0.5)
                ];
                foreach (var (dx, dy) in spatialOffsets)
                {
                    var proposal = center with { X = center.X + dx, Y = center.Y + dy, Angle = angle };
                    var score = ContinuousVerifier.Score(gradient, model, proposal, level, allowElastic: false);
                    if (score.Score > best.Score + 1e-6)
                    {
                        best = score;
                        bestPose = proposal;
                    }
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
        SearchOptions options, PriorityQueue<PoseCandidate, double> candidates, ref bool exceeded, bool anisotropic = false)
    {
        var maxQueue = anisotropic ? options.MaximumCandidates * 4 : options.MaximumCandidates;
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


    /// <summary>회전한 패턴 크기의 40%를 가로·세로 억제 반경으로 쓰고, 각도가 한 간격 이내인 더 높은 이웃만 억제로 본다.</summary>
    private static bool IsAnisotropicMaximum(ReadOnlySpan<(double Score, int Group)> cells, int width, int height, int x, int y,
        int group, ShapeModel model, List<List<int>> groups, int cellSize)
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
                var relX = dx - x;
                var relY = dy - y;
                var localX = Math.Abs((relX * cos) + (relY * sin));
                var localY = Math.Abs((-relX * sin) + (relY * cos));
                if (localX > rWidth + 0.35 || localY > rHeight + 0.35)
                {
                    continue;
                }
                var other = model.Templates[groups[cells[j].Group][0]];
                if (AngleDistance(other.Angle, template.Angle) > angleLimit || Math.Abs(other.Scale - template.Scale) > 0.15)
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

    /// <summary>회전 정렬된 로컬 주축/부축(±1, ±2 셀)을 따라 가림 경계선 능선에 억제된 진짜 중심 후보를 복원하되, 2D 직교 법선 다양성 게이트로 직선 오검출을 차단한다.</summary>
    private static bool TryRecoverOccludedAnisotropicCandidate(PreparedSearchImage image, OrientationMaps map, ShapeModel model,
        List<List<int>> groups, int top, int gridWidth, int gridHeight, ReadOnlySpan<(double Score, int Group)> cellBest,
        int x, int y, (double Score, int Group) cell, int bucketIndex, bool stride2, double maxOcclusionRatio,
        out (double Score, int Group) recoveredCell, out double effectiveScore)
    {
        recoveredCell = cell;
        effectiveScore = cell.Score;
        var horizontalMajor = model.BoundsWidth >= model.BoundsHeight;
        var template = model.Templates[groups[cell.Group][0]];
        var majorAngle = (template.Angle - model.TrainingReferenceAngle) + (horizontalMajor ? 0.0 : Math.PI / 2);
        var ux = Math.Cos(majorAngle);
        var uy = Math.Sin(majorAngle);
        var vx = -uy;
        var vy = ux;
        var dux = Math.Abs(ux) >= 0.38268 ? Math.Sign(ux) : 0;
        var duy = Math.Abs(uy) >= 0.38268 ? Math.Sign(uy) : 0;
        var dvx = Math.Abs(vx) >= 0.38268 ? Math.Sign(vx) : 0;
        var dvy = Math.Abs(vy) >= 0.38268 ? Math.Sign(vy) : 0;
        var m1x = x - dvx;
        var m1y = y - dvy;
        var p1x = x + dvx;
        var p1y = y + dvy;
        var m1Score = m1x >= 0 && m1x < gridWidth && m1y >= 0 && m1y < gridHeight ? cellBest[(m1y * gridWidth) + m1x].Score : double.NegativeInfinity;
        var p1Score = p1x >= 0 && p1x < gridWidth && p1y >= 0 && p1y < gridHeight ? cellBest[(p1y * gridWidth) + p1x].Score : double.NegativeInfinity;
        var hasHigherMinorNeighbor = m1Score > cell.Score || p1Score > cell.Score;
        var isMinorRidgePeak = cell.Score > m1Score && cell.Score > p1Score;
        var maj1x = x - dux;
        var maj1y = y - duy;
        var maj2x = x + dux;
        var maj2y = y + duy;
        var maj1Score = maj1x >= 0 && maj1x < gridWidth && maj1y >= 0 && maj1y < gridHeight ? cellBest[(maj1y * gridWidth) + maj1x].Score : double.NegativeInfinity;
        var maj2Score = maj2x >= 0 && maj2x < gridWidth && maj2y >= 0 && maj2y < gridHeight ? cellBest[(maj2y * gridWidth) + maj2x].Score : double.NegativeInfinity;
        var hasHigherMajorNeighbor = maj1Score > cell.Score || maj2Score > cell.Score;
        var isMajorValley = cell.Score < maj1Score && cell.Score < maj2Score;
        if (hasHigherMinorNeighbor && !isMajorValley)
        {
            // Case 1: 주축 능선 위에서 부축(가림 경계선) 이웃에 의해 억제된 경우
        }
        else if (isMinorRidgePeak && hasHigherMajorNeighbor && !isMajorValley)
        {
            // Case 2: 부축 중심 능선 위에서 가림 반대쪽 주축 이웃에 의해 억제된 경우
        }
        else
        {
            return false;
        }
        if (stride2)
        {
            recoveredCell = RestoreOddAngles(map, model, groups, top, x, y, cell, bucketIndex);
        }
        var bestGroup = recoveredCell.Group;
        var bestTemplate = model.Templates[groups[bestGroup][0]];
        var herePose = new MatchPose((x * map.Step) + 0.5, (y * map.Step) + 0.5, bestTemplate.Angle, bestTemplate.Scale);
        var hereVer = ProbeTemplateScore(image.Gradients[top], model, herePose, top, out var bestProbePose, fullSubCell: true);
        if (hereVer.Score < 0.72)
        {
            for (var g = 0; g < groups.Count; g++)
            {
                if (g == bestGroup || GetAngleBucket(model, groups[g][0]) != bucketIndex)
                {
                    continue;
                }
                var gTemplate = model.Templates[groups[g][0]];
                var gVer = ProbeTemplateScore(image.Gradients[top], model,
                    herePose with { Angle = gTemplate.Angle, Scale = gTemplate.Scale }, top, out var gProbePose, fullSubCell: true);
                if (gVer.Score > hereVer.Score + 0.01)
                {
                    hereVer = gVer;
                    bestGroup = g;
                    bestTemplate = gTemplate;
                    herePose = herePose with { Angle = gTemplate.Angle, Scale = gTemplate.Scale };
                    bestProbePose = gProbePose;
                }
            }
            recoveredCell = (recoveredCell.Score, bestGroup);
        }
        if (hereVer.Score < 0.45)
        {
            return false;
        }
        if (!ContinuousVerifier.HasOrthogonalNormalDiversity(image.Gradients[top], model.Levels[top].Features.AsSpan(),
                default, 0, bestProbePose, model.Options.Polarity))
        {
            return false;
        }
        var stepX = hasHigherMinorNeighbor ? dvx : dux;
        var stepY = hasHigherMinorNeighbor ? dvy : duy;
        var subOffsetX = bestProbePose.X - herePose.X;
        var subOffsetY = bestProbePose.Y - herePose.Y;
        for (var s = -2; s <= 2; s++)
        {
            if (s == 0)
            {
                continue;
            }
            var nx = x + (s * stepX);
            var ny = y + (s * stepY);
            if (nx < 0 || nx >= gridWidth || ny < 0 || ny >= gridHeight)
            {
                continue;
            }
            if (cellBest[(ny * gridWidth) + nx].Score > cell.Score)
            {
                var sVer = ContinuousVerifier.Score(image.Gradients[top], model,
                    bestProbePose with { X = (nx * map.Step) + 0.5 + subOffsetX, Y = (ny * map.Step) + 0.5 + subOffsetY }, top, allowElastic: false);
                if (sVer.Score > hereVer.Score + (hasHigherMinorNeighbor ? 0.04 : 0.01))
                {
                    return false;
                }
            }
        }
        // 부분 가림된 후보의 가시 인라이어 비율(visibleFraction = 1 - maxOcclusionRatio)과
        // 2D 법선 다양성을 통과한 검증 점수(hereVer.Score)를 결합하여 우선순위 큐에서 탈락하지 않도록 보정한다.
        var visibleFraction = Math.Clamp(1.0 - maxOcclusionRatio, 0.50, 0.85);
        var occlusionAdjustedScore = Math.Min(0.98, (hereVer.Score / visibleFraction) + (0.35 * (1.0 - visibleFraction)));
        effectiveScore = Math.Max(recoveredCell.Score, occlusionAdjustedScore);
        return true;
    }

    /// <summary>정밀화 전에 회전 로컬 좌표 기준 (x, y, angle)이 가까운 후보를 점수가 가장 높은 하나로 병합한다. 능선 중복 후보가 정밀화 비용을 키우지 않게 한다.</summary>
    private static PoseCandidate[] ClusterPoses(ShapeModel model, PoseCandidate[] ascending)
    {
        var angleLimit = (model.Options.AngleStepDegrees * Math.PI / 180 * 2.01) + 1e-9;
        var kept = new List<PoseCandidate>();
        for (var index = ascending.Length - 1; index >= 0; index--)
        {
            var candidate = ascending[index];
            var theta = candidate.Pose.Angle - model.TrainingReferenceAngle;
            var cos = Math.Cos(theta);
            var sin = Math.Sin(theta);
            var rx = Math.Max(2, model.BoundsWidth * candidate.Pose.Scale / 3.2);
            var ry = Math.Max(2, model.BoundsHeight * candidate.Pose.Scale / 4.0);
            var duplicate = false;
            foreach (var other in kept)
            {
                var dx = other.Pose.X - candidate.Pose.X;
                var dy = other.Pose.Y - candidate.Pose.Y;
                var localX = Math.Abs((dx * cos) + (dy * sin));
                var localY = Math.Abs((-dx * sin) + (dy * cos));
                if (localX <= rx && localY <= ry &&
                    AngleDistance(other.Pose.Angle, candidate.Pose.Angle) <= angleLimit &&
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

    private static double AngleDistance(double a, double b)
    {
        var diff = Math.Abs(a - b) % (2 * Math.PI);
        return diff > Math.PI ? (2 * Math.PI) - diff : diff;
    }

    /// <summary>NMS를 통과한 피크 셀에서만 양옆 홀수 각도 점수를 계산해 최고 각도를 복원한다. 8방위 버킷이 다른 이웃은 제외한다.</summary>
    private static (double Score, int Group) RestoreOddAngles(OrientationMaps map, ShapeModel model, List<List<int>> groups, int top,
        int x, int y, (double Score, int Group) current, int bucketIndex)
    {
        var best = current;
        Span<int> neighbors = [current.Group - 1, current.Group + 1];
        foreach (var neighbor in neighbors)
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
