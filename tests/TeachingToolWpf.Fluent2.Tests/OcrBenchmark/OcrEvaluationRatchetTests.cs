using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TeachingToolWpf.Imaging.Matching;
using VisionOcr.PpOcr;
using Xunit;
using Xunit.Abstractions;

namespace TeachingToolWpf.Fluent2.Tests.OcrBenchmark;

public sealed class OcrEvaluationRatchetTests(ITestOutputHelper output)
{
    private const double MinRequiredHoldoutShare = 0.30;
    public const double TargetMaxTuningMisreadPassRate = 0.05;

    // Non-regression ratchets locked to measured Stage 4 PP-OCRv4 ONNX 60-ROI baseline in docs/ocr-evaluation.md (zero weakening):
    private const int MinRequiredPpOcrExactMatches = 32; // Measured: 14/29 tuning + 18/31 holdout = 32/60 (53.3%)
    private const double MaxAllowedPpOcrCer = 0.0900; // Measured: 8.71% (99 / 1137), improved from 8.79% (100 / 1137)
    private const double MaxAllowedPpOcrOverallMisreadPassRate = 0.0700; // Measured: 4 / 60 (6.7%), tuning = 1 / 29 (3.45% <= 5%)

    [Fact]
    public void Dataset_HasNonOverlappingTuningAndHoldoutSplits_WithAtLeastThirtyPercentHoldout()
    {
        (OcrBenchmarkDataset dataset, string repoRootPath, _) = OcrBenchmarkDataset.LoadFromDefaultLocation();

        Assert.NotEmpty(dataset.Entries);

        int totalCount = dataset.Entries.Count;
        int tuningCount = dataset.Entries.Count(e => string.Equals(e.Split, "tuning", StringComparison.OrdinalIgnoreCase));
        int holdoutCount = dataset.Entries.Count(e => string.Equals(e.Split, "holdout", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(totalCount, tuningCount + holdoutCount);
        double holdoutRatio = (double)holdoutCount / totalCount;
        Assert.True(
            holdoutRatio >= MinRequiredHoldoutShare,
            $"Holdout share {holdoutRatio:P1} is below required {MinRequiredHoldoutShare:P0}.");

        foreach (OcrBenchmarkEntry entry in dataset.Entries)
        {
            string imgPath = Path.Combine(repoRootPath, entry.ImageFile);
            Assert.True(File.Exists(imgPath), $"Missing benchmark image: {imgPath}");
            Assert.NotEmpty(entry.GroundTruthLines);
        }

        // Ensure zero pixel overlap between tuning and holdout ROIs on the same image.
        var byImage = dataset.Entries.GroupBy(e => e.ImageFile, StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, OcrBenchmarkEntry> group in byImage)
        {
            List<OcrBenchmarkEntry> tuningRois = group.Where(e => e.Split == "tuning").ToList();
            List<OcrBenchmarkEntry> holdoutRois = group.Where(e => e.Split == "holdout").ToList();

            foreach (OcrBenchmarkEntry t in tuningRois)
            {
                foreach (OcrBenchmarkEntry h in holdoutRois)
                {
                    Assert.False(
                        BoxesOverlapAxisAligned(t, h),
                        $"Overlap detected on {group.Key} between tuning ROI '{t.Id}' and holdout ROI '{h.Id}'.");
                }
            }
        }
    }

    [Fact]
    public async Task EvaluatePpOcrOnnx_CalibratesOnTuningAndReportsHoldoutCpuAndCuda()
    {
        (OcrBenchmarkDataset dataset, string repoRootPath, _) = OcrBenchmarkDataset.LoadFromDefaultLocation();

        // 1) Tuning-only calibration sweep with low floor (0.05 / 0.00) — NEVER touches holdout!
        var sweepParams = new InspectionSuiteParameters(
            PpOcrMinConfidence: 0.05f,
            PpOcrMinCharacterConfidence: 0.0f);
        PpOcrEvaluationSummary tuningSweep = await OcrEvaluationRunner.EvaluatePpOcrEngineAsync(
            dataset,
            repoRootPath,
            PpOcrExecutionDevice.Cpu,
            splitFilter: "tuning",
            parametersOverride: sweepParams);

        int nTuning = tuningSweep.Report.TotalRois;
        double bestMeanThr = 1.00;
        double bestMinCharThr = 0.0;
        double bestRejRate = 1.00;
        double bestMpRate = 1.00;

        for (int mStep = 300; mStep <= 1000; mStep += 5)
        {
            double mThr = mStep / 1000.0;
            for (int cStep = 0; cStep <= mStep; cStep += 25)
            {
                double cThr = cStep / 1000.0;
                int mp = 0;
                int rej = 0;
                foreach (OcrRoiEvaluationSample s in tuningSweep.Report.Samples)
                {
                    bool pass = !string.IsNullOrWhiteSpace(s.PredictedText) &&
                                !s.PredictedText.Contains('?', StringComparison.Ordinal) &&
                                s.MeanConfidence >= mThr &&
                                s.MinConfidence >= cThr;
                    if (!pass)
                    {
                        rej++;
                    }
                    else if (!s.IsExactMatch)
                    {
                        mp++;
                    }
                }

                double mpRate = (double)mp / nTuning;
                double rejRate = (double)rej / nTuning;
                if (mpRate <= TargetMaxTuningMisreadPassRate && rejRate < bestRejRate)
                {
                    bestMeanThr = mThr;
                    bestMinCharThr = cThr;
                    bestRejRate = rejRate;
                    bestMpRate = mpRate;
                }
            }
        }

        // 2) Full 60-ROI evaluation with default named-const parameters on CPU and CUDA
        PpOcrEvaluationSummary cpuSummary = await OcrEvaluationRunner.EvaluatePpOcrEngineAsync(
            dataset,
            repoRootPath,
            PpOcrExecutionDevice.Cpu,
            splitFilter: null);

        PpOcrEvaluationSummary cudaSummary = await OcrEvaluationRunner.EvaluatePpOcrEngineAsync(
            dataset,
            repoRootPath,
            PpOcrExecutionDevice.Cuda,
            splitFilter: null);

        var upperParams = new InspectionSuiteParameters(
            OcrAllowedCharacters: VisionAlgorithm.OpenSrc.IndustrialOcrOcv.OcrAllowedCharacterSet.UpperAlphaNumericAndSymbols);
        PpOcrEvaluationSummary cpuUpperSummary = await OcrEvaluationRunner.EvaluatePpOcrEngineAsync(
            dataset,
            repoRootPath,
            PpOcrExecutionDevice.Cpu,
            splitFilter: null,
            parametersOverride: upperParams);

        var highSpeedParams = new InspectionSuiteParameters(
            PpOcrPreset: PpOcrSpeedPreset.HighSpeedProduction);
        PpOcrEvaluationSummary cpuHighSpeedSummary = await OcrEvaluationRunner.EvaluatePpOcrEngineAsync(
            dataset,
            repoRootPath,
            PpOcrExecutionDevice.Cpu,
            splitFilter: null,
            parametersOverride: highSpeedParams);

        var sb = new StringBuilder();
        sb.AppendLine("=== PP-OCRv4 ONNX TUNING-ONLY CALIBRATION SWEEP ===");
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Best Tuning Threshold (MisreadPass<=5%): meanThr={bestMeanThr:F3}, minCharThr={bestMinCharThr:F3} -> TuningReject={bestRejRate:P2}, TuningMisreadPass={bestMpRate:P2}"));
        foreach (OcrRoiEvaluationSample ts in tuningSweep.Report.Samples.OrderByDescending(x => x.MeanConfidence))
        {
            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  [TUNING-SWEEP] {ts.EntryId,-22} [{ts.Category}] Exact={ts.IsExactMatch,-5} CER={ts.CharacterErrorRate:F3} Mean={ts.MeanConfidence:F4} Min={ts.MinConfidence:F4} | GT=\"{ts.GroundTruthText}\" | PRED=\"{ts.PredictedText}\""));
        }

        sb.AppendLine();
        sb.AppendLine("=== PP-OCRv4 ONNX CPU EVALUATION ===");
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"CPU Device: Requested={cpuSummary.RequestedDevice}, Active={cpuSummary.ActiveDevice}, SessionInitMs={cpuSummary.SessionInitMs:F2}, SessionsCreated={cpuSummary.SessionCreationCount}"));
        AppendSummaryLine(sb, cpuSummary.Report);
        AppendSubSplitSummary(sb, cpuSummary.Report, "tuning");
        AppendSubSplitSummary(sb, cpuSummary.Report, "holdout");
        AppendCategoryBreakdown(sb, "CPU", cpuSummary.Report);

        sb.AppendLine();
        sb.AppendLine("=== PP-OCRv4 ONNX CPU HIGHSPEED PRODUCTION PRESET EVALUATION (60 ROIS) ===");
        AppendSummaryLine(sb, cpuHighSpeedSummary.Report);
        AppendSubSplitSummary(sb, cpuHighSpeedSummary.Report, "tuning");
        AppendSubSplitSummary(sb, cpuHighSpeedSummary.Report, "holdout");
        AppendCategoryBreakdown(sb, "CPU-HIGHSPEED", cpuHighSpeedSummary.Report);

        sb.AppendLine();
        sb.AppendLine("=== PP-OCRv4 ONNX CUDA EVALUATION ===");
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"CUDA Device: Requested={cudaSummary.RequestedDevice}, Active={cudaSummary.ActiveDevice}, FallbackReason=\"{cudaSummary.CudaFallbackReason ?? "NONE"}\", SessionInitMs={cudaSummary.SessionInitMs:F2}, SessionsCreated={cudaSummary.SessionCreationCount}"));
        AppendSummaryLine(sb, cudaSummary.Report);
        AppendSubSplitSummary(sb, cudaSummary.Report, "tuning");
        AppendSubSplitSummary(sb, cudaSummary.Report, "holdout");

        sb.AppendLine();
        sb.AppendLine("=== PP-OCRv4 ALL 60 ROIS (CPU DEFAULT) ===");
        foreach (OcrRoiEvaluationSample s in cpuSummary.Report.Samples)
        {
            OcrRoiEvaluationSample upperS = cpuUpperSummary.Report.Samples.Single(u => u.EntryId == s.EntryId);
            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  [PPOCR-CPU][{s.Split}][{s.Category}] {s.EntryId}: CER={s.CharacterErrorRate:F3} Exact={s.IsExactMatch} Pass={s.IsPassVerdict} Reject={s.IsRejected} MisreadPass={s.IsMisreadPass} Mean={s.MeanConfidence:F3} Min={s.MinConfidence:F3} WarmMs={s.WarmCallLatencyMs:F2} | GT=\"{s.GroundTruthText}\" | PRED=\"{s.PredictedText}\" | UPPER_PRED=\"{upperS.PredictedText}\""));
        }

        output.WriteLine(sb.ToString());

        Assert.Equal(3, cpuSummary.SessionCreationCount);
        List<OcrRoiEvaluationSample> cpuTuning = cpuSummary.Report.Samples
            .Where(s => string.Equals(s.Split, "tuning", StringComparison.OrdinalIgnoreCase))
            .ToList();
        double cpuTuningMpRate = (double)cpuTuning.Count(s => s.IsMisreadPass) / cpuTuning.Count;
        Assert.True(
            cpuTuningMpRate <= TargetMaxTuningMisreadPassRate,
            $"PP-OCRv4 CPU tuning MisreadPassRate {cpuTuningMpRate:P2} exceeded {TargetMaxTuningMisreadPassRate:P2}. Adjust PpOcrRunOptions.DefaultMinAcceptConfidence/DefaultMinCharacterConfidence to ({bestMeanThr:F3}, {bestMinCharThr:F3})!");

        // Enforce strict non-regression ratchets on full 60-ROI benchmark + real kensa.png chip markings
        Assert.True(
            cpuSummary.Report.ExactMatchRois >= MinRequiredPpOcrExactMatches,
            $"PP-OCRv4 ExactMatchRois regressed: {cpuSummary.Report.ExactMatchRois}/{cpuSummary.Report.TotalRois} < {MinRequiredPpOcrExactMatches}");
        Assert.True(
            cpuSummary.Report.CharacterErrorRate <= MaxAllowedPpOcrCer,
            $"PP-OCRv4 CER regressed: {cpuSummary.Report.CharacterErrorRate:P2} > {MaxAllowedPpOcrCer:P2}");
        Assert.True(
            cpuSummary.Report.MisreadPassRate <= MaxAllowedPpOcrOverallMisreadPassRate,
            $"PP-OCRv4 overall MisreadPassRate regressed: {cpuSummary.Report.MisreadPassRate:P2} > {MaxAllowedPpOcrOverallMisreadPassRate:P2}");

        List<OcrRoiEvaluationSample> kensaSamples = cpuSummary.Report.Samples
            .Where(s => s.EntryId.StartsWith("Kensa_", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(10, kensaSamples.Count);
        List<OcrRoiEvaluationSample> kensaVendorSamples = kensaSamples
            .Where(s => s.EntryId.EndsWith("_Vendor", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(5, kensaVendorSamples.Count);
        Assert.All(kensaVendorSamples, ks => Assert.True(ks.IsExactMatch, $"{ks.EntryId}: GT='{ks.GroundTruthText}', PRED='{ks.PredictedText}'"));
        Assert.All(kensaSamples, ks => Assert.True(ks.CharacterErrorRate <= 0.2001, $"{ks.EntryId}: CER={ks.CharacterErrorRate:F3}, GT='{ks.GroundTruthText}', PRED='{ks.PredictedText}'"));

        // Enforce AllowedCharacterSet (UpperAlphaNumericAndSymbols) eliminates lowercase misreads cleanly via CTC logit masking
        List<OcrRoiEvaluationSample> resTuningUpper = cpuUpperSummary.Report.Samples
            .Where(s => s.Split == "tuning" && IsStrictlyUppercaseGroundTruth(s.GroundTruthText))
            .ToList();
        List<OcrRoiEvaluationSample> resHoldoutUpper = cpuUpperSummary.Report.Samples
            .Where(s => s.Split == "holdout" && IsStrictlyUppercaseGroundTruth(s.GroundTruthText))
            .ToList();
        Assert.Equal(0, resTuningUpper.Sum(s => CountAsciiLowercase(s.PredictedText)));
        Assert.Equal(0, resHoldoutUpper.Sum(s => CountAsciiLowercase(s.PredictedText)));
    }

    private static bool IsStrictlyUppercaseGroundTruth(string gt)
    {
        bool hasUpperOrDigit = false;
        foreach (char c in gt)
        {
            if (c is >= 'a' and <= 'z')
            {
                return false;
            }

            if ((c is >= 'A' and <= 'Z') || (c is >= '0' and <= '9'))
            {
                hasUpperOrDigit = true;
            }
        }

        return hasUpperOrDigit;
    }

    private static int CountAsciiLowercase(string text)
        => text.Count(c => c is >= 'a' and <= 'z');

    private static void AppendSummaryLine(StringBuilder sb, OcrEngineEvaluationReport r)
    {
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"SUMMARY [{r.Engine}] split={r.SplitName} ROIs={r.TotalRois} Exact={r.ExactMatchRois}/{r.TotalRois} ({r.LineExactAccuracy:P1}) CER={r.CharacterErrorRate:P2} ({r.TotalEditDistance}/{r.TotalReferenceChars}) Reject={r.RejectionRate:P1} MisreadPass={r.MisreadPassRate:P1} ConfCorr={r.ConfidenceAccuracyCorrelation:F3} ColdMs={r.MeanFirstCallLatencyMs:F2} WarmMs={r.MeanWarmCallLatencyMs:F2}"));
    }

    private static void AppendSubSplitSummary(StringBuilder sb, OcrEngineEvaluationReport fullReport, string split)
    {
        List<OcrRoiEvaluationSample> sub = fullReport.Samples
            .Where(s => string.Equals(s.Split, split, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (sub.Count == 0)
        {
            return;
        }

        int exact = sub.Count(s => s.IsExactMatch);
        int edits = sub.Sum(s => s.EditDistance);
        int refChars = sub.Sum(s => s.ReferenceLength);
        int rejected = sub.Count(s => s.IsRejected);
        int misreadPass = sub.Count(s => s.IsMisreadPass);
        double cer = refChars > 0 ? (double)edits / refChars : 1.0;
        double lineAcc = (double)exact / sub.Count;
        double rejRate = (double)rejected / sub.Count;
        double mpRate = (double)misreadPass / sub.Count;
        double coldMs = sub.Average(s => s.FirstCallLatencyMs);
        double warmMs = sub.Average(s => s.WarmCallLatencyMs);
        double corr = ComputeSubCorrelation(sub);

        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"SUMMARY [{fullReport.Engine}] split={split} ROIs={sub.Count} Exact={exact}/{sub.Count} ({lineAcc:P1}) CER={cer:P2} ({edits}/{refChars}) Reject={rejRate:P1} MisreadPass={mpRate:P1} ConfCorr={corr:F3} ColdMs={coldMs:F2} WarmMs={warmMs:F2}"));
    }

    private static double ComputeSubCorrelation(IReadOnlyList<OcrRoiEvaluationSample> samples)
    {
        if (samples.Count < 2)
        {
            return 0.0;
        }

        double meanX = samples.Average(s => s.MeanConfidence);
        double meanY = samples.Average(s => Math.Clamp(1.0 - s.CharacterErrorRate, 0.0, 1.0));
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

    private static bool BoxesOverlapAxisAligned(OcrBenchmarkEntry a, OcrBenchmarkEntry b)
    {
        double aLeft = a.CenterX - (a.Width * 0.5);
        double aRight = a.CenterX + (a.Width * 0.5);
        double aTop = a.CenterY - (a.Height * 0.5);
        double aBottom = a.CenterY + (a.Height * 0.5);

        double bLeft = b.CenterX - (b.Width * 0.5);
        double bRight = b.CenterX + (b.Width * 0.5);
        double bTop = b.CenterY - (b.Height * 0.5);
        double bBottom = b.CenterY + (b.Height * 0.5);

        return aLeft < bRight && aRight > bLeft && aTop < bBottom && aBottom > bTop;
    }

    private static void AppendCategoryBreakdown(StringBuilder sb, string tag, OcrEngineEvaluationReport report)
    {
        foreach (string split in new[] { "tuning", "holdout" })
        {
            var groups = report.Samples
                .Where(s => string.Equals(s.Split, split, StringComparison.OrdinalIgnoreCase))
                .GroupBy(s => s.Category, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

            foreach (IGrouping<string, OcrRoiEvaluationSample> g in groups)
            {
                List<OcrRoiEvaluationSample> list = g.ToList();
                int exact = list.Count(x => x.IsExactMatch);
                int edits = list.Sum(x => x.EditDistance);
                int refs = list.Sum(x => x.ReferenceLength);
                int rej = list.Count(x => x.IsRejected);
                int mp = list.Count(x => x.IsMisreadPass);
                double cer = refs > 0 ? (double)edits / refs : 1.0;
                sb.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  [CAT:{tag}:{split}:{g.Key}] n={list.Count} Exact={exact}/{list.Count} ({(double)exact / list.Count:P1}) CER={cer:P2} ({edits}/{refs}) Reject={(double)rej / list.Count:P1} MisreadPass={(double)mp / list.Count:P1}"));
            }
        }
    }
}
