using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TeachingToolWpf.Imaging.Matching;
using VisionGeometry;
using VisionImaging;
using VisionImaging.Abstractions;
using VisionOcr.PpOcr;

namespace TeachingToolWpf.Fluent2.Tests.OcrBenchmark;

/// <summary>
/// Executes honest, external-label OCR evaluation across <c>tuning</c> and <c>holdout</c> splits,
/// computing CER, line accuracy, rejection rate, misread-pass rate, confidence-accuracy correlation, and cold/warm latency.
/// </summary>
public static class OcrEvaluationRunner
{
    public static async Task<PpOcrEvaluationSummary> EvaluatePpOcrEngineAsync(
        OcrBenchmarkDataset dataset,
        string repoRootPath,
        PpOcrExecutionDevice device,
        string? splitFilter = null,
        CancellationToken cancellationToken = default,
        InspectionSuiteParameters? parametersOverride = null)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRootPath);

        if (device == PpOcrExecutionDevice.Cuda)
        {
            TryAppendLocalCudaRuntimeDirectoriesToPath(repoRootPath);
        }

        string modelDir = Path.Combine(repoRootPath, "models", "ppocr");
        PpOcrModelOptions modelOptions = PpOcrModelOptions.FromDirectory(modelDir, device);

        var initSw = Stopwatch.StartNew();
        using var ppOcrService = new PpOcrInferenceService(modelOptions);
        initSw.Stop();
        double sessionInitMs = initSw.Elapsed.TotalMilliseconds;

        OcrEngineEvaluationReport report = await EvaluateEngineCoreAsync(
            InspectionSuiteAlgorithmKind.PpOcrOnnx,
            dataset,
            repoRootPath,
            splitFilter,
            ppOcrService,
            cancellationToken,
            parametersOverride);

        return new PpOcrEvaluationSummary(
            Report: report,
            RequestedDevice: ppOcrService.RequestedDevice,
            ActiveDevice: ppOcrService.ActiveDevice,
            CudaFallbackReason: ppOcrService.CudaFallbackReason,
            SessionInitMs: sessionInitMs,
            SessionCreationCount: ppOcrService.SessionCreationCount);
    }

    private static void TryAppendLocalCudaRuntimeDirectoriesToPath(string repoRootPath)
    {
        string packagesDir = Path.Combine(repoRootPath, ".packages");
        if (!Directory.Exists(packagesDir))
        {
            return;
        }

        string currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var extraDirs = new List<string>();
        foreach (string pkgDir in Directory.GetDirectories(packagesDir, "libtorch-cuda-*", SearchOption.TopDirectoryOnly))
        {
            foreach (string nativeDir in Directory.GetDirectories(pkgDir, "native", SearchOption.AllDirectories))
            {
                if (!currentPath.Contains(nativeDir, StringComparison.OrdinalIgnoreCase))
                {
                    extraDirs.Add(nativeDir);
                }
            }
        }

        if (extraDirs.Count > 0)
        {
            Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, extraDirs) + Path.PathSeparator + currentPath);
        }
    }

    private static async Task<OcrEngineEvaluationReport> EvaluateEngineCoreAsync(
        InspectionSuiteAlgorithmKind engine,
        OcrBenchmarkDataset dataset,
        string repoRootPath,
        string? splitFilter,
        IPpOcrInferenceService? ppOcrService,
        CancellationToken cancellationToken,
        InspectionSuiteParameters? parametersOverride)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRootPath);

        List<OcrBenchmarkEntry> targetEntries = string.IsNullOrWhiteSpace(splitFilter)
            ? dataset.Entries.ToList()
            : dataset.Entries.Where(e => string.Equals(e.Split, splitFilter, StringComparison.OrdinalIgnoreCase)).ToList();

        string tempCache = Path.Combine(Path.GetTempPath(), $"TeachingToolWpf_OcrEval_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempCache);

        try
        {
            IImageLoader loader = ImageLoaderHost.Create(tempCache);
            var suiteService = new InspectionSuiteService(loader, () => null, ppOcrService);
            InspectionSuiteParameters evalParams = parametersOverride ?? new InspectionSuiteParameters();

            var samples = new List<OcrRoiEvaluationSample>(targetEntries.Count);
            int exactMatches = 0;
            int totalEdits = 0;
            int totalRefChars = 0;
            int rejectedCount = 0;
            int misreadPassCount = 0;
            double sumFirstMs = 0.0;
            double sumWarmMs = 0.0;

            foreach (OcrBenchmarkEntry entry in targetEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string imgPath = Path.Combine(repoRootPath, entry.ImageFile);
                ImageReference imageRef = new(imgPath, entry.ImageFile, entry.ImageWidth, entry.ImageHeight);
                InspectionSuiteRoiInput roiInput = CreateRoiInput(entry);

                // First call (includes image tile load if first ROI of image + cold algorithm execution)
                InspectionSuiteExecutionOutput coldOut = await suiteService.ExecuteRoiInspectionAsync(
                    imageRef,
                    live: false,
                    engine,
                    [roiInput],
                    [],
                    evalParams,
                    cancellationToken);

                InspectionSuiteItemResult coldItem = coldOut.Items[0];
                double firstMs = coldItem.ElapsedMicroseconds / 1000.0;

                // Warm repeat call on already-cached image source
                Stopwatch warmSw = Stopwatch.StartNew();
                InspectionSuiteExecutionOutput warmOut = await suiteService.ExecuteRoiInspectionAsync(
                    imageRef,
                    live: false,
                    engine,
                    [roiInput],
                    [],
                    evalParams,
                    cancellationToken);
                warmSw.Stop();

                InspectionSuiteItemResult item = warmOut.Items[0];
                double warmMs = item.ElapsedMicroseconds > 0
                    ? item.ElapsedMicroseconds / 1000.0
                    : warmSw.Elapsed.TotalMilliseconds;

                string predictedRaw = item.OcrDiagnostic?.RecognizedText ?? string.Empty;
                string predictedNorm = NormalizeText(predictedRaw);
                string groundTruthNorm = NormalizeText(entry.GroundTruthJoined);

                int refLen = Math.Max(1, groundTruthNorm.Length);
                int edits = ComputeLevenshteinDistance(groundTruthNorm, predictedNorm);
                double cer = (double)edits / refLen;
                bool isExact = string.Equals(groundTruthNorm, predictedNorm, StringComparison.Ordinal);

                bool hasRejectionChar = predictedNorm.Contains('?', StringComparison.Ordinal);
                bool isRejected = string.IsNullOrWhiteSpace(predictedNorm) || hasRejectionChar || !item.IsPass;
                bool isMisreadPass = item.IsPass && !isExact;
                double meanConf = item.OcrDiagnostic?.MeanConfidence ?? 0.0;
                double minConf = item.OcrDiagnostic?.MinConfidence ?? 0.0;

                if (isExact)
                {
                    exactMatches++;
                }

                if (isRejected)
                {
                    rejectedCount++;
                }

                if (isMisreadPass)
                {
                    misreadPassCount++;
                }

                totalEdits += edits;
                totalRefChars += refLen;
                sumFirstMs += firstMs;
                sumWarmMs += warmMs;

                samples.Add(new OcrRoiEvaluationSample(
                    EntryId: entry.Id,
                    ImageFile: entry.ImageFile,
                    Category: entry.Category,
                    Split: entry.Split,
                    GroundTruthText: groundTruthNorm,
                    PredictedText: predictedNorm,
                    IsExactMatch: isExact,
                    IsPassVerdict: item.IsPass,
                    IsRejected: isRejected,
                    IsMisreadPass: isMisreadPass,
                    EditDistance: edits,
                    ReferenceLength: refLen,
                    CharacterErrorRate: cer,
                    MeanConfidence: meanConf,
                    MinConfidence: minConf,
                    FirstCallLatencyMs: firstMs,
                    WarmCallLatencyMs: warmMs));
            }

            int count = Math.Max(1, samples.Count);
            double overallCer = totalRefChars > 0 ? (double)totalEdits / totalRefChars : 1.0;
            double lineAcc = (double)exactMatches / count;
            double rejectionRate = (double)rejectedCount / count;
            double misreadPassRate = (double)misreadPassCount / count;
            double corr = ComputeConfidenceAccuracyCorrelation(samples);

            return new OcrEngineEvaluationReport(
                Engine: engine,
                SplitName: string.IsNullOrWhiteSpace(splitFilter) ? "all" : splitFilter,
                TotalRois: samples.Count,
                ExactMatchRois: exactMatches,
                TotalEditDistance: totalEdits,
                TotalReferenceChars: totalRefChars,
                CharacterErrorRate: overallCer,
                LineExactAccuracy: lineAcc,
                RejectionRate: rejectionRate,
                MisreadPassRate: misreadPassRate,
                ConfidenceAccuracyCorrelation: corr,
                MeanFirstCallLatencyMs: sumFirstMs / count,
                MeanWarmCallLatencyMs: sumWarmMs / count,
                Samples: samples);
        }
        finally
        {
            if (Directory.Exists(tempCache))
            {
                try
                {
                    Directory.Delete(tempCache, recursive: true);
                }
                catch
                {
                    // Ignore transient file lock during temp cleanup.
                }
            }
        }
    }

    public static string NormalizeText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string unifiedLines = text
            .Replace("\r\n", " / ", StringComparison.Ordinal)
            .Replace("\n", " / ", StringComparison.Ordinal);

        var sb = new StringBuilder(unifiedLines.Length);
        bool prevSpace = true;
        for (int i = 0; i < unifiedLines.Length; i++)
        {
            char c = unifiedLines[i];
            if (char.IsWhiteSpace(c))
            {
                if (!prevSpace)
                {
                    sb.Append(' ');
                    prevSpace = true;
                }
            }
            else
            {
                sb.Append(c);
                prevSpace = false;
            }
        }

        return sb.ToString().Trim();
    }

    public static int ComputeLevenshteinDistance(ReadOnlySpan<char> source, ReadOnlySpan<char> target)
    {
        int n = source.Length;
        int m = target.Length;
        if (n == 0)
        {
            return m;
        }

        if (m == 0)
        {
            return n;
        }

        int[] prev = new int[m + 1];
        int[] curr = new int[m + 1];
        for (int j = 0; j <= m; j++)
        {
            prev[j] = j;
        }

        for (int i = 1; i <= n; i++)
        {
            curr[0] = i;
            char sc = source[i - 1];
            for (int j = 1; j <= m; j++)
            {
                int cost = sc == target[j - 1] ? 0 : 1;
                int del = prev[j] + 1;
                int ins = curr[j - 1] + 1;
                int sub = prev[j - 1] + cost;
                curr[j] = Math.Min(del, Math.Min(ins, sub));
            }

            (prev, curr) = (curr, prev);
        }

        return prev[m];
    }

    private static double ComputeConfidenceAccuracyCorrelation(IReadOnlyList<OcrRoiEvaluationSample> samples)
    {
        if (samples.Count < 2)
        {
            return 0.0;
        }

        // Correlate MeanConfidence with per-ROI character accuracy = Clamp(1 - CER, 0, 1)
        double meanX = 0.0;
        double meanY = 0.0;
        for (int i = 0; i < samples.Count; i++)
        {
            meanX += samples[i].MeanConfidence;
            meanY += Math.Clamp(1.0 - samples[i].CharacterErrorRate, 0.0, 1.0);
        }

        meanX /= samples.Count;
        meanY /= samples.Count;

        double cov = 0.0;
        double varX = 0.0;
        double varY = 0.0;
        for (int i = 0; i < samples.Count; i++)
        {
            double dx = samples[i].MeanConfidence - meanX;
            double dy = Math.Clamp(1.0 - samples[i].CharacterErrorRate, 0.0, 1.0) - meanY;
            cov += dx * dy;
            varX += dx * dx;
            varY += dy * dy;
        }

        double denom = Math.Sqrt(varX * varY);
        return denom > 1e-9 ? cov / denom : 0.0;
    }

    private static InspectionSuiteRoiInput CreateRoiInput(OcrBenchmarkEntry entry)
    {
        double angleRad = entry.AngleDeg * (Math.PI / 180.0);
        double cos = Math.Cos(angleRad);
        double sin = Math.Sin(angleRad);
        double hw = entry.Width * 0.5;
        double hh = entry.Height * 0.5;

        PointD Corner(double lx, double ly) => new(
            entry.CenterX + (lx * cos) - (ly * sin),
            entry.CenterY + (lx * sin) + (ly * cos));

        return new InspectionSuiteRoiInput(
            Guid.NewGuid(),
            entry.Id,
            new PointD(entry.CenterX, entry.CenterY),
            entry.Width,
            entry.Height,
            angleRad,
            [
                Corner(-hw, -hh),
                Corner(+hw, -hh),
                Corner(+hw, +hh),
                Corner(-hw, +hh)
            ]);
    }
}
