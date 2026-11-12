using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.IndustrialOcrOcv;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// Production taught-font OCV service wrapping <see cref="TaughtFontOcvVerifier"/>,
/// <see cref="IndustrialOcrTrainer"/>, and <see cref="IndustrialOcrFontModelSerializer"/>.
/// Supports multiple named font slots (<c>FontId</c>) for multi-print recipes and merges incremental teaches
/// cleanly onto loaded <c>.ocvfont.json</c> models without losing previously stored glyph templates.
/// </summary>
public sealed class TaughtFontOcvService(
    IImageLoader loader,
    Func<VideoFrame?> retainFrame,
    IPreInspectionFilterPipeline? preFilterPipeline = null) : ITaughtFontOcvService
{
    public const string DefaultFontId = "default";
    public const string AccentOk = "#10B981";
    public const string AccentReview = "#F59E0B";
    public const string AccentNg = "#EF4444";

    private const int MinRectifiedWidth = 16;
    private const int MinRectifiedHeight = 12;
    private const int MaxRectifiedWidth = 4096;
    private const int MaxRectifiedHeight = 2048;
    private const int CropMarginPixels = 16;
    private const int MinPitchPx = 5;
    private const int MaxPitchPx = 56;
    private const float MinContrastOffset = 2.0f;
    private const float MaxContrastOffset = 80.0f;
    private const float DarkOnLightBorderThreshold = 120.0f;
    private const float PolaritySwitchMinGain = 0.05f;

    private readonly object _sync = new();
    private readonly Dictionary<string, FontSlot> _fontsById = new(StringComparer.OrdinalIgnoreCase);

    public bool HasGoldenFont
    {
        get
        {
            lock (_sync)
            {
                return _fontsById.Count > 0;
            }
        }
    }

    public int RegisteredCharacterCount => GetRegisteredCharacterCount(DefaultFontId);

    public string RegisteredCharactersSummary => GetRegisteredCharactersSummary(DefaultFontId);

    public bool HasGoldenFontForId(string? fontId)
    {
        string key = NormalizeFontId(fontId);
        lock (_sync)
        {
            return _fontsById.ContainsKey(key);
        }
    }

    public int GetRegisteredCharacterCount(string? fontId)
    {
        string key = NormalizeFontId(fontId);
        lock (_sync)
        {
            if (_fontsById.TryGetValue(key, out FontSlot? slot))
            {
                return slot.Model.Count;
            }

            if (string.Equals(key, DefaultFontId, StringComparison.OrdinalIgnoreCase) && _fontsById.Count == 1)
            {
                foreach (FontSlot onlySlot in _fontsById.Values)
                {
                    return onlySlot.Model.Count;
                }
            }

            return 0;
        }
    }

    public string GetRegisteredCharactersSummary(string? fontId)
    {
        string key = NormalizeFontId(fontId);
        lock (_sync)
        {
            if (!_fontsById.TryGetValue(key, out FontSlot? slot))
            {
                if (_fontsById.Count == 0)
                {
                    return "미등록 (기준 ROI에서 '폰트 티칭' 또는 '폰트 불러오기' 필요)";
                }

                return $"폰트 '{key}' 미등록 (전체 등록 폰트 {_fontsById.Count}개)";
            }

            ReadOnlySpan<IndustrialOcrGlyphTemplate> templates = slot.Model.Templates;
            var sb = new StringBuilder(templates.Length + 16);
            for (int i = 0; i < templates.Length; i++)
            {
                sb.Append(templates[i].Character);
            }

            string idTag = string.Equals(key, DefaultFontId, StringComparison.OrdinalIgnoreCase) && _fontsById.Count <= 1
                ? string.Empty
                : $" (ID:{key})";
            return $"등록 문자 {templates.Length}종{idTag} [{sb}]";
        }
    }

    public Task<OcvTeachingOutcome> TeachFromRoiAsync(
        ImageReference image,
        bool live,
        InspectionSuiteRoiInput roi,
        string expectedText,
        OcvPolaritySelection polarity,
        float foregroundContrastOffset,
        bool appendToExistingFont,
        CancellationToken token,
        string fontId = DefaultFontId)
        => Task.Run(async () =>
        {
            ArgumentNullException.ThrowIfNull(image);
            ArgumentNullException.ThrowIfNull(roi);

            string resolvedFontId = NormalizeFontId(fontId);
            string compactExpected = CompactNonWhitespace(expectedText);
            if (compactExpected.Length == 0)
            {
                return new OcvTeachingOutcome(
                    IsSuccess: false,
                    TaughtGlyphCount: 0,
                    UniqueCharacterCount: GetRegisteredCharacterCount(resolvedFontId),
                    ExpectedLength: 0,
                    ObservedSegmentCount: 0,
                    PolarityTag: "N/A",
                    StatusSummary: "폰트 티칭 거부: 기대 문자열 미입력",
                    FailureCause: "기대 문자열이 비어 있어 골든 폰트를 생성하지 않았습니다.",
                    RecommendedAction: "선택 ROI에 인쇄된 정확한 기준 문자열을 입력한 뒤 다시 티칭하세요.");
            }

            using var reader = await MatchingImageReader.OpenAsync(image, live, loader, retainFrame, token, preFilterPipeline).ConfigureAwait(false);
            reader.PreferDirectGray = true;
            using var visionWs = new VisionWorkspace();

            ReadOnlyImageView2D<byte> roiView = ExtractRectifiedRoi(
                reader,
                image.Width,
                image.Height,
                roi,
                visionWs,
                token,
                out _,
                out _,
                out _);

            var stripFixture = new FixturePose2D(roiView.Width * 0.5f, roiView.Height * 0.5f, 0f);
            float clampedContrast = Math.Clamp(foregroundContrastOffset, MinContrastOffset, MaxContrastOffset);
            bool darkPrior = EstimateIsDarkOnLight(roiView);
            ReadOnlySpan<bool> candidatePolarities = ResolvePolarityCandidates(polarity, darkPrior);

            int bestObservedCount = 0;
            bool bestObservedDarkOnLight = darkPrior;

            for (int pIdx = 0; pIdx < candidatePolarities.Length; pIdx++)
            {
                bool darkOnLight = candidatePolarities[pIdx];
                OcvVerificationOptions candidateOptions = BuildAdaptiveOptions(
                    roiView.Width,
                    roiView.Height,
                    compactExpected.Length,
                    clampedContrast,
                    darkOnLight);

                IReadOnlyList<GoldenCharSample> extracted = TaughtFontOcvVerifier.ExtractTeachingSamplesFromLine(
                    roiView,
                    in stripFixture,
                    expectedText,
                    candidateOptions,
                    visionWs);

                if (extracted.Count == compactExpected.Length)
                {
                    var extractedList = new List<GoldenCharSample>(extracted);
                    int uniqueCount;
                    string summaryChars;
                    lock (_sync)
                    {
                        IndustrialOcrFontModel? baseModel = appendToExistingFont &&
                            _fontsById.TryGetValue(resolvedFontId, out FontSlot? existingSlot)
                                ? existingSlot.Model
                                : null;

                        IndustrialOcrFontModel updatedModel = IndustrialOcrTrainer.TrainOrMerge(
                            baseModel,
                            CollectionsMarshal.AsSpan(extractedList),
                            darkOnLight);

                        _fontsById[resolvedFontId] = new FontSlot(updatedModel, darkOnLight);
                        uniqueCount = updatedModel.Count;
                        summaryChars = GetRegisteredCharactersSummary(resolvedFontId);
                    }

                    string polarityLabel = darkOnLight ? "DarkOnLight(밝은 배경/어두운 글자)" : "LightOnDark(어두운 배경/밝은 글자)";
                    return new OcvTeachingOutcome(
                        IsSuccess: true,
                        TaughtGlyphCount: extracted.Count,
                        UniqueCharacterCount: uniqueCount,
                        ExpectedLength: compactExpected.Length,
                        ObservedSegmentCount: extracted.Count,
                        PolarityTag: polarityLabel,
                        StatusSummary: $"폰트 티칭 완료: '{compactExpected}' ({extracted.Count}글자) · {summaryChars}",
                        FailureCause: string.Empty,
                        RecommendedAction: "이제 다른 검증 대상 ROI를 선택하고 'OCV 검증'을 실행하세요.");
                }

                int segCount = ProbeSegmentedBoxCount(roiView, in stripFixture, candidateOptions, visionWs);
                if (pIdx == 0 || Math.Abs(segCount - compactExpected.Length) < Math.Abs(bestObservedCount - compactExpected.Length))
                {
                    bestObservedCount = segCount;
                    bestObservedDarkOnLight = darkOnLight;
                }
            }

            string failPolarity = bestObservedDarkOnLight ? "DarkOnLight" : "LightOnDark";
            return new OcvTeachingOutcome(
                IsSuccess: false,
                TaughtGlyphCount: 0,
                UniqueCharacterCount: GetRegisteredCharacterCount(resolvedFontId),
                ExpectedLength: compactExpected.Length,
                ObservedSegmentCount: bestObservedCount,
                PolarityTag: failPolarity,
                StatusSummary: $"폰트 티칭 거부: 분할 글자 수({bestObservedCount}개) ≠ 기대 길이({compactExpected.Length}자)",
                FailureCause: $"ROI에서 분할된 문자 블록 수({bestObservedCount}개, 극성={failPolarity})가 기대 문자열 '{compactExpected}' 길이({compactExpected.Length}자)와 일치하지 않아 골든 폰트를 생성·저장하지 않았습니다.",
                RecommendedAction: "1) ROI가 한 줄의 마킹만 감싸는지 확인하고, 2) 기대 문자열 철자·글자 수를 확인하거나, 3) 대비 오프셋·극성(밝은 글자/어두운 글자)을 조정한 뒤 다시 티칭하세요.");
        }, token);

    public Task<ImmutableArray<OcvRoiInspectionItem>> VerifyRoisAsync(
        ImageReference image,
        bool live,
        IReadOnlyList<OcvRoiVerifyRequest> requests,
        OcvPolaritySelection polarity,
        float foregroundContrastOffset,
        CancellationToken token)
        => Task.Run(async () =>
        {
            ArgumentNullException.ThrowIfNull(image);
            ArgumentNullException.ThrowIfNull(requests);
            if (requests.Count == 0)
            {
                throw new ArgumentException("OCV 검증할 대상 ROI를 먼저 선택하거나 추가하세요.", nameof(requests));
            }

            Dictionary<string, FontSlot> snapshot;
            lock (_sync)
            {
                if (_fontsById.Count == 0)
                {
                    throw new InvalidOperationException("등록된 골든 폰트가 없습니다. 먼저 기준 ROI에서 '폰트 티칭'을 실행하거나 저장된 폰트 파일을 불러오세요.");
                }

                snapshot = new Dictionary<string, FontSlot>(_fontsById, StringComparer.OrdinalIgnoreCase);
            }

            var verifiersByFontId = new Dictionary<string, (TaughtFontOcvVerifier Verifier, bool TaughtDarkOnLight)>(StringComparer.OrdinalIgnoreCase);
            using var reader = await MatchingImageReader.OpenAsync(image, live, loader, retainFrame, token, preFilterPipeline).ConfigureAwait(false);
            reader.PreferDirectGray = true;
            using var visionWs = new VisionWorkspace();

            float clampedContrast = Math.Clamp(foregroundContrastOffset, MinContrastOffset, MaxContrastOffset);
            var results = ImmutableArray.CreateBuilder<OcvRoiInspectionItem>(requests.Count);

            for (int i = 0; i < requests.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                OcvRoiVerifyRequest req = requests[i];
                var sw = Stopwatch.StartNew();

                string reqFontId = NormalizeFontId(req.FontId);
                if (!verifiersByFontId.TryGetValue(reqFontId, out var slotVerifier))
                {
                    if (!snapshot.TryGetValue(reqFontId, out FontSlot? resolvedSlot) &&
                        !snapshot.TryGetValue(DefaultFontId, out resolvedSlot))
                    {
                        sw.Stop();
                        results.Add(new OcvRoiInspectionItem(
                            req.RoiId,
                            req.Name,
                            req.ExpectedText,
                            req.FormatMask,
                            DecodedText: string.Empty,
                            OcvVerdictKind.Ng,
                            VerdictBadge: "NG · 폰트ID 미등록",
                            VerdictAccent: AccentNg,
                            MeanScore: 0f,
                            MinScore: 0f,
                            CauseSummary: $"ROI가 참조하는 골든 폰트 ID '{reqFontId}'가 등록되지 않았습니다.",
                            RecommendedAction: $"폰트 ID '{reqFontId}'로 기준 ROI를 티칭하거나 해당 폰트 파일을 불러오세요.",
                            Characters: [],
                            req.Center,
                            req.AngleRadians,
                            req.Outline,
                            CharContours: [],
                            OkCharCenters: [],
                            ReviewCharCenters: [],
                            NgCharCenters: [req.Center],
                            sw.Elapsed.TotalMicroseconds));
                        continue;
                    }

                    slotVerifier = (new TaughtFontOcvVerifier(resolvedSlot.Model), resolvedSlot.DarkOnLight);
                    verifiersByFontId[reqFontId] = slotVerifier;
                }

                var roiInput = new InspectionSuiteRoiInput(
                    req.RoiId,
                    req.Name,
                    req.Center,
                    req.Width,
                    req.Height,
                    req.AngleRadians,
                    req.Outline);

                ReadOnlyImageView2D<byte> roiView = ExtractRectifiedRoi(
                    reader,
                    image.Width,
                    image.Height,
                    roiInput,
                    visionWs,
                    token,
                    out FixturePose2D worldCropFixture,
                    out int offsetX,
                    out int offsetY);

                string compactExpected = CompactNonWhitespace(req.ExpectedText);
                string? normalizedMask = string.IsNullOrWhiteSpace(req.FormatMask) ? null : req.FormatMask.Trim();
                if (compactExpected.Length == 0 && normalizedMask is null)
                {
                    sw.Stop();
                    results.Add(new OcvRoiInspectionItem(
                        req.RoiId,
                        req.Name,
                        req.ExpectedText,
                        req.FormatMask,
                        DecodedText: string.Empty,
                        OcvVerdictKind.Ng,
                        VerdictBadge: "NG · 기대문자 미지정",
                        VerdictAccent: AccentNg,
                        MeanScore: 0f,
                        MinScore: 0f,
                        CauseSummary: "ROI에 기대 문자열(ExpectedText) 또는 필드 형식 마스크가 설정되지 않았습니다.",
                        RecommendedAction: "해당 ROI를 선택하고 기대 문자열 또는 형식 마스크(D/A/a/X/#)를 입력한 뒤 다시 OCV 검증을 실행하세요.",
                        Characters: [],
                        req.Center,
                        req.AngleRadians,
                        req.Outline,
                        CharContours: [],
                        OkCharCenters: [],
                        ReviewCharCenters: [],
                        NgCharCenters: [req.Center],
                        sw.Elapsed.TotalMicroseconds));
                    continue;
                }

                OcrFieldFormatSpec fieldSpec;
                try
                {
                    fieldSpec = new OcrFieldFormatSpec(
                        FormatMask: normalizedMask,
                        ExpectedText: string.IsNullOrWhiteSpace(req.ExpectedText) ? null : req.ExpectedText,
                        StrictLengthMatch: true);
                }
                catch (FormatException ex)
                {
                    sw.Stop();
                    results.Add(new OcvRoiInspectionItem(
                        req.RoiId,
                        req.Name,
                        req.ExpectedText,
                        req.FormatMask,
                        DecodedText: string.Empty,
                        OcvVerdictKind.Ng,
                        VerdictBadge: "NG · 마스크 문법 오류",
                        VerdictAccent: AccentNg,
                        MeanScore: 0f,
                        MinScore: 0f,
                        CauseSummary: $"필드 형식 마스크 구문 오류: {ex.Message}",
                        RecommendedAction: "마스크 기호(D:숫자, A:대문자, a:소문자, X:영숫자, #:임의문자, [0-9A-F], \\리터럴) 문법을 확인하세요.",
                        Characters: [],
                        req.Center,
                        req.AngleRadians,
                        req.Outline,
                        CharContours: [],
                        OkCharCenters: [],
                        ReviewCharCenters: [],
                        NgCharCenters: [req.Center],
                        sw.Elapsed.TotalMicroseconds));
                    continue;
                }

                int expectedSlotLen = compactExpected.Length > 0 ? compactExpected.Length : Math.Max(1, fieldSpec.SlotCount);
                var stripFixture = new FixturePose2D(roiView.Width * 0.5f, roiView.Height * 0.5f, 0f);
                OcvVerificationResult lineRes = ResolveAndVerifyLine(
                    slotVerifier.Verifier,
                    roiView,
                    in stripFixture,
                    fieldSpec,
                    polarity,
                    slotVerifier.TaughtDarkOnLight,
                    expectedSlotLen,
                    clampedContrast,
                    visionWs);
                sw.Stop();

                results.Add(BuildRoiInspectionItem(
                    req,
                    roiView.Width,
                    roiView.Height,
                    in worldCropFixture,
                    offsetX,
                    offsetY,
                    lineRes,
                    sw.Elapsed.TotalMicroseconds));
            }

            return results.MoveToImmutable();
        }, token);

    public void SaveFontModel(Stream destination, string fontId = DefaultFontId)
    {
        ArgumentNullException.ThrowIfNull(destination);
        string key = NormalizeFontId(fontId);
        IndustrialOcrFontModel model;
        lock (_sync)
        {
            if (!_fontsById.TryGetValue(key, out FontSlot? slot))
            {
                throw new InvalidOperationException($"저장할 티칭 골든 폰트('{key}')가 없습니다. 먼저 '폰트 티칭'을 실행하세요.");
            }

            model = slot.Model;
        }

        IndustrialOcrFontModelSerializer.Save(destination, model);
    }

    public void LoadFontModel(Stream source, string fontId = DefaultFontId)
    {
        ArgumentNullException.ThrowIfNull(source);
        string key = NormalizeFontId(fontId);
        IndustrialOcrFontModel loaded = IndustrialOcrFontModelSerializer.Load(source);
        lock (_sync)
        {
            _fontsById[key] = new FontSlot(loaded, loaded.DarkOnLight);
        }
    }

    public void ClearFontModel(string? fontId = null)
    {
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(fontId))
            {
                _fontsById.Clear();
                return;
            }

            _fontsById.Remove(NormalizeFontId(fontId));
        }
    }

    public IReadOnlyDictionary<string, string> ExportAllFontModelsJson()
    {
        Dictionary<string, FontSlot> snapshot;
        lock (_sync)
        {
            snapshot = new Dictionary<string, FontSlot>(_fontsById, StringComparer.OrdinalIgnoreCase);
        }

        var exported = new Dictionary<string, string>(snapshot.Count, StringComparer.OrdinalIgnoreCase);
        foreach ((string key, FontSlot slot) in snapshot)
        {
            using var ms = new MemoryStream();
            IndustrialOcrFontModelSerializer.Save(ms, slot.Model);
            exported[key] = Encoding.UTF8.GetString(ms.ToArray());
        }

        return exported;
    }

    public void ImportAllFontModelsJson(IReadOnlyDictionary<string, string>? fontModelsJson, bool clearExisting = true)
    {
        var loadedSlots = new Dictionary<string, FontSlot>(StringComparer.OrdinalIgnoreCase);
        if (fontModelsJson is not null)
        {
            foreach ((string rawId, string json) in fontModelsJson)
            {
                if (string.IsNullOrWhiteSpace(json))
                {
                    continue;
                }

                using var ms = new MemoryStream(Encoding.UTF8.GetBytes(json));
                IndustrialOcrFontModel loaded = IndustrialOcrFontModelSerializer.Load(ms);
                loadedSlots[NormalizeFontId(rawId)] = new FontSlot(loaded, loaded.DarkOnLight);
            }
        }

        lock (_sync)
        {
            if (clearExisting)
            {
                _fontsById.Clear();
            }

            foreach ((string key, FontSlot slot) in loadedSlots)
            {
                _fontsById[key] = slot;
            }
        }
    }

    internal static OcvVerificationResult SelectPreferredPolarityVerification(
        OcvVerificationResult primary,
        OcvVerificationResult alternate)
    {
        int primaryRank = GetVerdictQualityRank(primary.Verdict);
        int altRank = GetVerdictQualityRank(alternate.Verdict);
        if (altRank != primaryRank)
        {
            return altRank > primaryRank ? alternate : primary;
        }

        (int primaryNg, int primaryReview) = CountDefectiveCharacters(primary);
        (int altNg, int altReview) = CountDefectiveCharacters(alternate);
        if (altNg != primaryNg)
        {
            return altNg < primaryNg ? alternate : primary;
        }

        if (altReview != primaryReview)
        {
            return altReview < primaryReview ? alternate : primary;
        }

        return alternate.MeanMatchScore > (primary.MeanMatchScore + PolaritySwitchMinGain)
            ? alternate
            : primary;
    }

    private static int GetVerdictQualityRank(OcvVerdictKind verdict)
        => verdict switch
        {
            OcvVerdictKind.Ok => 2,
            OcvVerdictKind.Review => 1,
            _ => 0,
        };

    private static (int NgCount, int ReviewCount) CountDefectiveCharacters(OcvVerificationResult result)
    {
        if (result.Characters.Count == 0)
        {
            return (result.IsNg ? int.MaxValue : 0, result.NeedsReview ? 1 : 0);
        }

        int ng = 0;
        int review = 0;
        for (int i = 0; i < result.Characters.Count; i++)
        {
            OcvVerdictKind v = result.Characters[i].Verdict;
            if (v == OcvVerdictKind.Ng)
            {
                ng++;
            }
            else if (v == OcvVerdictKind.Review)
            {
                review++;
            }
        }

        return (ng, review);
    }

    private static OcvVerificationResult ResolveAndVerifyLine(
        TaughtFontOcvVerifier verifier,
        ReadOnlyImageView2D<byte> roiView,
        in FixturePose2D stripFixture,
        OcrFieldFormatSpec fieldSpec,
        OcvPolaritySelection polarity,
        bool taughtDarkOnLight,
        int expectedSlotLen,
        float clampedContrast,
        VisionWorkspace visionWs)
    {
        if (polarity is OcvPolaritySelection.DarkOnLight or OcvPolaritySelection.LightOnDark)
        {
            bool explicitDark = polarity == OcvPolaritySelection.DarkOnLight;
            OcvVerificationOptions explicitOptions = BuildAdaptiveOptions(
                roiView.Width,
                roiView.Height,
                expectedSlotLen,
                clampedContrast,
                explicitDark);
            return verifier.VerifyLine(roiView, in stripFixture, fieldSpec, explicitOptions, visionWs);
        }

        OcvVerificationOptions primaryOptions = BuildAdaptiveOptions(
            roiView.Width,
            roiView.Height,
            expectedSlotLen,
            clampedContrast,
            taughtDarkOnLight);
        OcvVerificationResult primary = verifier.VerifyLine(roiView, in stripFixture, fieldSpec, primaryOptions, visionWs);
        if (primary.IsOk || EstimateIsDarkOnLight(roiView) == taughtDarkOnLight)
        {
            return primary;
        }

        OcvVerificationOptions altOptions = BuildAdaptiveOptions(
            roiView.Width,
            roiView.Height,
            expectedSlotLen,
            clampedContrast,
            !taughtDarkOnLight);
        OcvVerificationResult alt = verifier.VerifyLine(roiView, in stripFixture, fieldSpec, altOptions, visionWs);
        return SelectPreferredPolarityVerification(primary, alt);
    }

    private static OcvRoiInspectionItem BuildRoiInspectionItem(
        OcvRoiVerifyRequest req,
        int roiW,
        int roiH,
        in FixturePose2D worldCropFixture,
        int offsetX,
        int offsetY,
        OcvVerificationResult lineRes,
        double elapsedUs)
    {
        float halfW = (roiW - 1) * 0.5f;
        float halfH = (roiH - 1) * 0.5f;

        var charItems = ImmutableArray.CreateBuilder<OcvCharacterInspectionItem>(lineRes.Characters.Count);
        var charContours = ImmutableArray.CreateBuilder<ImmutableArray<PointD>>(lineRes.Characters.Count);
        var okCenters = ImmutableArray.CreateBuilder<PointD>();
        var reviewCenters = ImmutableArray.CreateBuilder<PointD>();
        var ngCenters = ImmutableArray.CreateBuilder<PointD>();

        for (int i = 0; i < lineRes.Characters.Count; i++)
        {
            OcvCharacterVerification ch = lineRes.Characters[i];
            (string charBadge, string charAccent) = DescribeCharBadgeAndAccent(ch.Verdict);
            char expDisplay = ch.ExpectedChar ?? '-';
            string summary = $"[{ch.CharIndex + 1}] 기대 '{expDisplay}' → 관측 '{ch.ObservedChar}' | {charBadge} | 점수={ch.MatchScore:F3} · 대비={ch.LocalContrastGv:F0}GV · 결손={ch.MissingStrokeRatio:P0} · 번짐={ch.BleedRatio:P0} | {ch.Cause}";

            charItems.Add(new OcvCharacterInspectionItem(
                ch.CharIndex,
                expDisplay,
                ch.ObservedChar,
                ch.Verdict,
                charBadge,
                charAccent,
                ch.MatchScore,
                ch.LocalContrastGv,
                ch.MissingStrokeRatio,
                ch.BleedRatio,
                ch.Cause,
                summary));

            if (ch.EndCol >= ch.StartCol && ch.BottomRow >= ch.TopRow)
            {
                float lx0 = ch.StartCol - halfW;
                float ly0 = ch.TopRow - halfH;
                float lx1 = ch.EndCol - halfW;
                float ly1 = ch.BottomRow - halfH;

                var p0 = worldCropFixture.LocalToWorld(lx0, ly0);
                var p1 = worldCropFixture.LocalToWorld(lx1, ly0);
                var p2 = worldCropFixture.LocalToWorld(lx1, ly1);
                var p3 = worldCropFixture.LocalToWorld(lx0, ly1);

                charContours.Add([
                    new PointD(offsetX + p0.X, offsetY + p0.Y),
                    new PointD(offsetX + p1.X, offsetY + p1.Y),
                    new PointD(offsetX + p2.X, offsetY + p2.Y),
                    new PointD(offsetX + p3.X, offsetY + p3.Y),
                ]);

                var pc = worldCropFixture.LocalToWorld(0.5f * (lx0 + lx1), 0.5f * (ly0 + ly1));
                PointD worldPt = new(offsetX + pc.X, offsetY + pc.Y);
                switch (ch.Verdict)
                {
                    case OcvVerdictKind.Ok:
                        okCenters.Add(worldPt);
                        break;
                    case OcvVerdictKind.Review:
                        reviewCenters.Add(worldPt);
                        break;
                    default:
                        ngCenters.Add(worldPt);
                        break;
                }
            }
        }

        if (lineRes.Verdict == OcvVerdictKind.Ng && ngCenters.Count == 0)
        {
            ngCenters.Add(req.Center);
        }
        else if (lineRes.Verdict == OcvVerdictKind.Review && reviewCenters.Count == 0)
        {
            reviewCenters.Add(req.Center);
        }

        (string badge, string accent) = lineRes.Verdict switch
        {
            OcvVerdictKind.Ok => ("OK (정상 합격)", AccentOk),
            OcvVerdictKind.Review => ("REVIEW (경계 경고 · 제품 NG 아님)", AccentReview),
            _ => ("NG (인쇄 불량)", AccentNg),
        };

        return new OcvRoiInspectionItem(
            req.RoiId,
            req.Name,
            req.ExpectedText,
            req.FormatMask,
            lineRes.DecodedText,
            lineRes.Verdict,
            badge,
            accent,
            lineRes.MeanMatchScore,
            lineRes.MinMatchScore,
            lineRes.CauseSummary,
            lineRes.RecommendedAction,
            charItems.ToImmutable(),
            req.Center,
            req.AngleRadians,
            req.Outline,
            charContours.ToImmutable(),
            okCenters.ToImmutable(),
            reviewCenters.ToImmutable(),
            ngCenters.ToImmutable(),
            elapsedUs);
    }

    private static (string Badge, string Accent) DescribeCharBadgeAndAccent(OcvVerdictKind verdict)
        => verdict switch
        {
            OcvVerdictKind.Ok => ("OK", AccentOk),
            OcvVerdictKind.Review => ("REVIEW (경고)", AccentReview),
            _ => ("NG", AccentNg),
        };

    private static int ProbeSegmentedBoxCount(
        ReadOnlyImageView2D<byte> roiView,
        in FixturePose2D stripFixture,
        OcvVerificationOptions options,
        VisionWorkspace workspace)
    {
        int stripW = Math.Max(MinRectifiedWidth, roiView.Width);
        int stripH = Math.Max(MinRectifiedHeight, roiView.Height);
        var segParams = new OcrMaxSegmentParams(
            printMethod: options.PrintMethod,
            rectifiedWidth: stripW,
            rectifiedHeight: stripH,
            minCharWidth: options.MinCharWidth,
            maxCharWidth: options.MaxCharWidth,
            expectedPitch: options.ExpectedPitch,
            dotClosingRadiusX: options.DotClosingRadiusX,
            dotClosingRadiusY: options.DotClosingRadiusY,
            foregroundContrastOffset: options.ForegroundContrastOffset,
            darkOnLight: options.DarkOnLight);

        Span<byte> rectifiedStrip = workspace.GetByteBufferA(checked(stripW * stripH));
        int probeLen = Math.Min(OcrMaxSegmenter.MaxSupportedSegmentedChars, options.MaxLineCharacters + 1);
        Span<SegmentedCharBox> boxes = stackalloc SegmentedCharBox[probeLen];
        return OcrMaxSegmenter.SegmentLine(roiView, in stripFixture, in segParams, workspace, rectifiedStrip, boxes);
    }

    private static OcvVerificationOptions BuildAdaptiveOptions(
        int roiWidth,
        int roiHeight,
        int compactExpectedLen,
        float contrastOffset,
        bool darkOnLight)
    {
        int expectedPitch = Math.Clamp(
            roiWidth / Math.Max(1, compactExpectedLen),
            MinPitchPx,
            MaxPitchPx);
        int minCharWidth = Math.Clamp(roiHeight / 12, 2, 4);
        int maxCharWidth = Math.Max(
            8,
            (int)MathF.Ceiling(MathF.Max(roiHeight * 1.15f, expectedPitch * 1.45f)));

        return new OcvVerificationOptions(
            MinCharWidth: minCharWidth,
            MaxCharWidth: maxCharWidth,
            ExpectedPitch: expectedPitch,
            ForegroundContrastOffset: contrastOffset,
            DarkOnLight: darkOnLight);
    }

    private static ReadOnlySpan<bool> ResolvePolarityCandidates(OcvPolaritySelection polarity, bool darkOnLightPrior)
        => polarity switch
        {
            OcvPolaritySelection.DarkOnLight => [true],
            OcvPolaritySelection.LightOnDark => [false],
            _ => darkOnLightPrior ? [true, false] : [false, true],
        };

    private static bool EstimateIsDarkOnLight(ReadOnlyImageView2D<byte> view)
    {
        int w = view.Width;
        int h = view.Height;
        ReadOnlySpan<byte> px = view.Pixels;
        int stride = view.Stride;

        long borderSum = 0;
        int borderCount = 0;
        for (int x = 0; x < w; x++)
        {
            borderSum += px[x] + px[((h - 1) * stride) + x];
            borderCount += 2;
        }

        for (int y = 1; y < h - 1; y++)
        {
            borderSum += px[y * stride] + px[(y * stride) + (w - 1)];
            borderCount += 2;
        }

        float borderMean = borderSum / (float)Math.Max(1, borderCount);
        return borderMean >= DarkOnLightBorderThreshold;
    }

    private static ReadOnlyImageView2D<byte> ExtractRectifiedRoi(
        MatchingImageReader reader,
        int imageWidth,
        int imageHeight,
        InspectionSuiteRoiInput roi,
        VisionWorkspace visionWs,
        CancellationToken token,
        out FixturePose2D fixture,
        out int offsetX,
        out int offsetY)
    {
        double halfDiag = (0.5 * Math.Sqrt((roi.Width * roi.Width) + (roi.Height * roi.Height))) + CropMarginPixels;
        int x0 = Math.Clamp((int)Math.Floor(roi.Center.X - halfDiag), 0, Math.Max(0, imageWidth - 1));
        int y0 = Math.Clamp((int)Math.Floor(roi.Center.Y - halfDiag), 0, Math.Max(0, imageHeight - 1));
        int x1 = Math.Clamp((int)Math.Ceiling(roi.Center.X + halfDiag), x0 + 1, imageWidth);
        int y1 = Math.Clamp((int)Math.Ceiling(roi.Center.Y + halfDiag), y0 + 1, imageHeight);

        var crop = reader.Read(
            x0,
            y0,
            x1 - x0,
            y1 - y0,
            token,
            maximumPixels: VisionAlgorithm.OpenSrc.PatternMatching.CorrelationTemplate.MaximumSearchPixels);
        offsetX = x0;
        offsetY = y0;
        var cropView = new ReadOnlyImageView2D<byte>(crop.Pixels.Span, crop.Width, crop.Height, crop.Stride);
        fixture = new FixturePose2D((float)(roi.Center.X - x0), (float)(roi.Center.Y - y0), (float)roi.AngleRadians);

        int roiW = Math.Clamp((int)Math.Round(roi.Width), MinRectifiedWidth, Math.Min(MaxRectifiedWidth, crop.Width));
        int roiH = Math.Clamp((int)Math.Round(roi.Height), MinRectifiedHeight, Math.Min(MaxRectifiedHeight, crop.Height));
        int pixelCount = checked(roiW * roiH);

        Span<byte> rectified = visionWs.GetByteBufferA(pixelCount);
        Span<float> scratch = visionWs.GetFloatBufferA(pixelCount);
        SimdAffineWarper.WarpAffineBilinear(cropView, in fixture, roiW, roiH, rectified, scratch);

        byte[] copy = rectified.Slice(0, pixelCount).ToArray();
        return new ReadOnlyImageView2D<byte>(copy, roiW, roiH, roiW);
    }

    private static string NormalizeFontId(string? fontId)
        => string.IsNullOrWhiteSpace(fontId) ? DefaultFontId : fontId.Trim();

    private static string CompactNonWhitespace(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (!char.IsWhiteSpace(text[i]))
            {
                sb.Append(text[i]);
            }
        }

        return sb.ToString();
    }

    private sealed record FontSlot(IndustrialOcrFontModel Model, bool DarkOnLight);
}
