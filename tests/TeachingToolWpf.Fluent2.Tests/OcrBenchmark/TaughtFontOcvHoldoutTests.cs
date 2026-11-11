using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media.Imaging;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.IndustrialOcrOcv;
using Xunit;
using Xunit.Abstractions;

namespace TeachingToolWpf.Fluent2.Tests.OcrBenchmark;

public sealed class TaughtFontOcvHoldoutTests(ITestOutputHelper output)
{
    public const double TargetNormalOkRate = 0.90;
    public const double TargetDefectDetectionRate = 0.95;

    private static readonly (char From, char To)[] HomoglyphPairs =
    [
        ('8', 'B'), ('B', '8'),
        ('0', 'O'), ('O', '0'),
        ('1', 'I'), ('I', '1'),
        ('1', 'l'), ('l', '1'),
        ('5', 'S'), ('S', '5'),
        ('2', 'Z'), ('Z', '2'),
    ];

    [Fact]
    [Trait("Category", "KnownGap")]
    public void TaughtFontOcv_PerPrintGroup_RealIndependent_MeetsTargetRates()
    {
        (OcrBenchmarkDataset dataset, string repoRootPath, _) = OcrBenchmarkDataset.LoadFromDefaultLocation();
        Assert.Equal(2, dataset.SchemaVersion);
        Assert.NotEmpty(dataset.PrintGroups);

        var entryById = dataset.Entries.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var grayCache = new Dictionary<string, (byte[] Pixels, int Width, int Height)>(StringComparer.OrdinalIgnoreCase);
        using var workspace = new VisionWorkspace();

        var sb = new StringBuilder();
        sb.AppendLine("=== STAGE 3 REWORK: PER-PRINT-GROUP REAL INDEPENDENT OCV REPORT ===");

        int totalVerifySamples = 0;
        int totalNormalOk = 0;
        int totalNormalReview = 0;
        int totalNormalFalseNg = 0;
        var globalDefectCounts = new DefectTallies();

        foreach (OcrPrintGroupSpec group in dataset.PrintGroups)
        {
            if (!string.IsNullOrWhiteSpace(group.RealIndependentNotRunReason))
            {
                sb.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"[GROUP][{group.GroupId}] REAL_INDEPENDENT = NOT_RUN | Reason: {group.RealIndependentNotRunReason}"));
                continue;
            }

            // Verify zero pixel overlap between all teach regions and all verify regions in this group
            List<(string Id, string ImageFile, double Left, double Top, double Right, double Bottom, byte[] Pixels, int W, int H, string Gt)> teachRegions = [];
            if (group.OcvSpecimens is not null)
            {
                foreach (OcrOcvSpecimenEntry spec in group.OcvSpecimens)
                {
                    (byte[] px, int w, int h) = CropRectPixels(
                        spec.ImageFile,
                        spec.CenterX,
                        spec.CenterY,
                        spec.Width,
                        spec.Height,
                        repoRootPath,
                        grayCache);
                    teachRegions.Add((
                        spec.Id,
                        spec.ImageFile,
                        spec.CenterX - (spec.Width * 0.5),
                        spec.CenterY - (spec.Height * 0.5),
                        spec.CenterX + (spec.Width * 0.5),
                        spec.CenterY + (spec.Height * 0.5),
                        px,
                        w,
                        h,
                        spec.GroundTruth));
                }
            }

            foreach (string teachId in group.TeachEntryIds)
            {
                OcrBenchmarkEntry te = entryById[teachId];
                Assert.Single(te.GroundTruthLines);
                (byte[] px, int w, int h) = CropRoiPixels(te, repoRootPath, grayCache);
                teachRegions.Add((
                    te.Id,
                    te.ImageFile,
                    te.CenterX - (te.Width * 0.5),
                    te.CenterY - (te.Height * 0.5),
                    te.CenterX + (te.Width * 0.5),
                    te.CenterY + (te.Height * 0.5),
                    px,
                    w,
                    h,
                    te.GroundTruthLines[0]));
            }

            Assert.NotEmpty(teachRegions);
            Assert.NotEmpty(group.VerifyEntryIds);

            foreach (string verifyId in group.VerifyEntryIds)
            {
                OcrBenchmarkEntry ve = entryById[verifyId];
                double vLeft = ve.CenterX - (ve.Width * 0.5);
                double vTop = ve.CenterY - (ve.Height * 0.5);
                double vRight = ve.CenterX + (ve.Width * 0.5);
                double vBottom = ve.CenterY + (ve.Height * 0.5);

                foreach (var tr in teachRegions)
                {
                    if (string.Equals(tr.ImageFile, ve.ImageFile, StringComparison.OrdinalIgnoreCase))
                    {
                        bool overlaps = vLeft < tr.Right && vRight > tr.Left && vTop < tr.Bottom && vBottom > tr.Top;
                        Assert.False(overlaps, $"Pixel overlap detected in {group.GroupId} between teach '{tr.Id}' and verify '{ve.Id}'.");
                    }
                }
            }

            // Teach golden font strictly from this group's real teach samples (ZERO synthetic Consolas/Arial!)
            List<GoldenCharSample> goldenSamples = [];
            List<GoldenCharSample> rawPatchSamples = [];
            foreach (var tr in teachRegions)
            {
                OcvVerificationOptions teachOpts = BuildOptionsForStrip(group, tr.W, tr.H, tr.Gt);
                var view = new ReadOnlyImageView2D<byte>(tr.Pixels, tr.W, tr.H, tr.W);
                var fixture = new FixturePose2D(tr.W * 0.5f, tr.H * 0.5f, 0f);
                IReadOnlyList<GoldenCharSample> extracted = TaughtFontOcvVerifier.ExtractTeachingSamplesFromLine(
                    view,
                    in fixture,
                    tr.Gt,
                    teachOpts,
                    workspace);
                Assert.True(
                    extracted.Count > 0,
                    $"Teach sample '{tr.Id}' in group '{group.GroupId}' failed exact character count segmentation for GT='{tr.Gt}'.");
                goldenSamples.AddRange(extracted);
                rawPatchSamples.AddRange(ExtractRawPatchSamplesFromLine(tr.Pixels, tr.W, tr.H, tr.Gt, teachOpts, workspace));
            }

            IndustrialOcrFontModel groupFont = IndustrialOcrTrainer.Train(goldenSamples.ToArray());
            var verifier = new TaughtFontOcvVerifier(groupFont);
            Dictionary<char, List<GoldenCharSample>> patchPool = BuildRealPatchPool(rawPatchSamples);

            int grpVerify = 0;
            int grpOk = 0;
            int grpReview = 0;
            int grpFalseNg = 0;
            var grpDefects = new DefectTallies();

            foreach (string verifyId in group.VerifyEntryIds)
            {
                OcrBenchmarkEntry ve = entryById[verifyId];
                Assert.Single(ve.GroundTruthLines);
                string gt = ve.GroundTruthLines[0];
                (byte[] roiPixels, int w, int h) = CropRoiPixels(ve, repoRootPath, grayCache);

                OcvVerificationOptions verifyOpts = BuildOptionsForStrip(group, w, h, gt);
                EvaluateSingleVerifySample(
                    sampleLabel: ve.Id,
                    roiPixels: roiPixels,
                    w: w,
                    h: h,
                    gt: gt,
                    options: verifyOpts,
                    verifier: verifier,
                    patchPool: patchPool,
                    workspace: workspace,
                    log: sb,
                    verifyTotal: ref grpVerify,
                    normalOk: ref grpOk,
                    normalReview: ref grpReview,
                    normalFalseNg: ref grpFalseNg,
                    defects: grpDefects);
            }

            totalVerifySamples += grpVerify;
            totalNormalOk += grpOk;
            totalNormalReview += grpReview;
            totalNormalFalseNg += grpFalseNg;
            globalDefectCounts.AddFrom(grpDefects);

            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"[GROUP][{group.GroupId}] Teach={teachRegions.Count} ({goldenSamples.Count} glyphs) | Verify={grpVerify} | Normal OK={FormatRateWithWilson(grpOk, grpVerify)}, Review={FormatRateWithWilson(grpReview, grpVerify)}, FalseNG={FormatRateWithWilson(grpFalseNg, grpVerify)} | Defects(denom=OK={grpOk}): Homoglyph={grpDefects.Format(DefectKind.Homoglyph)}, MidDel={grpDefects.Format(DefectKind.MiddleDeletion)}, Swap={grpDefects.Format(DefectKind.AdjacentSwap)}, MissStroke={grpDefects.Format(DefectKind.MissingStroke)}, Bleed={grpDefects.Format(DefectKind.InkBleed)}, LowContrast={grpDefects.Format(DefectKind.SevereLowContrast)}, MidTextErr={grpDefects.Format(DefectKind.MiddleTextError)}"));
        }

        double overallNormalOkRate = (double)totalNormalOk / Math.Max(1, totalVerifySamples);

        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"=== REAL INDEPENDENT TOTAL: Verify={totalVerifySamples}, NormalOK={FormatRateWithWilson(totalNormalOk, totalVerifySamples)}, Review={FormatRateWithWilson(totalNormalReview, totalVerifySamples)}, FalseNG={FormatRateWithWilson(totalNormalFalseNg, totalVerifySamples)} ==="));
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"=== REAL INDEPENDENT DEFECT DETECTION (Denominator = Normal OK samples ONLY): Homoglyph={globalDefectCounts.Format(DefectKind.Homoglyph)}, MidDel={globalDefectCounts.Format(DefectKind.MiddleDeletion)}, Swap={globalDefectCounts.Format(DefectKind.AdjacentSwap)}, MissStroke={globalDefectCounts.Format(DefectKind.MissingStroke)}, Bleed={globalDefectCounts.Format(DefectKind.InkBleed)}, LowContrast={globalDefectCounts.Format(DefectKind.SevereLowContrast)}, MidTextErr={globalDefectCounts.Format(DefectKind.MiddleTextError)} ==="));

        output.WriteLine(sb.ToString());

        Assert.True(totalVerifySamples >= 7, $"Expected at least 7 real independent verify samples, got {totalVerifySamples}.");
        Assert.True(
            overallNormalOkRate >= TargetNormalOkRate,
            $"Real-independent Normal OK rate {overallNormalOkRate:P1} ({totalNormalOk}/{totalVerifySamples}) fell below target {TargetNormalOkRate:P1}.");

        foreach (DefectKind kind in Enum.GetValues<DefectKind>())
        {
            (int detected, int tested) = globalDefectCounts.Get(kind);
            Assert.True(tested > 0, $"Defect kind {kind} had 0 eligible Normal-OK samples tested.");
            double rate = (double)detected / tested;
            Assert.True(
                rate >= TargetDefectDetectionRate,
                $"Real-independent defect detection rate for {kind} was {rate:P1} ({detected}/{tested}), below target {TargetDefectDetectionRate:P1}.");
        }
    }

    [Fact]
    [Trait("Category", "KnownGap")]
    public void TaughtFontOcv_PerPrintGroup_PerturbedReal_EvaluatedSeparately()
    {
        (OcrBenchmarkDataset dataset, string repoRootPath, _) = OcrBenchmarkDataset.LoadFromDefaultLocation();
        var entryById = dataset.Entries.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var grayCache = new Dictionary<string, (byte[] Pixels, int Width, int Height)>(StringComparer.OrdinalIgnoreCase);
        using var workspace = new VisionWorkspace();

        var sb = new StringBuilder();
        sb.AppendLine("=== PER-PRINT-GROUP PERTURBED-REAL LEAVE-ONE-OUT OCV REPORT (EXCLUDES TARGET LINE X FROM GOLDEN FONT) ===");

        int totalPerturbedVerify = 0;
        int totalNormalOk = 0;
        int totalNormalReview = 0;
        int totalNormalFalseNg = 0;
        int totalNotRunLines = 0;
        var globalDefects = new DefectTallies();

        var selfCheckSb = new StringBuilder();
        selfCheckSb.AppendLine("=== 별도 참고표: 변형 강건성(자기 검증 — OCV 성능 아님, 티칭 줄 포함) ===");
        int selfVerifyTotal = 0;
        int selfOkTotal = 0;
        int selfReviewTotal = 0;
        int selfFalseNgTotal = 0;
        var selfGlobalDefects = new DefectTallies();

        foreach (OcrPrintGroupSpec group in dataset.PrintGroups)
        {
            if (!string.IsNullOrWhiteSpace(group.PerturbedNotRunReason) || group.PerturbedCandidateEntryIds.Count == 0)
            {
                sb.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"[PERTURBED-LOO][{group.GroupId}] = NOT_RUN | Reason: {group.PerturbedNotRunReason ?? "후보 단일 줄 없음"}"));
                continue;
            }

            // Collect dedicated OcvSpecimens (if any) and candidate entries separately so Leave-One-Out excludes candidate X cleanly
            var specimenSources = new List<(string Id, string ImageFile, double Left, double Top, double Right, double Bottom, IReadOnlyList<GoldenCharSample> Golden, IReadOnlyList<GoldenCharSample> RawPatches)>();
            if (group.OcvSpecimens is not null)
            {
                foreach (OcrOcvSpecimenEntry spec in group.OcvSpecimens)
                {
                    (byte[] px, int sw, int sh) = CropRectPixels(
                        spec.ImageFile,
                        spec.CenterX,
                        spec.CenterY,
                        spec.Width,
                        spec.Height,
                        repoRootPath,
                        grayCache);
                    OcvVerificationOptions sOpts = BuildOptionsForStrip(group, sw, sh, spec.GroundTruth);
                    var sView = new ReadOnlyImageView2D<byte>(px, sw, sh, sw);
                    var sFix = new FixturePose2D(sw * 0.5f, sh * 0.5f, 0f);
                    IReadOnlyList<GoldenCharSample> extracted = TaughtFontOcvVerifier.ExtractTeachingSamplesFromLine(
                        sView,
                        in sFix,
                        spec.GroundTruth,
                        sOpts,
                        workspace);
                    if (extracted.Count > 0)
                    {
                        IReadOnlyList<GoldenCharSample> rawPatches = ExtractRawPatchSamplesFromLine(px, sw, sh, spec.GroundTruth, sOpts, workspace);
                        specimenSources.Add((
                            spec.Id,
                            spec.ImageFile,
                            spec.CenterX - (spec.Width * 0.5),
                            spec.CenterY - (spec.Height * 0.5),
                            spec.CenterX + (spec.Width * 0.5),
                            spec.CenterY + (spec.Height * 0.5),
                            extracted,
                            rawPatches));
                    }
                }
            }

            var candidateEntries = new List<(OcrBenchmarkEntry Entry, byte[] Pixels, int W, int H, OcvVerificationOptions Opts, IReadOnlyList<GoldenCharSample> Golden, IReadOnlyList<GoldenCharSample> RawPatches)>();
            foreach (string entryId in group.PerturbedCandidateEntryIds)
            {
                OcrBenchmarkEntry entry = entryById[entryId];
                if (entry.GroundTruthLines.Count != 1)
                {
                    continue;
                }

                string gt = entry.GroundTruthLines[0];
                (byte[] px, int w, int h) = CropRoiPixels(entry, repoRootPath, grayCache);
                OcvVerificationOptions opts = BuildOptionsForStrip(group, w, h, gt);
                var view = new ReadOnlyImageView2D<byte>(px, w, h, w);
                var fixture = new FixturePose2D(w * 0.5f, h * 0.5f, 0f);
                IReadOnlyList<GoldenCharSample> extracted = TaughtFontOcvVerifier.ExtractTeachingSamplesFromLine(
                    view,
                    in fixture,
                    gt,
                    opts,
                    workspace);
                IReadOnlyList<GoldenCharSample> rawPatches = extracted.Count > 0
                    ? ExtractRawPatchSamplesFromLine(px, w, h, gt, opts, workspace)
                    : [];
                candidateEntries.Add((entry, px, w, h, opts, extracted, rawPatches));
            }

            var teachableCandidates = candidateEntries.Where(c => c.Golden.Count > 0).ToList();
            if (teachableCandidates.Count == 0 && specimenSources.Count == 0)
            {
                sb.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"[PERTURBED-LOO][{group.GroupId}] = NOT_RUN | Reason: 1D 열 투영 기준 글자 수 일치 티칭 샘플 추출 불가(인접 글자 접촉 또는 열화)"));
                continue;
            }

            // 1) Separate table ONLY: 변형 강건성(자기 검증 — OCV 성능 아님)
            if (teachableCandidates.Count > 0)
            {
                List<GoldenCharSample> selfGolden = teachableCandidates.SelectMany(c => c.Golden).ToList();
                List<GoldenCharSample> selfRaw = teachableCandidates.SelectMany(c => c.RawPatches).ToList();
                IndustrialOcrFontModel selfFont = IndustrialOcrTrainer.Train(selfGolden.ToArray());
                var selfVerifier = new TaughtFontOcvVerifier(selfFont);
                Dictionary<char, List<GoldenCharSample>> selfPatchPool = BuildRealPatchPool(selfRaw);

                int sGrpVerify = 0;
                int sGrpOk = 0;
                int sGrpReview = 0;
                int sGrpFalseNg = 0;
                var sGrpDefects = new DefectTallies();
                var dummyLog = new StringBuilder();

                foreach (var (entry, basePx, w, h, opts, _, _) in teachableCandidates)
                {
                    string gt = entry.GroundTruthLines[0];
                    byte bgVal = EstimateBackgroundValue(basePx, group.DarkOnLight);
                    foreach (var (tag, pertPx) in CreateStandardPerturbations(entry.Id, basePx, w, h, bgVal))
                    {
                        EvaluateSingleVerifySample(
                            sampleLabel: $"{entry.Id}:{tag}",
                            roiPixels: pertPx,
                            w: w,
                            h: h,
                            gt: gt,
                            options: opts,
                            verifier: selfVerifier,
                            patchPool: selfPatchPool,
                            workspace: workspace,
                            log: dummyLog,
                            verifyTotal: ref sGrpVerify,
                            normalOk: ref sGrpOk,
                            normalReview: ref sGrpReview,
                            normalFalseNg: ref sGrpFalseNg,
                            defects: sGrpDefects);
                    }
                }

                selfVerifyTotal += sGrpVerify;
                selfOkTotal += sGrpOk;
                selfReviewTotal += sGrpReview;
                selfFalseNgTotal += sGrpFalseNg;
                selfGlobalDefects.AddFrom(sGrpDefects);
                selfCheckSb.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"[자기 검증(OCV 아님)][{group.GroupId}] Lines={teachableCandidates.Count} | Perturbed={sGrpVerify} | Normal OK={FormatRateWithWilson(sGrpOk, sGrpVerify)}, Review={FormatRateWithWilson(sGrpReview, sGrpVerify)}, FalseNG={FormatRateWithWilson(sGrpFalseNg, sGrpVerify)} | Defects(denom=OK={sGrpOk}): Homoglyph={sGrpDefects.Format(DefectKind.Homoglyph)}, MidDel={sGrpDefects.Format(DefectKind.MiddleDeletion)}, Swap={sGrpDefects.Format(DefectKind.AdjacentSwap)}, MissStroke={sGrpDefects.Format(DefectKind.MissingStroke)}, Bleed={sGrpDefects.Format(DefectKind.InkBleed)}, LowContrast={sGrpDefects.Format(DefectKind.SevereLowContrast)}, MidTextErr={sGrpDefects.Format(DefectKind.MiddleTextError)}"));
            }

            // 2) Honest Leave-One-Out OCV evaluation: exclude target line X's glyphs from golden font!
            int grpVerify = 0;
            int grpOk = 0;
            int grpReview = 0;
            int grpFalseNg = 0;
            int grpEvaluatedLines = 0;
            int grpNotRunLines = 0;
            var grpDefects = new DefectTallies();

            foreach (var (targetEntry, basePx, w, h, opts, _, _) in candidateEntries)
            {
                double xLeft = targetEntry.CenterX - (targetEntry.Width * 0.5);
                double xTop = targetEntry.CenterY - (targetEntry.Height * 0.5);
                double xRight = targetEntry.CenterX + (targetEntry.Width * 0.5);
                double xBottom = targetEntry.CenterY + (targetEntry.Height * 0.5);

                List<GoldenCharSample> looGolden = [];
                List<GoldenCharSample> looRaw = [];

                foreach (var spec in specimenSources)
                {
                    if (string.Equals(spec.ImageFile, targetEntry.ImageFile, StringComparison.OrdinalIgnoreCase))
                    {
                        bool overlaps = xLeft < spec.Right && xRight > spec.Left && xTop < spec.Bottom && xBottom > spec.Top;
                        if (overlaps)
                        {
                            continue;
                        }
                    }

                    looGolden.AddRange(spec.Golden);
                    looRaw.AddRange(spec.RawPatches);
                }

                foreach (var other in teachableCandidates)
                {
                    if (string.Equals(other.Entry.Id, targetEntry.Id, StringComparison.Ordinal))
                    {
                        continue; // Leave-One-Out: strictly exclude target line X!
                    }

                    if (string.Equals(other.Entry.ImageFile, targetEntry.ImageFile, StringComparison.OrdinalIgnoreCase))
                    {
                        double oLeft = other.Entry.CenterX - (other.Entry.Width * 0.5);
                        double oTop = other.Entry.CenterY - (other.Entry.Height * 0.5);
                        double oRight = other.Entry.CenterX + (other.Entry.Width * 0.5);
                        double oBottom = other.Entry.CenterY + (other.Entry.Height * 0.5);
                        bool overlaps = xLeft < oRight && xRight > oLeft && xTop < oBottom && xBottom > oTop;
                        if (overlaps)
                        {
                            continue;
                        }
                    }

                    looGolden.AddRange(other.Golden);
                    looRaw.AddRange(other.RawPatches);
                }

                string gt = targetEntry.GroundTruthLines[0];
                HashSet<char> taughtChars = looGolden.Select(g => g.Character).ToHashSet();
                List<char> missingChars = gt
                    .Where(c => !char.IsWhiteSpace(c) && !taughtChars.Contains(c))
                    .Distinct()
                    .ToList();

                if (looGolden.Count == 0 || missingChars.Count > 0)
                {
                    grpNotRunLines++;
                    totalNotRunLines++;
                    string missingStr = missingChars.Count > 0 ? string.Join(",", missingChars) : "전부";
                    sb.AppendLine(string.Create(
                        CultureInfo.InvariantCulture,
                        $"  [PERTURBED-LOO][{group.GroupId}][{targetEntry.Id}] = NOT_RUN | GT=\"{gt}\" | Reason: Leave-one-out 제외 후 티칭 샘플 없는 문자=[{missingStr}]"));
                    continue;
                }

                grpEvaluatedLines++;
                IndustrialOcrFontModel looFont = IndustrialOcrTrainer.Train(looGolden.ToArray());
                var looVerifier = new TaughtFontOcvVerifier(looFont);
                Dictionary<char, List<GoldenCharSample>> looPatchPool = BuildRealPatchPool(looRaw);
                byte bgVal = EstimateBackgroundValue(basePx, group.DarkOnLight);

                foreach (var (tag, pertPx) in CreateStandardPerturbations(targetEntry.Id, basePx, w, h, bgVal))
                {
                    EvaluateSingleVerifySample(
                        sampleLabel: $"{targetEntry.Id}:{tag}",
                        roiPixels: pertPx,
                        w: w,
                        h: h,
                        gt: gt,
                        options: opts,
                        verifier: looVerifier,
                        patchPool: looPatchPool,
                        workspace: workspace,
                        log: sb,
                        verifyTotal: ref grpVerify,
                        normalOk: ref grpOk,
                        normalReview: ref grpReview,
                        normalFalseNg: ref grpFalseNg,
                        defects: grpDefects);
                }
            }

            if (grpVerify == 0)
            {
                sb.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"[PERTURBED-LOO][{group.GroupId}] = NOT_RUN (NotRunLines={grpNotRunLines}) | Reason: Leave-one-out 제외 후 모든 후보 줄에 미티칭 문자가 존재함"));
                continue;
            }

            totalPerturbedVerify += grpVerify;
            totalNormalOk += grpOk;
            totalNormalReview += grpReview;
            totalNormalFalseNg += grpFalseNg;
            globalDefects.AddFrom(grpDefects);

            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"[PERTURBED-LOO][{group.GroupId}] EvalLines={grpEvaluatedLines} (NotRunLines={grpNotRunLines}) | PerturbedVerify={grpVerify} | Normal OK={FormatRateWithWilson(grpOk, grpVerify)}, Review={FormatRateWithWilson(grpReview, grpVerify)}, FalseNG={FormatRateWithWilson(grpFalseNg, grpVerify)} | Defects(denom=OK={grpOk}): Homoglyph={grpDefects.Format(DefectKind.Homoglyph)}, MidDel={grpDefects.Format(DefectKind.MiddleDeletion)}, Swap={grpDefects.Format(DefectKind.AdjacentSwap)}, MissStroke={grpDefects.Format(DefectKind.MissingStroke)}, Bleed={grpDefects.Format(DefectKind.InkBleed)}, LowContrast={grpDefects.Format(DefectKind.SevereLowContrast)}, MidTextErr={grpDefects.Format(DefectKind.MiddleTextError)}"));
        }

        double pertOkRate = (double)totalNormalOk / Math.Max(1, totalPerturbedVerify);
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"=== PERTURBED-REAL LEAVE-ONE-OUT TOTAL: Verify={totalPerturbedVerify} (NotRunLines={totalNotRunLines}), NormalOK={FormatRateWithWilson(totalNormalOk, totalPerturbedVerify)}, Review={FormatRateWithWilson(totalNormalReview, totalPerturbedVerify)}, FalseNG={FormatRateWithWilson(totalNormalFalseNg, totalPerturbedVerify)} ==="));
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"=== PERTURBED-REAL LEAVE-ONE-OUT DEFECTS (Denominator = Normal OK ONLY): Homoglyph={globalDefects.Format(DefectKind.Homoglyph)}, MidDel={globalDefects.Format(DefectKind.MiddleDeletion)}, Swap={globalDefects.Format(DefectKind.AdjacentSwap)}, MissStroke={globalDefects.Format(DefectKind.MissingStroke)}, Bleed={globalDefects.Format(DefectKind.InkBleed)}, LowContrast={globalDefects.Format(DefectKind.SevereLowContrast)}, MidTextErr={globalDefects.Format(DefectKind.MiddleTextError)} ==="));

        selfCheckSb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"=== 변형 강건성(자기 검증 — OCV 성능 아님) TOTAL: Perturbed={selfVerifyTotal}, NormalOK={FormatRateWithWilson(selfOkTotal, selfVerifyTotal)}, Review={FormatRateWithWilson(selfReviewTotal, selfVerifyTotal)}, FalseNG={FormatRateWithWilson(selfFalseNgTotal, selfVerifyTotal)} | Defects(denom=OK={selfOkTotal}): Homoglyph={selfGlobalDefects.Format(DefectKind.Homoglyph)}, MidDel={selfGlobalDefects.Format(DefectKind.MiddleDeletion)}, Swap={selfGlobalDefects.Format(DefectKind.AdjacentSwap)}, MissStroke={selfGlobalDefects.Format(DefectKind.MissingStroke)}, Bleed={selfGlobalDefects.Format(DefectKind.InkBleed)}, LowContrast={selfGlobalDefects.Format(DefectKind.SevereLowContrast)}, MidTextErr={selfGlobalDefects.Format(DefectKind.MiddleTextError)} ==="));
        sb.AppendLine();
        sb.Append(selfCheckSb);

        output.WriteLine(sb.ToString());

        Assert.True(totalPerturbedVerify > 0, "Expected at least 1 leave-one-out perturbed verify sample.");
        Assert.True(
            pertOkRate >= TargetNormalOkRate,
            $"Perturbed-real (leave-one-out) Normal OK rate {pertOkRate:P1} ({totalNormalOk}/{totalPerturbedVerify}) fell below target {TargetNormalOkRate:P1}.");

        foreach (DefectKind kind in Enum.GetValues<DefectKind>())
        {
            (int detected, int tested) = globalDefects.Get(kind);
            Assert.True(tested > 0, $"Perturbed-real (leave-one-out) defect kind {kind} had 0 eligible Normal-OK samples.");
            double rate = (double)detected / tested;
            Assert.True(
                rate >= TargetDefectDetectionRate,
                $"Perturbed-real (leave-one-out) defect detection rate for {kind} was {rate:P1} ({detected}/{tested}), below target {TargetDefectDetectionRate:P1}.");
        }
    }

    [Fact]
    public void OcvVerificationOptions_InvalidInvariants_ThrowOnConstruction()
    {
        Assert.Throws<ArgumentException>(() => new OcvVerificationOptions(PassMatchScore: 0.50f, ReviewMatchScore: 0.70f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OcvVerificationOptions(PassMatchScore: 1.05f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OcvVerificationOptions(ReviewMatchScore: -0.01f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OcvVerificationOptions(ReviewContrastGv: 15f, MinContrastGv: 25f));
        Assert.Throws<ArgumentException>(() => new OcvVerificationOptions(MaxMissingStrokeRatio: 0.15f, ReviewMissingStrokeRatio: 0.25f));
        Assert.Throws<ArgumentException>(() => new OcvVerificationOptions(MaxBleedRatio: 0.10f, ReviewBleedRatio: 0.20f));
        Assert.Throws<ArgumentException>(() => new OcvVerificationOptions(MinCharWidth: 20, MaxCharWidth: 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OcvVerificationOptions(MaxLineCharacters: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OcvVerificationOptions(MaxLineCharacters: 300));
    }

    [Fact]
    public void OcrFieldFormatSpec_EscapesAndCharacterClasses_ResolveSymbolCollision()
    {
        // 9 slots: \A(0), \D(1), -(2), A(3), D(4), -(5), [0-9A-F](6), \\(7), \[(8)
        var spec = new OcrFieldFormatSpec(FormatMask: @"\A\D-AD-[0-9A-F]\\\[", StrictLengthMatch: true);
        Assert.Equal(9, spec.SlotCount);

        // Slot 0: literal 'A' only (rejects 'B')
        Assert.True(spec.AllowsCharacterAt(0, 'A'));
        Assert.False(spec.AllowsCharacterAt(0, 'B'));

        // Slot 1: literal 'D' only (rejects '5' and 'E')
        Assert.True(spec.AllowsCharacterAt(1, 'D'));
        Assert.False(spec.AllowsCharacterAt(1, '5'));

        // Slot 2: literal '-'
        Assert.True(spec.AllowsCharacterAt(2, '-'));

        // Slot 3: wildcard 'A' (any uppercase letter)
        Assert.True(spec.AllowsCharacterAt(3, 'Z'));
        Assert.False(spec.AllowsCharacterAt(3, '9'));

        // Slot 4: wildcard 'D' (any digit)
        Assert.True(spec.AllowsCharacterAt(4, '7'));
        Assert.False(spec.AllowsCharacterAt(4, 'D'));

        // Slot 6: character class [0-9A-F]
        Assert.True(spec.AllowsCharacterAt(6, '9'));
        Assert.True(spec.AllowsCharacterAt(6, 'F'));
        Assert.False(spec.AllowsCharacterAt(6, 'G'));

        // Slot 7 & 8: escaped literal '\' and '['
        Assert.True(spec.AllowsCharacterAt(7, '\\'));
        Assert.True(spec.AllowsCharacterAt(8, '['));
        Assert.False(spec.AllowsCharacterAt(9, '0')); // StrictLengthMatch beyond SlotCount

        // Syntax error cases
        Assert.Throws<FormatException>(() => new OcrFieldFormatSpec(FormatMask: @"DDD\"));
        Assert.Throws<FormatException>(() => new OcrFieldFormatSpec(FormatMask: "[0-9"));
        Assert.Throws<FormatException>(() => new OcrFieldFormatSpec(FormatMask: "[]"));
        Assert.Throws<FormatException>(() => new OcrFieldFormatSpec(FormatMask: "[Z-A]"));
    }

    [Fact]
    public void VerifyLine_WhenSegmentedBoxesExceedMaxLineCharacters_ReturnsExplicitOverflowNg()
    {
        using var workspace = new VisionWorkspace();
        // Build a simple clean strip with 10 distinct vertical bar characters ('I')
        int w = 220;
        int h = 36;
        byte[] strip = new byte[w * h];
        Array.Fill(strip, (byte)245);
        for (int k = 0; k < 10; k++)
        {
            int x0 = 12 + (k * 20);
            for (int y = 6; y <= 29; y++)
            {
                for (int x = x0; x < x0 + 6; x++)
                {
                    strip[(y * w) + x] = 20;
                }
            }
        }

        var view = new ReadOnlyImageView2D<byte>(strip, w, h, w);
        var fixture = new FixturePose2D(w * 0.5f, h * 0.5f, 0f);
        var teachOpts = new OcvVerificationOptions(MinCharWidth: 3, MaxCharWidth: 24, ExpectedPitch: 20, MaxLineCharacters: 16);
        IReadOnlyList<GoldenCharSample> samples = TaughtFontOcvVerifier.ExtractTeachingSamplesFromLine(
            view,
            in fixture,
            "IIIIIIIIII",
            teachOpts,
            workspace);
        Assert.Equal(10, samples.Count);

        IndustrialOcrFontModel font = IndustrialOcrTrainer.Train(samples.ToArray());
        var verifier = new TaughtFontOcvVerifier(font);

        // Restrict MaxLineCharacters to 6 (< 10) -> must NOT silently truncate; must return explicit NG with overflow cause
        var cappedOpts = teachOpts with { MaxLineCharacters = 6 };
        OcvVerificationResult res = verifier.VerifyLine(
            view,
            in fixture,
            new OcrFieldFormatSpec(ExpectedText: "IIIIII", StrictLengthMatch: true),
            cappedOpts,
            workspace);

        Assert.Equal(OcvVerdictKind.Ng, res.Verdict);
        Assert.Contains("최대 허용 문자 수 초과", res.CauseSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyLine_SequenceAlignment_IsolatesMiddleDeletionAndOverSegmentationWithoutCascade()
    {
        (OcrBenchmarkDataset dataset, string repoRootPath, _) = OcrBenchmarkDataset.LoadFromDefaultLocation();
        var grayCache = new Dictionary<string, (byte[] Pixels, int Width, int Height)>(StringComparer.OrdinalIgnoreCase);
        using var workspace = new VisionWorkspace();

        OcrPrintGroupSpec group = dataset.PrintGroups.Single(g => g.GroupId == "pg_dotocr_5x7_crisp");
        List<GoldenCharSample> goldenSamples = [];
        foreach (OcrOcvSpecimenEntry spec in group.OcvSpecimens!)
        {
            (byte[] px, int sw, int sh) = CropRectPixels(
                spec.ImageFile,
                spec.CenterX,
                spec.CenterY,
                spec.Width,
                spec.Height,
                repoRootPath,
                grayCache);
            var sView = new ReadOnlyImageView2D<byte>(px, sw, sh, sw);
            var sFix = new FixturePose2D(sw * 0.5f, sh * 0.5f, 0f);
            OcvVerificationOptions sOpts = BuildOptionsForStrip(group, sw, sh, spec.GroundTruth);
            goldenSamples.AddRange(TaughtFontOcvVerifier.ExtractTeachingSamplesFromLine(sView, in sFix, spec.GroundTruth, sOpts, workspace));
        }

        IndustrialOcrFontModel font = IndustrialOcrTrainer.Train(goldenSamples.ToArray());
        var verifier = new TaughtFontOcvVerifier(font);

        OcrBenchmarkEntry mfgEntry = dataset.Entries.Single(e => e.Id == "DotOcr_Mfg");
        string gt = mfgEntry.GroundTruthLines[0]; // "MFG:200523"
        (byte[] basePixels, int w, int h) = CropRoiPixels(mfgEntry, repoRootPath, grayCache);
        OcvVerificationOptions opts = BuildOptionsForStrip(group, w, h, gt);
        var fixture = new FixturePose2D(w * 0.5f, h * 0.5f, 0f);
        var specField = new OcrFieldFormatSpec(FormatMask: BuildEscapedFormatMaskFromGroundTruth(gt), ExpectedText: gt, StrictLengthMatch: true);

        // Segment baseline once to find exact box coordinates of middle character #4 ('2' at index 4)
        Span<byte> rectStrip = new byte[w * h];
        Span<SegmentedCharBox> boxes = stackalloc SegmentedCharBox[32];
        int count = OcrMaxSegmenter.SegmentLine(
            new ReadOnlyImageView2D<byte>(basePixels, w, h, w),
            in fixture,
            new OcrMaxSegmentParams(
                PrintMethodType.InkjetDotMatrix,
                w,
                h,
                opts.MinCharWidth,
                opts.MaxCharWidth,
                opts.ExpectedPitch,
                opts.DotClosingRadiusX,
                opts.DotClosingRadiusY,
                opts.ForegroundContrastOffset,
                opts.DarkOnLight),
            workspace,
            rectStrip,
            boxes);
        Assert.Equal(10, count);

        // 1. Middle character deletion: erase character at index 4 ('2') completely
        byte[] deletedImg = (byte[])rectStrip.ToArray().Clone();
        SegmentedCharBox targetBox = boxes[4];
        for (int r = targetBox.TopRow; r <= targetBox.BottomRow; r++)
        {
            for (int c = targetBox.StartCol; c <= targetBox.EndCol; c++)
            {
                deletedImg[(r * w) + c] = 252;
            }
        }

        OcvVerificationResult delRes = verifier.VerifyLine(
            new ReadOnlyImageView2D<byte>(deletedImg, w, h, w),
            in fixture,
            specField,
            opts,
            workspace);

        Assert.Equal(OcvVerdictKind.Ng, delRes.Verdict);
        Assert.Contains(delRes.Characters, c => c.Cause.Contains("삭제(누락)", StringComparison.Ordinal));
        // Subsequent characters ('0','0','5','2','3' at indices 5..9) MUST remain Ok without cascading failure!
        for (int idx = 5; idx < delRes.Characters.Count; idx++)
        {
            Assert.Equal(OcvVerdictKind.Ok, delRes.Characters[idx].Verdict);
        }

        // 2. Over-segmentation: carve a wide vertical white gap down the middle of character index 4 so it splits into 2 boxes (11 total)
        byte[] splitImg = (byte[])rectStrip.ToArray().Clone();
        int midCol = (targetBox.StartCol + targetBox.EndCol) / 2;
        for (int r = targetBox.TopRow; r <= targetBox.BottomRow; r++)
        {
            for (int c = midCol - 2; c <= midCol + 2; c++)
            {
                splitImg[(r * w) + c] = 252;
            }
        }

        OcvVerificationResult splitRes = verifier.VerifyLine(
            new ReadOnlyImageView2D<byte>(splitImg, w, h, w),
            in fixture,
            specField,
            opts with { MinCharWidth = 3 },
            workspace);

        Assert.Equal(OcvVerdictKind.Ng, splitRes.Verdict);
        Assert.Contains(splitRes.Characters, c => c.Cause.Contains("삽입(과분할)", StringComparison.Ordinal));
        // Trailing 5 intact characters ('0','0','5','2','3') MUST remain Ok without cascading failure!
        int trailingOk = splitRes.Characters.Skip(splitRes.Characters.Count - 5).Count(c => c.Verdict == OcvVerdictKind.Ok);
        Assert.Equal(5, trailingOk);
    }

    private static void EvaluateSingleVerifySample(
        string sampleLabel,
        byte[] roiPixels,
        int w,
        int h,
        string gt,
        OcvVerificationOptions options,
        TaughtFontOcvVerifier verifier,
        Dictionary<char, List<GoldenCharSample>> patchPool,
        VisionWorkspace workspace,
        StringBuilder log,
        ref int verifyTotal,
        ref int normalOk,
        ref int normalReview,
        ref int normalFalseNg,
        DefectTallies defects)
    {
        verifyTotal++;
        var view = new ReadOnlyImageView2D<byte>(roiPixels, w, h, w);
        var fixture = new FixturePose2D(w * 0.5f, h * 0.5f, 0f);
        string escapedMask = BuildEscapedFormatMaskFromGroundTruth(gt);
        var normalSpec = new OcrFieldFormatSpec(FormatMask: escapedMask, ExpectedText: gt, StrictLengthMatch: true);

        OcvVerificationResult normalRes = verifier.VerifyLine(view, in fixture, normalSpec, options, workspace);
        switch (normalRes.Verdict)
        {
            case OcvVerdictKind.Ok:
                normalOk++;
                break;
            case OcvVerdictKind.Review:
                normalReview++;
                break;
            default:
                normalFalseNg++;
                break;
        }

        log.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  [NORMAL][{sampleLabel}] Verdict={normalRes.Verdict} Mean={normalRes.MeanMatchScore:F3} Min={normalRes.MinMatchScore:F3} Contrast={normalRes.MinContrastGv:F1} | GT=\"{gt}\" | DEC=\"{normalRes.DecodedText}\" | Cause={normalRes.CauseSummary}"));

        // Rule 7: Defect detection denominator MUST include ONLY Normal-OK samples!
        if (normalRes.Verdict != OcvVerdictKind.Ok)
        {
            return;
        }

        // Segment strip once to get exact character boxes for real-patch defects
        Span<byte> rectStrip = new byte[w * h];
        Span<SegmentedCharBox> boxes = stackalloc SegmentedCharBox[options.MaxLineCharacters];
        int boxCount = OcrMaxSegmenter.SegmentLine(
            view,
            in fixture,
            new OcrMaxSegmentParams(
                options.PrintMethod,
                w,
                h,
                options.MinCharWidth,
                options.MaxCharWidth,
                options.ExpectedPitch,
                options.DotClosingRadiusX,
                options.DotClosingRadiusY,
                options.ForegroundContrastOffset,
                options.DarkOnLight),
            workspace,
            rectStrip,
            boxes);

        if (boxCount < 2)
        {
            return;
        }

        // Use raw unbridged ROI pixels so VerifyLine applies dot-matrix morphological bridging only once
        byte[] baseStrip = (byte[])roiPixels.Clone();
        string compactGt = normalSpec.CompactExpectedText;
        byte bgVal = EstimateBackgroundValue(baseStrip, options.DarkOnLight);
        byte fgVal = EstimateForegroundValue(baseStrip, options.DarkOnLight);

        // (a) Homoglyph real-patch substitution (8↔B, 0↔O, 1↔I/l, 5↔S, 2↔Z)
        if (TryCreateHomoglyphSubstitutedStrip(baseStrip, w, h, boxes[..boxCount], compactGt, patchPool, bgVal, out byte[]? homoglyphStrip))
        {
            // Verify with ExpectedText only (unconstrained mask so 8↔B or 0↔O cross-class homoglyph can compete freely!)
            var homoSpec = new OcrFieldFormatSpec(FormatMask: null, ExpectedText: gt, StrictLengthMatch: true);
            OcvVerificationResult homoRes = verifier.VerifyLine(
                new ReadOnlyImageView2D<byte>(homoglyphStrip!, w, h, w),
                in fixture,
                homoSpec,
                options,
                workspace);
            defects.Record(DefectKind.Homoglyph, detected: homoRes.Verdict != OcvVerdictKind.Ok);
        }

        // (b) Middle character deletion
        int midIdx = Math.Clamp(boxCount / 2, 1, boxCount - 1);
        if (boxCount >= 3)
        {
            midIdx = Math.Clamp(boxCount / 2, 1, boxCount - 2);
        }

        byte[] deletedStrip = (byte[])baseStrip.Clone();
        EraseBoxToBackground(deletedStrip, w, boxes[midIdx], bgVal);
        OcvVerificationResult delRes = verifier.VerifyLine(
            new ReadOnlyImageView2D<byte>(deletedStrip, w, h, w),
            in fixture,
            normalSpec,
            options,
            workspace);
        defects.Record(DefectKind.MiddleDeletion, detected: delRes.IsNg);

        // (c) Adjacent character swap (two distinct adjacent middle characters)
        if (TryCreateAdjacentSwapStrip(baseStrip, w, h, boxes[..boxCount], compactGt, bgVal, out byte[]? swappedStrip))
        {
            var swapSpec = new OcrFieldFormatSpec(FormatMask: null, ExpectedText: gt, StrictLengthMatch: true);
            OcvVerificationResult swapRes = verifier.VerifyLine(
                new ReadOnlyImageView2D<byte>(swappedStrip!, w, h, w),
                in fixture,
                swapSpec,
                options,
                workspace);
            defects.Record(DefectKind.AdjacentSwap, detected: swapRes.IsNg);
        }

        // (d) Missing stroke (erase ~45% band of middle glyph)
        byte[] missingStrokeStrip = CreateMissingStrokeStrip(baseStrip, w, boxes[midIdx], bgVal);
        OcvVerificationResult missRes = verifier.VerifyLine(
            new ReadOnlyImageView2D<byte>(missingStrokeStrip, w, h, w),
            in fixture,
            normalSpec,
            options,
            workspace);
        defects.Record(DefectKind.MissingStroke, detected: missRes.Verdict != OcvVerdictKind.Ok);

        // (e) Ink bleed (flood ink into background around middle glyph)
        byte[] bleedStrip = CreateInkBleedStrip(baseStrip, w, h, boxes[midIdx], fgVal);
        OcvVerificationResult bleedRes = verifier.VerifyLine(
            new ReadOnlyImageView2D<byte>(bleedStrip, w, h, w),
            in fixture,
            normalSpec,
            options,
            workspace);
        defects.Record(DefectKind.InkBleed, detected: bleedRes.Verdict != OcvVerdictKind.Ok);

        // (f) Severe low contrast (~12 GV peak-to-peak)
        byte[] lowContrastStrip = CreateCompressedContrastStrip(baseStrip, targetPeakToPeakGv: 12f);
        OcvVerificationResult lowRes = verifier.VerifyLine(
            new ReadOnlyImageView2D<byte>(lowContrastStrip, w, h, w),
            in fixture,
            normalSpec,
            options,
            workspace);
        defects.Record(DefectKind.SevereLowContrast, detected: lowRes.IsNg);

        // (g) Expected-text middle character error (NEVER mutate only the last character!)
        string mutatedMidExpected = MutateMiddleCharacter(gt);
        var midMismatchSpec = new OcrFieldFormatSpec(FormatMask: null, ExpectedText: mutatedMidExpected, StrictLengthMatch: true);
        OcvVerificationResult midTextRes = verifier.VerifyLine(view, in fixture, midMismatchSpec, options, workspace);
        string compactMutated = midMismatchSpec.CompactExpectedText;
        string compactDecoded = midTextRes.DecodedText.Replace(" ", string.Empty, StringComparison.Ordinal);
        bool detectedAndHonest = midTextRes.IsNg && !string.Equals(compactDecoded, compactMutated, StringComparison.Ordinal);
        defects.Record(DefectKind.MiddleTextError, detected: detectedAndHonest);
    }

    private static (string Tag, byte[] Pixels)[] CreateStandardPerturbations(
        string entryId,
        byte[] basePx,
        int w,
        int h,
        byte bgVal) =>
    [
        ("subpx+0.45", ApplySubpixelShift(basePx, w, h, dx: 0.45f, dy: -0.30f, bgVal)),
        ("rot+1.5deg", ApplySmallRotation(basePx, w, h, angleDeg: 1.5f, bgVal)),
        ("noise_s3.5", ApplyGaussianNoise(basePx, w, h, sigma: 3.5f, seed: entryId.GetHashCode(StringComparison.Ordinal))),
        ("bright-10%", ApplyBrightnessScale(basePx, scale: 0.90f)),
    ];

    public static (double Lower, double Upper) ComputeWilson95Interval(int k, int n)
    {
        if (n <= 0)
        {
            return (0.0, 0.0);
        }

        const double z = 1.959963984540054;
        const double z2 = z * z;
        double p = Math.Clamp((double)k / n, 0.0, 1.0);
        double denom = 1.0 + (z2 / n);
        double center = (p + (z2 / (2.0 * n))) / denom;
        double halfWidth = (z * Math.Sqrt(((p * (1.0 - p)) / n) + (z2 / (4.0 * n * n)))) / denom;
        return (Math.Max(0.0, center - halfWidth), Math.Min(1.0, center + halfWidth));
    }

    public static string FormatRateWithWilson(int k, int n)
    {
        if (n <= 0)
        {
            return "N/A (0/0) [표본 부족]";
        }

        (double lo, double hi) = ComputeWilson95Interval(k, n);
        double rate = (double)k / n;
        return n < 10
            ? string.Create(CultureInfo.InvariantCulture, $"{k}/{n} ({rate:P1}) [표본 부족 · Wilson95%={lo:P1}~{hi:P1}]")
            : string.Create(CultureInfo.InvariantCulture, $"{k}/{n} ({rate:P1}) [Wilson95%={lo:P1}~{hi:P1}]");
    }

    private static OcvVerificationOptions BuildOptionsForStrip(OcrPrintGroupSpec group, int w, int h, string gt)
    {
        PrintMethodType method = string.Equals(group.PrintMethod, "InkjetDotMatrix", StringComparison.OrdinalIgnoreCase)
            ? PrintMethodType.InkjetDotMatrix
            : PrintMethodType.ContinuousLaser;
        int nonSpaceLen = Math.Max(1, gt.Count(c => !char.IsWhiteSpace(c)));
        int estimatedPitch = Math.Clamp(w / nonSpaceLen, 5, 56);
        int minCharW = Math.Clamp(h / 12, 2, 4);
        int maxCharW = Math.Max(8, (int)MathF.Ceiling(Math.Max(h * 1.15f, estimatedPitch * 1.45f)));

        return new OcvVerificationOptions(
            PrintMethod: method,
            MinCharWidth: minCharW,
            MaxCharWidth: maxCharW,
            ExpectedPitch: estimatedPitch,
            DotClosingRadiusX: group.DotClosingRadiusX,
            DotClosingRadiusY: group.DotClosingRadiusY,
            DarkOnLight: group.DarkOnLight);
    }

    private static IReadOnlyList<GoldenCharSample> ExtractRawPatchSamplesFromLine(
        byte[] rawPixels,
        int w,
        int h,
        string groundTruthText,
        OcvVerificationOptions options,
        VisionWorkspace workspace)
    {
        string compact = new(groundTruthText.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (compact.Length == 0)
        {
            return [];
        }

        Span<byte> rectStrip = new byte[w * h];
        Span<SegmentedCharBox> boxes = stackalloc SegmentedCharBox[options.MaxLineCharacters];
        var view = new ReadOnlyImageView2D<byte>(rawPixels, w, h, w);
        var fixture = new FixturePose2D(w * 0.5f, h * 0.5f, 0f);
        int count = OcrMaxSegmenter.SegmentLine(
            view,
            in fixture,
            new OcrMaxSegmentParams(
                options.PrintMethod,
                w,
                h,
                options.MinCharWidth,
                options.MaxCharWidth,
                options.ExpectedPitch,
                options.DotClosingRadiusX,
                options.DotClosingRadiusY,
                options.ForegroundContrastOffset,
                options.DarkOnLight),
            workspace,
            rectStrip,
            boxes);
        if (count != compact.Length)
        {
            return [];
        }

        var list = new List<GoldenCharSample>(count);
        for (int i = 0; i < count; i++)
        {
            SegmentedCharBox b = boxes[i];
            byte[] patch = ExtractBoxPixels(rawPixels, w, b);
            list.Add(new GoldenCharSample(compact[i], patch, b.Width, b.Height, b.Width, options.DarkOnLight));
        }

        return list;
    }

    private static Dictionary<char, List<GoldenCharSample>> BuildRealPatchPool(IReadOnlyList<GoldenCharSample> samples)
    {
        var pool = new Dictionary<char, List<GoldenCharSample>>();
        foreach (GoldenCharSample s in samples)
        {
            if (!pool.TryGetValue(s.Character, out List<GoldenCharSample>? list))
            {
                list = [];
                pool[s.Character] = list;
            }

            list.Add(s);
        }

        return pool;
    }

    private static bool TryCreateHomoglyphSubstitutedStrip(
        byte[] baseStrip,
        int w,
        int h,
        ReadOnlySpan<SegmentedCharBox> boxes,
        string compactGt,
        Dictionary<char, List<GoldenCharSample>> patchPool,
        byte bgVal,
        out byte[]? resultStrip)
    {
        int count = Math.Min(boxes.Length, compactGt.Length);
        // Search from middle outward so we prefer interior characters
        int center = count / 2;
        for (int delta = 0; delta < count; delta++)
        {
            for (int sign = -1; sign <= 1; sign += 2)
            {
                int idx = center + (sign * delta);
                if (idx < 0 || idx >= count)
                {
                    continue;
                }

                char orig = compactGt[idx];
                foreach (var (from, to) in HomoglyphPairs)
                {
                    if (orig == from && patchPool.TryGetValue(to, out List<GoldenCharSample>? candidates) && candidates.Count > 0)
                    {
                        byte[] dst = (byte[])baseStrip.Clone();
                        SegmentedCharBox box = boxes[idx];
                        EraseBoxToBackground(dst, w, box, bgVal);
                        BlitSampleIntoBox(dst, w, h, box, candidates[0]);
                        resultStrip = dst;
                        return true;
                    }
                }
            }
        }

        resultStrip = null;
        return false;
    }

    private static bool TryCreateAdjacentSwapStrip(
        byte[] baseStrip,
        int w,
        int h,
        ReadOnlySpan<SegmentedCharBox> boxes,
        string compactGt,
        byte bgVal,
        out byte[]? swappedStrip)
    {
        int count = Math.Min(boxes.Length, compactGt.Length);
        int startSearch = Math.Max(0, (count / 2) - 1);
        for (int offset = 0; offset < count - 1; offset++)
        {
            int i = (startSearch + offset) % (count - 1);
            if (compactGt[i] == compactGt[i + 1])
            {
                continue;
            }

            SegmentedCharBox boxA = boxes[i];
            SegmentedCharBox boxB = boxes[i + 1];
            byte[] patchA = ExtractBoxPixels(baseStrip, w, boxA);
            byte[] patchB = ExtractBoxPixels(baseStrip, w, boxB);

            byte[] dst = (byte[])baseStrip.Clone();
            EraseBoxToBackground(dst, w, boxA, bgVal);
            EraseBoxToBackground(dst, w, boxB, bgVal);
            BlitRawPatchIntoBox(dst, w, h, boxA, patchB, boxB.Width, boxB.Height);
            BlitRawPatchIntoBox(dst, w, h, boxB, patchA, boxA.Width, boxA.Height);
            swappedStrip = dst;
            return true;
        }

        swappedStrip = null;
        return false;
    }

    private static byte[] ExtractBoxPixels(byte[] strip, int stride, SegmentedCharBox box)
    {
        byte[] buf = new byte[box.Width * box.Height];
        for (int r = 0; r < box.Height; r++)
        {
            Array.Copy(strip, ((box.TopRow + r) * stride) + box.StartCol, buf, r * box.Width, box.Width);
        }

        return buf;
    }

    private static void BlitSampleIntoBox(byte[] dst, int dstW, int dstH, SegmentedCharBox box, GoldenCharSample sample)
    {
        BlitRawPatchIntoBox(dst, dstW, dstH, box, sample.Pixels, sample.Width, sample.Height, sample.Stride);
    }

    private static void BlitRawPatchIntoBox(
        byte[] dst,
        int dstW,
        int dstH,
        SegmentedCharBox box,
        byte[] src,
        int srcW,
        int srcH,
        int srcStride = 0)
    {
        if (srcStride <= 0)
        {
            srcStride = srcW;
        }

        for (int r = 0; r < box.Height; r++)
        {
            int dy = box.TopRow + r;
            if ((uint)dy >= (uint)dstH)
            {
                continue;
            }

            float sy = ((r + 0.5f) * srcH / box.Height) - 0.5f;
            for (int c = 0; c < box.Width; c++)
            {
                int dx = box.StartCol + c;
                if ((uint)dx >= (uint)dstW)
                {
                    continue;
                }

                float sx = ((c + 0.5f) * srcW / box.Width) - 0.5f;
                dst[(dy * dstW) + dx] = SampleBilinear(src, srcW, srcH, srcStride, sx, sy);
            }
        }
    }

    private static void EraseBoxToBackground(byte[] strip, int w, SegmentedCharBox box, byte bgVal)
    {
        for (int r = box.TopRow; r <= box.BottomRow; r++)
        {
            for (int c = box.StartCol; c <= box.EndCol; c++)
            {
                strip[(r * w) + c] = bgVal;
            }
        }
    }

    private static byte[] CreateMissingStrokeStrip(byte[] src, int w, SegmentedCharBox box, byte bgVal)
    {
        byte[] dst = (byte[])src.Clone();
        int bandStart = box.TopRow + (box.Height / 5);
        int bandEnd = Math.Min(box.BottomRow, bandStart + Math.Max(3, (box.Height * 55) / 100));
        for (int r = bandStart; r <= bandEnd; r++)
        {
            for (int c = box.StartCol; c <= box.EndCol; c++)
            {
                dst[(r * w) + c] = bgVal;
            }
        }

        return dst;
    }

    private static byte[] CreateInkBleedStrip(byte[] src, int w, int h, SegmentedCharBox box, byte fgVal)
    {
        byte[] dst = (byte[])src.Clone();
        int y0 = Math.Max(0, box.TopRow);
        int y1 = Math.Min(h - 1, box.BottomRow);
        int x0 = Math.Max(0, box.StartCol - 2);
        int x1 = Math.Min(w - 1, box.EndCol + 2);
        for (int r = y0; r <= y1; r++)
        {
            for (int c = x0; c <= x1; c++)
            {
                dst[(r * w) + c] = fgVal;
            }
        }

        return dst;
    }

    private static byte[] CreateCompressedContrastStrip(byte[] src, float targetPeakToPeakGv)
    {
        byte min = byte.MaxValue;
        byte max = byte.MinValue;
        double sum = 0.0;
        for (int i = 0; i < src.Length; i++)
        {
            byte p = src[i];
            if (p < min) min = p;
            if (p > max) max = p;
            sum += p;
        }

        float range = Math.Max(1f, (float)(max - min));
        float scale = Math.Min(1f, targetPeakToPeakGv / range);
        float mean = (float)(sum / src.Length);

        byte[] dst = new byte[src.Length];
        for (int i = 0; i < src.Length; i++)
        {
            float v = mean + ((src[i] - mean) * scale);
            dst[i] = (byte)Math.Clamp((int)MathF.Round(v), 0, 255);
        }

        return dst;
    }

    private static byte[] ApplySubpixelShift(byte[] src, int w, int h, float dx, float dy, byte borderVal)
    {
        byte[] dst = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            float sy = y - dy;
            for (int x = 0; x < w; x++)
            {
                float sx = x - dx;
                dst[(y * w) + x] = SampleBilinearWithBorder(src, w, h, sx, sy, borderVal);
            }
        }

        return dst;
    }

    private static byte[] ApplySmallRotation(byte[] src, int w, int h, float angleDeg, byte borderVal)
    {
        byte[] dst = new byte[w * h];
        float rad = angleDeg * (MathF.PI / 180f);
        float cos = MathF.Cos(rad);
        float sin = MathF.Sin(rad);
        float cx = (w - 1) * 0.5f;
        float cy = (h - 1) * 0.5f;

        for (int y = 0; y < h; y++)
        {
            float ry = y - cy;
            for (int x = 0; x < w; x++)
            {
                float rx = x - cx;
                float sx = cx + (rx * cos) + (ry * sin);
                float sy = cy - (rx * sin) + (ry * cos);
                dst[(y * w) + x] = SampleBilinearWithBorder(src, w, h, sx, sy, borderVal);
            }
        }

        return dst;
    }

    private static byte[] ApplyGaussianNoise(byte[] src, int w, int h, float sigma, int seed)
    {
        byte[] dst = new byte[w * h];
        var rng = new Random(seed);
        for (int i = 0; i < src.Length; i++)
        {
            double u1 = 1.0 - rng.NextDouble();
            double u2 = 1.0 - rng.NextDouble();
            double z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            int v = (int)Math.Round(src[i] + (z * sigma));
            dst[i] = (byte)Math.Clamp(v, 0, 255);
        }

        return dst;
    }

    private static byte[] ApplyBrightnessScale(byte[] src, float scale)
    {
        byte[] dst = new byte[src.Length];
        for (int i = 0; i < src.Length; i++)
        {
            dst[i] = (byte)Math.Clamp((int)MathF.Round(src[i] * scale), 0, 255);
        }

        return dst;
    }

    private static byte SampleBilinear(byte[] src, int w, int h, int stride, float sx, float sy)
    {
        float cx = Math.Clamp(sx, 0f, Math.Max(0, w - 1));
        float cy = Math.Clamp(sy, 0f, Math.Max(0, h - 1));
        int x0 = (int)MathF.Floor(cx);
        int y0 = (int)MathF.Floor(cy);
        int x1 = Math.Min(w - 1, x0 + 1);
        int y1 = Math.Min(h - 1, y0 + 1);
        float fx = cx - x0;
        float fy = cy - y0;

        float v00 = src[(y0 * stride) + x0];
        float v10 = src[(y0 * stride) + x1];
        float v01 = src[(y1 * stride) + x0];
        float v11 = src[(y1 * stride) + x1];

        float top = v00 + ((v10 - v00) * fx);
        float bot = v01 + ((v11 - v01) * fx);
        return (byte)Math.Clamp((int)MathF.Round(top + ((bot - top) * fy)), 0, 255);
    }

    private static byte SampleBilinearWithBorder(byte[] src, int w, int h, float sx, float sy, byte borderVal)
    {
        if (sx < 0f || sy < 0f || sx > w - 1 || sy > h - 1)
        {
            return borderVal;
        }

        return SampleBilinear(src, w, h, w, sx, sy);
    }

    private static byte EstimateBackgroundValue(byte[] pixels, bool darkOnLight)
    {
        byte min = byte.MaxValue;
        byte max = byte.MinValue;
        for (int i = 0; i < pixels.Length; i++)
        {
            byte p = pixels[i];
            if (p < min) min = p;
            if (p > max) max = p;
        }

        return darkOnLight ? max : min;
    }

    private static byte EstimateForegroundValue(byte[] pixels, bool darkOnLight)
    {
        byte min = byte.MaxValue;
        byte max = byte.MinValue;
        for (int i = 0; i < pixels.Length; i++)
        {
            byte p = pixels[i];
            if (p < min) min = p;
            if (p > max) max = p;
        }

        return darkOnLight ? min : max;
    }

    private static string BuildEscapedFormatMaskFromGroundTruth(string text)
    {
        var sb = new StringBuilder(text.Length * 2);
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                continue;
            }

            if (c is >= '0' and <= '9')
            {
                sb.Append(OcrFieldFormatSpec.DigitMaskSymbol);
            }
            else if (c is >= 'A' and <= 'Z')
            {
                sb.Append(OcrFieldFormatSpec.UpperAlphaMaskSymbol);
            }
            else if (c is >= 'a' and <= 'z')
            {
                sb.Append(OcrFieldFormatSpec.LowerAlphaMaskSymbol);
            }
            else if (c is '\\' or '[' or ']' or '#')
            {
                sb.Append('\\').Append(c);
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static string MutateMiddleCharacter(string gt)
    {
        char[] chars = gt.ToCharArray();
        List<int> nonSpaceIndices = [];
        for (int i = 0; i < chars.Length; i++)
        {
            if (!char.IsWhiteSpace(chars[i]))
            {
                nonSpaceIndices.Add(i);
            }
        }

        Assert.True(nonSpaceIndices.Count >= 2, $"Expected at least 2 non-space characters in '{gt}'.");
        int midSlot = nonSpaceIndices.Count >= 3
            ? Math.Clamp(nonSpaceIndices.Count / 2, 1, nonSpaceIndices.Count - 2)
            : 0;
        int pos = nonSpaceIndices[midSlot];

        char c = chars[pos];
        chars[pos] = c switch
        {
            >= '0' and <= '8' => (char)(c + 1),
            '9' => '0',
            >= 'A' and <= 'Y' => (char)(c + 1),
            'Z' => 'A',
            >= 'a' and <= 'y' => (char)(c + 1),
            'z' => 'a',
            _ => 'X',
        };
        return new string(chars);
    }

    private static (byte[] Pixels, int Width, int Height) CropRoiPixels(
        OcrBenchmarkEntry entry,
        string repoRootPath,
        Dictionary<string, (byte[] Pixels, int Width, int Height)> cache)
    {
        return CropRectPixels(
            entry.ImageFile,
            entry.CenterX,
            entry.CenterY,
            entry.Width,
            entry.Height,
            repoRootPath,
            cache);
    }

    private static (byte[] Pixels, int Width, int Height) CropRectPixels(
        string imageFile,
        double centerX,
        double centerY,
        double width,
        double height,
        string repoRootPath,
        Dictionary<string, (byte[] Pixels, int Width, int Height)> cache)
    {
        if (!cache.TryGetValue(imageFile, out var img))
        {
            string fullPath = Path.Combine(repoRootPath, imageFile);
            img = LoadGray8Image(fullPath);
            cache[imageFile] = img;
        }

        int x0 = Math.Clamp((int)Math.Round(centerX - (width * 0.5)), 0, img.Width - 1);
        int y0 = Math.Clamp((int)Math.Round(centerY - (height * 0.5)), 0, img.Height - 1);
        int x1 = Math.Clamp((int)Math.Round(centerX + (width * 0.5)), x0 + 1, img.Width);
        int y1 = Math.Clamp((int)Math.Round(centerY + (height * 0.5)), y0 + 1, img.Height);

        int cw = x1 - x0;
        int ch = y1 - y0;
        byte[] crop = new byte[cw * ch];
        for (int r = 0; r < ch; r++)
        {
            Array.Copy(img.Pixels, ((y0 + r) * img.Width) + x0, crop, r * cw, cw);
        }

        return (crop, cw, ch);
    }

    private static (byte[] Pixels, int Width, int Height) LoadGray8Image(string path)
    {
        BitmapDecoder decoder = BitmapDecoder.Create(
            new Uri(path, UriKind.Absolute),
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        var gray = new FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Gray8, null, 0.0);
        int w = gray.PixelWidth;
        int h = gray.PixelHeight;
        byte[] pixels = new byte[w * h];
        gray.CopyPixels(pixels, w, 0);
        return (pixels, w, h);
    }

    private enum DefectKind
    {
        Homoglyph = 0,
        MiddleDeletion = 1,
        AdjacentSwap = 2,
        MissingStroke = 3,
        InkBleed = 4,
        SevereLowContrast = 5,
        MiddleTextError = 6,
    }

    private sealed class DefectTallies
    {
        private readonly int[] _detected = new int[7];
        private readonly int[] _tested = new int[7];

        public void Record(DefectKind kind, bool detected)
        {
            int idx = (int)kind;
            _tested[idx]++;
            if (detected)
            {
                _detected[idx]++;
            }
        }

        public (int Detected, int Tested) Get(DefectKind kind)
        {
            int idx = (int)kind;
            return (_detected[idx], _tested[idx]);
        }

        public void AddFrom(DefectTallies other)
        {
            for (int i = 0; i < _detected.Length; i++)
            {
                _detected[i] += other._detected[i];
                _tested[i] += other._tested[i];
            }
        }

        public string Format(DefectKind kind)
        {
            (int d, int t) = Get(kind);
            return FormatRateWithWilson(d, t);
        }
    }
}
