// File: src/VisionAlgorithm/OpenSrc/PatMax/PatMaxEngine.cs
using System.Buffers;
using System.Runtime.CompilerServices;

namespace VisionAlgorithm.OpenSrc.PatMax;

/// <summary>
/// Reusable zero-allocation PatMax runtime engine combining Granularity Pyramid (US 6,408,109),
/// PatQuick Coarse Search (US 7,016,539), and 4-Step WLS Attraction Fine Alignment (US 7,065,262).
/// </summary>
public sealed class PatMaxEngine : IDisposable
{
    private const int MaxStackCandidateCount = 128;
    private const int MinInitialDipoleCapacity = 256;
    private const int MaxInitialDipoleCapacity = 65536;
    private const int FullImageFineExtractionPixelLimit = 65536; // 256 x 256
    private const int CandidateWindowFilterMarginPixels = 6;
    private const int AreaToDipoleRatioShift = 3; // (width * height) >> 3 == (width * height) / 8
    private const int ParallelCandidateThreshold = 2;
    private const int MaxParallelWorkers = 16;
    private const int MaxStackWlsScratchFloats = 4608; // 768 model dipoles * 6 arrays

    private readonly GranularityPyramid _runtimePyramid = new();
    private EdgeDipoleSoA _runtimeFineDipoles = EdgeDipoleSoA.Empty;
    private bool _disposed;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public int Find(
        PatMaxModel model,
        ReadOnlySpan<byte> runtimePixels,
        int width,
        int height,
        int stride,
        in PatMaxSearchParams searchParams,
        Span<PatMaxResult> results,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(model);
        if (!model.IsTrained || results.IsEmpty)
        {
            return 0;
        }

        if (searchParams.IsEmpty)
        {
            throw new ArgumentException("PatMaxSearchParams must not be empty.", nameof(searchParams));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var finePixelsTotal = checked(width * height);
        var trainParams = model.TrainParams;
        _runtimePyramid.Build(
            runtimePixels,
            width,
            height,
            stride,
            trainParams.FineGranularity,
            trainParams.CoarseGranularity,
            copyBaseLevel: finePixelsTotal <= FullImageFineExtractionPixelLimit);

        return FindWithSharedPyramid(
            model,
            _runtimePyramid,
            runtimePixels,
            width,
            height,
            stride,
            in searchParams,
            results,
            cancellationToken);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public int FindWithSharedPyramid(
        PatMaxModel model,
        GranularityPyramid sharedPyramid,
        ReadOnlySpan<byte> runtimePixels,
        int width,
        int height,
        int stride,
        in PatMaxSearchParams searchParams,
        Span<PatMaxResult> results,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(sharedPyramid);
        if (!model.IsTrained || results.IsEmpty)
        {
            return 0;
        }

        if (searchParams.IsEmpty)
        {
            throw new ArgumentException("PatMaxSearchParams must not be empty.", nameof(searchParams));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var finePixelsTotal = checked(width * height);
        var trainParams = model.TrainParams;

        var effectiveMaxCandidates = Math.Clamp(Math.Max(searchParams.MaxCandidates, searchParams.MaxResults * 2), 1, MaxStackCandidateCount);
        var effectiveSearchParams = effectiveMaxCandidates == searchParams.MaxCandidates
            ? searchParams
            : PatMaxSearchParams.Create(
                acceptThreshold: searchParams.AcceptThreshold,
                angleStartRadians: searchParams.AngleStartRadians,
                angleExtentRadians: searchParams.AngleExtentRadians,
                scaleMin: searchParams.ScaleMin,
                scaleMax: searchParams.ScaleMax,
                runtimeEdgeThreshold: searchParams.RuntimeEdgeThreshold,
                ignorePolarity: searchParams.IgnorePolarity,
                maxCandidates: effectiveMaxCandidates,
                maxResults: searchParams.MaxResults,
                minSeparationPixels: searchParams.MinSeparationPixels);

        Span<CoarsePoseCandidate> coarseCandidates = stackalloc CoarsePoseCandidate[effectiveMaxCandidates];

        var coarseCount = PatQuickCoarseSearcher.Search(model, sharedPyramid, in effectiveSearchParams, coarseCandidates, cancellationToken);
        if (coarseCount == 0)
        {
            return 0;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var maxResults = Math.Min(searchParams.MaxResults, results.Length);
        var minSeparationSq = searchParams.MinSeparationPixels * searchParams.MinSeparationPixels;
        var acceptedCount = 0;
        const float fineScaleFactor = 1.0f;

        if (finePixelsTotal <= FullImageFineExtractionPixelLimit)
        {
            var initialCapacity = Math.Clamp(finePixelsTotal >> AreaToDipoleRatioShift, MinInitialDipoleCapacity, MaxInitialDipoleCapacity);
            EnsureFineDipoleCapacity(initialCapacity);
            ExtractFineDipolesWithOptionalFilter(
                runtimePixels,
                width,
                height,
                stride,
                in trainParams,
                searchParams.RuntimeEdgeThreshold,
                _runtimeFineDipoles,
                fineScaleFactor,
                originOffsetX: 0.0f,
                originOffsetY: 0.0f);

            var scratchLen = checked(model.FineLevel.Dipoles.Count * PatMaxFineAligner.ScratchArraysPerDipole);
            Span<float> stackScratch = scratchLen <= MaxStackWlsScratchFloats ? stackalloc float[scratchLen] : default;

            for (var i = 0; i < coarseCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var aligned = !stackScratch.IsEmpty
                    ? PatMaxFineAligner.AlignWithScratch(model, _runtimeFineDipoles, in coarseCandidates[i], in effectiveSearchParams, stackScratch, cancellationToken)
                    : PatMaxFineAligner.Align(model, _runtimeFineDipoles, in coarseCandidates[i], in effectiveSearchParams, cancellationToken);
                if (!aligned.IsValid || aligned.Score < searchParams.AcceptThreshold)
                {
                    continue;
                }

                InsertOrSuppressResult(results.Slice(0, maxResults), ref acceptedCount, in aligned, minSeparationSq);
            }

            return acceptedCount;
        }

        // [Perf Why]: On large runtime images (> 256x256), extract fine sub-pixel dipoles only inside each coarse candidate's local bounding window via zero-copy strided spans.
        var maxLocalExtentX = MathF.Max(MathF.Abs(model.MinDx), MathF.Abs(model.MaxDx));
        var maxLocalExtentY = MathF.Max(MathF.Abs(model.MinDy), MathF.Abs(model.MaxDy));
        var modelDiagRadius = MathF.Sqrt((maxLocalExtentX * maxLocalExtentX) + (maxLocalExtentY * maxLocalExtentY));
        var elasticity = MathF.Max(0.5f, trainParams.ElasticityPixels);
        const float invFineScale = 1.0f;

        if (coarseCount >= ParallelCandidateThreshold)
        {
            return AlignCandidatesParallel(
                model,
                runtimePixels,
                width,
                height,
                stride,
                fineScaleFactor,
                coarseCandidates.Slice(0, coarseCount),
                in effectiveSearchParams,
                modelDiagRadius,
                elasticity,
                invFineScale,
                minSeparationSq,
                results.Slice(0, maxResults),
                cancellationToken);
        }

        for (var i = 0; i < coarseCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ref readonly var cand = ref coarseCandidates[i];
            var baseRadius = (modelDiagRadius * cand.Scale) + elasticity + CandidateWindowFilterMarginPixels;
            var viewRadius = (int)MathF.Ceiling(baseRadius * invFineScale) + CandidateWindowFilterMarginPixels;
            var centerViewX = (int)MathF.Round(cand.X * invFineScale);
            var centerViewY = (int)MathF.Round(cand.Y * invFineScale);

            var winLeft = Math.Clamp(centerViewX - viewRadius, 0, width - 1);
            var winTop = Math.Clamp(centerViewY - viewRadius, 0, height - 1);
            var winRight = Math.Clamp(centerViewX + viewRadius, winLeft + 1, width);
            var winBottom = Math.Clamp(centerViewY + viewRadius, winTop + 1, height);
            var winW = winRight - winLeft;
            var winH = winBottom - winTop;

            var winCap = Math.Clamp((winW * winH) >> AreaToDipoleRatioShift, MinInitialDipoleCapacity, MaxInitialDipoleCapacity);
            EnsureFineDipoleCapacity(winCap);

            var windowSlice = runtimePixels.Slice((winTop * stride) + winLeft);
            ExtractFineDipolesWithOptionalFilter(
                windowSlice,
                winW,
                winH,
                stride,
                in trainParams,
                searchParams.RuntimeEdgeThreshold,
                _runtimeFineDipoles,
                fineScaleFactor,
                originOffsetX: winLeft * fineScaleFactor,
                originOffsetY: winTop * fineScaleFactor);

            var aligned = PatMaxFineAligner.Align(model, _runtimeFineDipoles, in cand, in effectiveSearchParams, cancellationToken);
            if (!aligned.IsValid || aligned.Score < searchParams.AcceptThreshold)
            {
                continue;
            }

            InsertOrSuppressResult(results.Slice(0, maxResults), ref acceptedCount, in aligned, minSeparationSq);
        }

        return acceptedCount;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static unsafe int AlignCandidatesParallel(
        PatMaxModel model,
        ReadOnlySpan<byte> finePixels,
        int fineWidth,
        int fineHeight,
        int fineStride,
        float fineScale,
        ReadOnlySpan<CoarsePoseCandidate> coarseCandidates,
        in PatMaxSearchParams searchParams,
        float modelDiagRadius,
        float elasticity,
        float invFineScale,
        float minSeparationSq,
        Span<PatMaxResult> results,
        CancellationToken cancellationToken)
    {
        var coarseCount = coarseCandidates.Length;
        var workerCount = Math.Clamp(Math.Min(Environment.ProcessorCount, coarseCount), 1, MaxParallelWorkers);
        var alignedPool = ArrayPool<PatMaxResult>.Shared.Rent(coarseCount);
        var trainParams = model.TrainParams;
        var localSearchParams = searchParams;
        var scratchFloats = checked(model.FineLevel.Dipoles.Count * PatMaxFineAligner.ScratchArraysPerDipole);

        try
        {
            fixed (byte* pFinePixels = finePixels)
            fixed (CoarsePoseCandidate* pCands = coarseCandidates)
            fixed (PatMaxResult* pAligned = alignedPool)
            {
                var pixelsAddr = (nint)pFinePixels;
                var candsAddr = (nint)pCands;
                var alignedAddr = (nint)pAligned;
                var pixelsLen = ((fineHeight - 1) * fineStride) + fineWidth;

                Parallel.For(0, workerCount, new ParallelOptions
                {
                    MaxDegreeOfParallelism = workerCount,
                    CancellationToken = cancellationToken
                }, w =>
                {
                    var candStart = (w * coarseCount) / workerCount;
                    var candEnd = ((w + 1) * coarseCount) / workerCount;
                    if (candStart >= candEnd)
                    {
                        return;
                    }

                    using var workerDipoles = EdgeDipoleSoA.Rent(MinInitialDipoleCapacity * 4);
                    Span<float> workerScratch = scratchFloats <= MaxStackWlsScratchFloats
                        ? stackalloc float[scratchFloats]
                        : new float[scratchFloats];
                    var localPixels = new ReadOnlySpan<byte>((byte*)pixelsAddr, pixelsLen);

                    for (var i = candStart; i < candEnd; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        ref readonly var cand = ref ((CoarsePoseCandidate*)candsAddr)[i];
                        var baseRadius = (modelDiagRadius * cand.Scale) + elasticity + CandidateWindowFilterMarginPixels;
                        var viewRadius = (int)MathF.Ceiling(baseRadius * invFineScale) + CandidateWindowFilterMarginPixels;
                        var centerViewX = (int)MathF.Round(cand.X * invFineScale);
                        var centerViewY = (int)MathF.Round(cand.Y * invFineScale);

                        var winLeft = Math.Clamp(centerViewX - viewRadius, 0, fineWidth - 1);
                        var winTop = Math.Clamp(centerViewY - viewRadius, 0, fineHeight - 1);
                        var winRight = Math.Clamp(centerViewX + viewRadius, winLeft + 1, fineWidth);
                        var winBottom = Math.Clamp(centerViewY + viewRadius, winTop + 1, fineHeight);
                        var winW = winRight - winLeft;
                        var winH = winBottom - winTop;

                        var windowSlice = localPixels.Slice((winTop * fineStride) + winLeft);
                        ExtractFineDipolesWithOptionalFilter(
                            windowSlice,
                            winW,
                            winH,
                            fineStride,
                            in trainParams,
                            localSearchParams.RuntimeEdgeThreshold,
                            workerDipoles,
                            fineScale,
                            originOffsetX: winLeft * fineScale,
                            originOffsetY: winTop * fineScale);

                        ((PatMaxResult*)alignedAddr)[i] = PatMaxFineAligner.AlignWithScratch(
                            model,
                            workerDipoles,
                            in cand,
                            in localSearchParams,
                            workerScratch,
                            cancellationToken);
                    }
                });
            }

            var acceptedCount = 0;
            for (var i = 0; i < coarseCount; i++)
            {
                ref readonly var aligned = ref alignedPool[i];
                if (!aligned.IsValid || aligned.Score < searchParams.AcceptThreshold)
                {
                    continue;
                }

                InsertOrSuppressResult(results, ref acceptedCount, in aligned, minSeparationSq);
            }

            return acceptedCount;
        }
        finally
        {
            ArrayPool<PatMaxResult>.Shared.Return(alignedPool);
        }
    }

    private static void ExtractFineDipolesWithOptionalFilter(
        ReadOnlySpan<byte> windowPixels,
        int width,
        int height,
        int stride,
        in PatMaxTrainParams trainParams,
        float runtimeEdgeThreshold,
        EdgeDipoleSoA destination,
        float scaleFactor,
        float originOffsetX,
        float originOffsetY)
    {
        if (!PatMaxInspectionFilter.RequiresRuntimePreprocessingPass(in trainParams))
        {
            SubPixelEdgeExtractor.Extract(
                windowPixels,
                width,
                height,
                stride,
                runtimeEdgeThreshold,
                destination,
                scaleFactor,
                originOffsetX,
                originOffsetY);
            return;
        }

        var len = checked(width * height);
        var filteredPool = ArrayPool<byte>.Shared.Rent(len);
        try
        {
            var filteredSpan = filteredPool.AsSpan(0, len);
            PatMaxInspectionFilter.Apply(
                windowPixels,
                width,
                height,
                stride,
                filteredSpan,
                trainParams.PreprocessingFilter,
                trainParams.FineGranularity,
                trainParams.FilterSigma,
                runtimeEdgeThreshold,
                forDisplayPreview: false);

            SubPixelEdgeExtractor.Extract(
                filteredSpan,
                width,
                height,
                width,
                runtimeEdgeThreshold,
                destination,
                scaleFactor,
                originOffsetX,
                originOffsetY);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(filteredPool);
        }
    }

    private void EnsureFineDipoleCapacity(int requiredCapacity)
    {
        if (_runtimeFineDipoles.Capacity < requiredCapacity)
        {
            _runtimeFineDipoles.Dispose();
            _runtimeFineDipoles = EdgeDipoleSoA.Rent(requiredCapacity);
        }
    }

    private static void InsertOrSuppressResult(
        Span<PatMaxResult> buffer,
        ref int count,
        in PatMaxResult candidate,
        float minSeparationSq)
    {
        for (var i = 0; i < count; i++)
        {
            var dx = buffer[i].X - candidate.X;
            var dy = buffer[i].Y - candidate.Y;
            if ((dx * dx) + (dy * dy) <= minSeparationSq)
            {
                if (candidate.Score > buffer[i].Score)
                {
                    buffer[i] = candidate;
                    SortResultUp(buffer, i);
                }

                return;
            }
        }

        if (count < buffer.Length)
        {
            buffer[count] = candidate;
            SortResultUp(buffer, count);
            count++;
            return;
        }

        if (candidate.Score > buffer[count - 1].Score)
        {
            buffer[count - 1] = candidate;
            SortResultUp(buffer, count - 1);
        }
    }

    private static void SortResultUp(Span<PatMaxResult> buffer, int index)
    {
        while (index > 0 && buffer[index].Score > buffer[index - 1].Score)
        {
            (buffer[index - 1], buffer[index]) = (buffer[index], buffer[index - 1]);
            index--;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _runtimePyramid.Dispose();
        _runtimeFineDipoles.Dispose();
    }
}
