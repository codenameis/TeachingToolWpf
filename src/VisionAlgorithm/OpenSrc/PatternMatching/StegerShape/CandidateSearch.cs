// File: src/VisionAlgorithm/OpenSrc/PatternMatching/StegerShape/CandidateSearch.cs
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

internal static class CandidateSearch
{
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static CandidateBatch Find(
        PreparedSearchImage image,
        ShapeModel model,
        ShapeSearchOptions options,
        CancellationToken token,
        IShapeCandidateScorer? scorer = null,
        bool optimizedLocalSearch = false)
    {
        var timer = Stopwatch.StartNew();
        var coarse = CoarseLevelScanner.ScanTopLevel(image, model, options, token, scorer);
        var gridTime = timer.Elapsed.TotalMilliseconds;

        timer.Restart();
        var ordered = coarse.OrderedCandidates;
        if (coarse.IsAnisotropic)
        {
            ordered = PoseClusterer.ClusterCoarsePoses(model, ordered);
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
            // [Perf Why]: Each worker writes exclusively to its own slot so completion order never alters deterministic candidate ordering.
            Parallel.For(
                0,
                ordered.Length,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = options.MaximumParallelism,
                    CancellationToken = token
                },
                index =>
                {
                    ordered[index] = Refine(image, model, ordered[index], options, token, optimizedLocalSearch);
                });
        }

        var deduplicated = PoseClusterer.SortAndDeduplicateRefined(model, ordered, options, token);
        return new CandidateBatch(deduplicated, coarse.BudgetExceeded, coarse.EvaluatedTemplateGroups)
        {
            GridMilliseconds = gridTime,
            PoseMilliseconds = timer.Elapsed.TotalMilliseconds,
            CandidatesBeforeRefinement = ordered.Length
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static PoseCandidate Refine(
        PreparedSearchImage image,
        ShapeModel model,
        PoseCandidate candidate,
        ShapeSearchOptions options,
        CancellationToken token,
        bool optimizedLocalSearch = false)
        => PyramidCandidateTracker.Refine(image, model, candidate, options, token, optimizedLocalSearch);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void Accumulate(
        OrientationMaps map,
        ImmutableArray<QuantizedFeature> features,
        ushort[] scores,
        int gridWidth,
        int gridHeight,
        bool simd,
        CancellationToken token)
        => CoarseLevelScanner.Accumulate(map, features, scores, gridWidth, gridHeight, simd, token);
}
