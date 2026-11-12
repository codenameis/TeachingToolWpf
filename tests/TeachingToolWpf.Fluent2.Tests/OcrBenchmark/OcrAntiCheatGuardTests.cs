using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace TeachingToolWpf.Fluent2.Tests.OcrBenchmark;

public sealed class OcrAntiCheatGuardTests(ITestOutputHelper output)
{
    private const int MinAnswerSubstringLength = 6;

    // Generic full-alphabet or digit-order reference catalogs that are not specific answer words.
    // Note: specific benchmark lines like "01234567890" and "1234567890" are still checked in OCR engine files.
    private static readonly HashSet<string> GenericCatalogLines = new(StringComparer.Ordinal)
    {
        "0123456789",
        "ABCDEFGHIJKLMNOPQRSTUVWXYZ",
        "abcdefghijklmnopqrstuvwxyz",
        "ABCDEFGHIJKLM",
        "NOPQRSTUVWXYZ",
        "ABCDEFGHIJKLMN",
        "OPQRSTUVWXYZ",
    };

    // Per Task 4.1: Any exception across all 693 .cs files in src/ MUST be bound to an explicit "RelativeFilePath::Identifier"
    // with only the exact verified English prose/UI/sample-generator phrase and documented reason.
    // ZERO benchmark answer codes/dates/tokens ("2024/01/28", "FILTEC", "LASETEK", "200523", etc.) are ever whitelisted in OCR paths.
    private static readonly Dictionary<string, (string[] AllowedPhrases, bool IgnoreCase, string Reason)> AllowedUiAndNonOcrLiteralsByFileAndMember =
        new(StringComparer.Ordinal)
        {
            ["src/TeachingToolWpf.Diagnostics/DiagnosticStartup.cs::readonly"] =
                (["--group-interaction-benchmark"], false, "CLI flag shares 'action' with 'transaction'."),
            ["src/TeachingToolWpf.Diagnostics/GroupInteractionRun.cs::Attach"] =
                (["artifacts/group-interaction.json"], false, "Diagnostic output path."),
            ["src/TeachingToolWpf.Diagnostics/InspectionSmoke.Controls.cs::VerifyPrimitiveControlsAsync"] =
                (["sample ", "fractional", "detections"], false, "Non-OCR primitive shape diagnostic prose."),
            ["src/TeachingToolWpf.Diagnostics/InspectionSmoke.OpenCvPins.cs::VerifyFractionInputAsync"] =
                (["fractional"], false, "Non-OCR numeric input smoke prose."),
            ["src/TeachingToolWpf.Diagnostics/InspectionSmoke.OpenCvPins.cs::VerifyNumericInputAsync"] =
                (["fractional"], false, "Non-OCR numeric input smoke prose."),
            ["src/TeachingToolWpf.Diagnostics/InspectionSmoke.Startup.cs::VerifyStartupAsync"] =
                (["actionable", "detections", "sample "], false, "Non-OCR startup pattern smoke prose."),
            ["src/TeachingToolWpf.Diagnostics/RepeatedDocumentRun.cs::Attach"] =
                (["interaction"], false, "Canvas repeated-ROI benchmark description."),
            ["src/TeachingToolWpf.Diagnostics/RoiInteractionRun.cs::Attach"] =
                (["roi-interactions.json"], false, "Diagnostic output path."),
            ["src/TeachingToolWpf.Fluent2/TeachingReadiness/SubPixelTeachingReadinessEngine.cs::Analyze"] =
                (["sample count"], false, "ArgumentException message ('ROI interior sample count')."),
            ["src/TeachingToolWpf.Fluent2/ViewModels/Fluent2StudioViewModel.cs::ApplyReadinessReport"] =
                (["LOW CONTRAST"], false, "UI teaching readiness badge label 'LOW CONTRAST · {score}%'."),
            ["src/VisionInspection/SuiteAlgorithms/CalibrationRoiAlgorithm.cs::CalibrationRoiAlgorithm"] =
                (["Linear Transform"], false, "Calibration help text sharing 'r Tran'/' Trans' with 'Customer Transaction'."),
            ["src/TeachingToolWpf.Presentation/ViewModels/MainWindowViewModel.Commands.cs::LoadInspectionSuiteSampleAsync"] =
                (["EXP.2027.11.24"], false, "Procedural sample menu display label '도트 매트릭스 잉크젯 (EXP.2027.11.24)'."),
            ["src/VisionAlgorithm/OpenSrc/Curvilinear/BeadContinuityInspector.cs::InspectPath"] =
                (["directions"], false, "Non-OCR curvilinear exception prose."),
            ["src/VisionAlgorithm/OpenSrc/EdgeFewShot/Engine/EdgeFewShotTrainer.cs::TrainFromSamples"] =
                (["Sample #"], false, "Non-OCR few-shot exception message."),
            ["src/VisionAlgorithm/OpenSrc/EdgeFewShot/Engine/EmbeddingFewShotClassifier.cs::Train"] =
                (["training example "], false, "Non-OCR few-shot exception message."),
            ["src/VisionAlgorithm/OpenSrc/GoldenVariation/VariationModelTrainer.cs::Train"] =
                (["training sample "], false, "Non-OCR golden variation exception message."),
            ["src/VisionAlgorithm/OpenSrc/IndustrialOcrOcv/IndustrialOcrGlyphTemplate.cs::QuadrantDensities"] =
                (["Projection profile"], true, "ArgumentException message ('Projection profile dimensions')."),
            ["src/VisionAlgorithm/OpenSrc/IndustrialOcrOcv/IndustrialOcrTrainer.cs::Train"] =
                (["character sample "], true, "ArgumentException message ('golden character sample')."),
            ["src/VisionAlgorithm/OpenSrc/IndustrialOcrOcv/IndustrialOcrTrainer.cs::TrainOrMerge"] =
                (["character sample "], true, "ArgumentException message ('golden character sample')."),
            ["src/VisionAlgorithm.Verification/Synthetic8kTestGen/UnifiedNewSubsystems8kRunner.cs::RunIndustrialOcrOcvSuite"] =
                (["EXP.2027.11.24"], false, "Synthetic 8K procedural test string 'EXP.2027.11.24'."),
            ["src/VisionAlgorithm.Verification/Synthetic8kTestGen/UnifiedTestImageSuiteGenerator.cs::UnifiedTestImageSuiteGenerator"] =
                (["0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ.-/"], false, "Synthetic font atlas character catalog."),
            ["src/VisionAlgorithm.Verification/Synthetic8kTestGen/UnifiedTestImageSuiteGenerator.cs::ExportAllTestSuitesToDisk"] =
                (["EXP.2027.11.24"], false, "Synthetic 8K procedural manifest string."),
            ["src/VisionAlgorithm/OpenSrc/ToolBlock/CaliperLineCircleToolSpec.cs::CaliperLineCircleToolSpec"] =
                (["ransacInlierThresholdPixels"], false, "Non-OCR caliper parameter exception message."),
            ["src/VisionImaging.Sources/InspectionSuiteSampleTileSource.cs::RenderIndustrialOcrOcvScene"] =
                (["EXP.2027.11.24"], false, "Procedural sample tile generator text."),
        };

    // Allowed 3+ digit Width/Height bounds in OCR engine sources (Task 4.3: memory/buffer/clamp bounds with explicit reasons).
    private static readonly Dictionary<string, string> AllowedOcrDimensionBoundsWhitelist =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["src/VisionAlgorithm/OpenSrc/IndustrialOcrOcv/OcvVerificationOptions.cs::OcvVerificationOptions"] =
                "MaxLineCharacters upper invariant guard (> 256) preventing stackalloc overflow.",
        };

    private static readonly Regex AdjacentStringConcatRegex = new(
        "\"((?:\\\\.|[^\"\\\\])*)\"\\s*\\+\\s*\"((?:\\\\.|[^\"\\\\])*)\"",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex RegularOrVerbatimStringRegex = new(
        "@\"((?:\"\"|[^\"])*)\"|\"((?:\\\\.|[^\"\\\\])*)\"",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex InterpolatedStringRegex = new(
        "\\$@?\"((?:\\\\.|\\{\\{|\\}\\}|[^\"\\\\])*)\"|@\\$\"((?:\"\"|\\{\\{|\\}\\}|[^\"])*)\"",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex CharLiteralRegex = new(
        "'(?:\\\\.|[^'\\\\])'",
        RegexOptions.Compiled);

    private static readonly Regex ConfidenceFloorRegex = new(
        @"MathF?\.Max\s*\(\s*(?:[^,]+,\s*0\.[789]\d*f?|0\.[789]\d*f?\s*,)",
        RegexOptions.Compiled);

    private static readonly Regex RoiNameBranchRegex = new(
        @"\b\w*Name\s*\.\s*(?:Contains|StartsWith|EndsWith|Equals)\s*\(|\bstring\.Equals\s*\([^)]*\b\w*Name\b|\b\w*Name\s*[!=]=\s*""",
        RegexOptions.Compiled);

    private static readonly Regex DimensionThreeDigitComparisonRegex = new(
        @"(?:\b(?:Width|Height|RoiWidth|RoiHeight|roiWidth|roiHeight|srcW|srcH|imgW|imgH|w|h)\b\s*(?:==|!=|<=|>=|<|>)\s*\b\d{3,}\b|\b\d{3,}\b\s*(?:==|!=|<=|>=|<|>)\s*\b(?:Width|Height|RoiWidth|RoiHeight|roiWidth|roiHeight|srcW|srcH|imgW|imgH|w|h)\b)",
        RegexOptions.Compiled);

    [Fact]
    public void AllSrcFiles_ContainZeroUnwhitelistedAnswerSubstringsRoiNameBranchesOrDimensionCheats()
    {
        (OcrBenchmarkDataset dataset, string repoRootPath, _) = OcrBenchmarkDataset.LoadFromDefaultLocation();
        (HashSet<string> caseInsensitiveSubstrings, HashSet<string> caseSensitiveSubstrings) = BuildForbiddenSubstrings(dataset);
        Assert.NotEmpty(caseInsensitiveSubstrings);

        string srcRoot = Path.Combine(repoRootPath, "src");
        List<string> allSrcFiles = Directory
            .GetFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(p => !IsBuildArtifactPath(p))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.True(allSrcFiles.Count >= 50, $"Expected full src/ scan (>=50 .cs files), found {allSrcFiles.Count}.");

        var violations = new List<string>();
        foreach (string filePath in allSrcFiles)
        {
            string relPath = Path.GetRelativePath(repoRootPath, filePath).Replace('\\', '/');
            string sourceText = File.ReadAllText(filePath);
            bool isOcrEngineFile =
                relPath.StartsWith("src/VisionAlgorithm/OpenSrc/IndustrialOcrOcv/", StringComparison.OrdinalIgnoreCase) ||
                relPath.StartsWith("src/VisionAlgorithm/OpenSrc/LightweightOcr/", StringComparison.OrdinalIgnoreCase) ||
                relPath.StartsWith("src/VisionOcr.PpOcr/", StringComparison.OrdinalIgnoreCase);
            bool isSuiteServiceFile =
                string.Equals(relPath, "src/VisionInspection/Matching/InspectionSuiteService.cs", StringComparison.OrdinalIgnoreCase) ||
                relPath.StartsWith("src/VisionInspection/SuiteAlgorithms/", StringComparison.OrdinalIgnoreCase);

            AnalyzeSourceText(
                sourceText,
                relPath,
                isOcrEngineFile,
                isSuiteServiceFile,
                caseInsensitiveSubstrings,
                caseSensitiveSubstrings,
                violations);
        }

        output.WriteLine($"Scanned {allSrcFiles.Count} C# files across src/ against {caseInsensitiveSubstrings.Count} >=6-char benchmark substrings.");
        Assert.True(
            violations.Count == 0,
            $"OCR Anti-Cheat Guard Violations ({violations.Count}):\n" + string.Join('\n', violations));
    }

    [Theory]
    [InlineData(
        "ConcatenatedDateAnswer",
        "src/VisionAlgorithm/OpenSrc/LightweightOcr/FakeEngine.cs",
        true,
        false,
        """
        namespace Fake;
        public sealed class FakeEngine
        {
            public string Run() => "2024/" + "01/28";
        }
        """,
        "substring")]
    [InlineData(
        "InterpolatedDotOcrAnswer",
        "src/VisionAlgorithm/OpenSrc/IndustrialOcrOcv/FakeOcv.cs",
        true,
        false,
        """
        namespace Fake;
        public sealed class FakeOcv
        {
            public string Format(int tag) => $"MFG:{tag}:200523";
        }
        """,
        "substring")]
    [InlineData(
        "CharSequenceCheat",
        "src/VisionAlgorithm/OpenSrc/LightweightOcr/FakeCharLut.cs",
        true,
        false,
        """
        using System;
        namespace Fake;
        public sealed class FakeCharLut
        {
            public bool IsTarget(ReadOnlySpan<char> s)
            {
                ReadOnlySpan<char> key = ['F', 'I', 'L', 'T', 'E', 'C'];
                return s.SequenceEqual(key);
            }
        }
        """,
        "char-sequence")]
    [InlineData(
        "RoiNameBranchInOcrService",
        "src/VisionInspection/Matching/InspectionSuiteService.cs",
        false,
        true,
        """
        namespace TeachingToolWpf.Imaging.Matching;
        public sealed class InspectionSuiteService
        {
            private void RunIndustrialOcrOnRoi(dynamic target)
            {
                if (target.Name.Contains("Expire"))
                {
                    return;
                }
            }
        }
        """,
        "ROI Name branch")]
    [InlineData(
        "ImageDimensionBranchInOcrEngine",
        "src/VisionAlgorithm/OpenSrc/IndustrialOcrOcv/FakeLayoutEngine.cs",
        true,
        false,
        """
        namespace Fake;
        public sealed class FakeLayoutEngine
        {
            public int SelectMode(int Width, int Height)
            {
                if (Width >= 820 && Height >= 460)
                {
                    return 2;
                }
                return 0;
            }
        }
        """,
        "dimension comparison")]
    [InlineData(
        "ArtificialConfidenceFloor",
        "src/VisionAlgorithm/OpenSrc/LightweightOcr/FakeScore.cs",
        true,
        false,
        """
        using System;
        namespace Fake;
        public sealed class FakeScore
        {
            public float Boost(float iou) => MathF.Max(0.90f, iou);
        }
        """,
        "confidence floor")]
    public void GuardAnalyzer_DetectsInjectedViolationsInMemoryWithoutTouchingDisk(
        string scenarioName,
        string virtualRelPath,
        bool isOcrEngineFile,
        bool isSuiteServiceFile,
        string injectedCSharpSource,
        string expectedViolationKeyword)
    {
        (OcrBenchmarkDataset dataset, _, _) = OcrBenchmarkDataset.LoadFromDefaultLocation();
        (HashSet<string> caseInsensitiveSubstrings, HashSet<string> caseSensitiveSubstrings) = BuildForbiddenSubstrings(dataset);

        var violations = new List<string>();
        AnalyzeSourceText(
            injectedCSharpSource,
            virtualRelPath,
            isOcrEngineFile,
            isSuiteServiceFile,
            caseInsensitiveSubstrings,
            caseSensitiveSubstrings,
            violations);

        output.WriteLine($"[NegativeTest:{scenarioName}] Detected {violations.Count} violation(s): {string.Join(" | ", violations)}");
        Assert.NotEmpty(violations);
        Assert.Contains(violations, v => v.Contains(expectedViolationKeyword, StringComparison.OrdinalIgnoreCase));
    }

    internal static (HashSet<string> CaseInsensitive, HashSet<string> CaseSensitiveOrCode) BuildForbiddenSubstrings(OcrBenchmarkDataset dataset)
    {
        var ciSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var csOrCodeSet = new HashSet<string>(StringComparer.Ordinal);
        var codeCiSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddLineSubstrings(string rawLine)
        {
            string line = rawLine.Trim();
            if (line.Length < MinAnswerSubstringLength || GenericCatalogLines.Contains(line))
            {
                return;
            }

            for (int start = 0; start <= line.Length - MinAnswerSubstringLength; start++)
            {
                string sub = line.Substring(start, MinAnswerSubstringLength);
                if (string.IsNullOrWhiteSpace(sub))
                {
                    continue;
                }

                ciSet.Add(sub);
                csOrCodeSet.Add(sub);
                if (sub.Any(ch => char.IsDigit(ch) || (!char.IsLetter(ch) && !char.IsWhiteSpace(ch))))
                {
                    codeCiSet.Add(sub);
                }
            }
        }

        foreach (OcrBenchmarkEntry entry in dataset.Entries)
        {
            foreach (string rawLine in entry.GroundTruthLines)
            {
                AddLineSubstrings(rawLine);
            }
        }

        if (dataset.PrintGroups is not null)
        {
            foreach (OcrPrintGroupSpec pg in dataset.PrintGroups)
            {
                if (pg.OcvSpecimens is null)
                {
                    continue;
                }

                foreach (OcrOcvSpecimenEntry spec in pg.OcvSpecimens)
                {
                    AddLineSubstrings(spec.GroundTruth);
                }
            }
        }

        // Merge lowercase/uppercase variants of any code/digit/punctuation substring into csOrCodeSet as well
        foreach (string codeSub in codeCiSet)
        {
            csOrCodeSet.Add(codeSub.ToUpperInvariant());
            csOrCodeSet.Add(codeSub.ToLowerInvariant());
        }

        return (ciSet, csOrCodeSet);
    }

    internal static void AnalyzeSourceText(
        string rawSource,
        string relPath,
        bool isOcrEngineFile,
        bool isSuiteServiceFile,
        HashSet<string> caseInsensitiveSubstrings,
        HashSet<string> caseSensitiveOrCodeSubstrings,
        List<string> violations)
    {
        string stripped = StripCommentsPreservingNewlines(rawSource);
        string[] lines = stripped.Split('\n');
        string[] memberByLine = MapEnclosingMemberPerLine(lines);

        // 1. Scan interpolated strings FIRST (extract static fragments + combined static skeleton),
        //    then mask interpolated string spans before scanning regular/verbatim literals so C# expressions inside {...} are not misread as literals!
        var maskChars = stripped.ToCharArray();
        foreach (Match im in InterpolatedStringRegex.Matches(stripped))
        {
            int lineNo = GetLineNumberAtOffset(stripped, im.Index);
            string member = memberByLine[Math.Clamp(lineNo - 1, 0, memberByLine.Length - 1)];
            bool strictCi = isOcrEngineFile || (isSuiteServiceFile && IsOcrSuitePathOrMethod(relPath, member));
            HashSet<string> activeSet = strictCi ? caseInsensitiveSubstrings : caseSensitiveOrCodeSubstrings;

            string body = im.Groups[1].Success ? im.Groups[1].Value : im.Groups[2].Value;
            (List<string> segments, string combinedStatic) = ExtractInterpolatedStaticParts(body);
            foreach (string seg in segments)
            {
                CheckCandidateStringForForbiddenSubstrings(seg, relPath, lineNo, member, isOcrEngineFile, activeSet, violations);
            }

            CheckCandidateStringForForbiddenSubstrings(combinedStatic, relPath, lineNo, member, isOcrEngineFile, activeSet, violations);

            for (int k = im.Index; k < im.Index + im.Length && k < maskChars.Length; k++)
            {
                if (maskChars[k] != '\n')
                {
                    maskChars[k] = ' ';
                }
            }
        }

        string nonInterpolated = new(maskChars);
        string foldedConcat = nonInterpolated;
        while (AdjacentStringConcatRegex.IsMatch(foldedConcat))
        {
            foldedConcat = AdjacentStringConcatRegex.Replace(foldedConcat, m => $"\"{m.Groups[1].Value}{m.Groups[2].Value}\"");
        }

        // Scan regular, verbatim, and folded-concat string literals
        foreach (Match sm in RegularOrVerbatimStringRegex.Matches(foldedConcat))
        {
            int lineNo = GetLineNumberAtOffset(foldedConcat, sm.Index);
            string member = memberByLine[Math.Clamp(lineNo - 1, 0, memberByLine.Length - 1)];
            bool strictCi = isOcrEngineFile || (isSuiteServiceFile && IsOcrSuitePathOrMethod(relPath, member));
            HashSet<string> activeSet = strictCi ? caseInsensitiveSubstrings : caseSensitiveOrCodeSubstrings;

            string val = sm.Groups[1].Success ? sm.Groups[1].Value.Replace("\"\"", "\"", StringComparison.Ordinal) : sm.Groups[2].Value;
            CheckCandidateStringForForbiddenSubstrings(val, relPath, lineNo, member, isOcrEngineFile, activeSet, violations);
        }

        // 2. Scan sequences of >= 6 char literals within a sliding window of 3 lines
        for (int i = 0; i < lines.Length; i++)
        {
            string window = string.Join(' ', lines.Skip(i).Take(3));
            MatchCollection charMatches = CharLiteralRegex.Matches(window);
            if (charMatches.Count >= MinAnswerSubstringLength)
            {
                var chars = new StringBuilder(charMatches.Count);
                foreach (Match cm in charMatches)
                {
                    string token = cm.Value;
                    if (token.Length == 3 && token[1] != '\\')
                    {
                        chars.Append(token[1]);
                    }
                }

                string charSeq = chars.ToString();
                if (charSeq.Length >= MinAnswerSubstringLength)
                {
                    string member = memberByLine[i];
                    bool strictCi = isOcrEngineFile || (isSuiteServiceFile && IsOcrSuitePathOrMethod(relPath, member));
                    HashSet<string> activeSet = strictCi ? caseInsensitiveSubstrings : caseSensitiveOrCodeSubstrings;

                    for (int s = 0; s <= charSeq.Length - MinAnswerSubstringLength; s++)
                    {
                        string sub = charSeq.Substring(s, MinAnswerSubstringLength);
                        if (activeSet.Contains(sub) &&
                            !IsAllowedSubstring(relPath, member, sub))
                        {
                            violations.Add($"{relPath}:{i + 1} ({member}) contains benchmark char-sequence \"{sub}\"");
                            break;
                        }
                    }
                }
            }
        }

        // 3. Line-by-line structural checks (ROI Name branches in OCR paths, 3+ digit Width/Height branches in OCR engines, confidence floors)
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string member = memberByLine[i];
            bool isOcrSuiteMethod = isSuiteServiceFile && IsOcrSuitePathOrMethod(relPath, member);

            if ((isOcrEngineFile || isOcrSuiteMethod) && RoiNameBranchRegex.IsMatch(line))
            {
                violations.Add($"{relPath}:{i + 1} ({member}) contains forbidden ROI Name branch in OCR path: {line.Trim()}");
            }

            if (isOcrEngineFile && DimensionThreeDigitComparisonRegex.IsMatch(line))
            {
                string key = $"{relPath}::{member}";
                if (!AllowedOcrDimensionBoundsWhitelist.ContainsKey(key))
                {
                    violations.Add($"{relPath}:{i + 1} ({member}) contains unwhitelisted 3+ digit image/ROI dimension comparison: {line.Trim()}");
                }
            }

            if ((isOcrEngineFile || isOcrSuiteMethod) && ConfidenceFloorRegex.IsMatch(line))
            {
                violations.Add($"{relPath}:{i + 1} ({member}) contains artificial confidence floor: {line.Trim()}");
            }
        }
    }

    private static bool IsOcrSuitePathOrMethod(string relPath, string memberName)
    {
        return relPath.EndsWith("/PpOcrRoiAlgorithm.cs", StringComparison.OrdinalIgnoreCase) ||
               memberName.Contains("Ocr", StringComparison.OrdinalIgnoreCase);
    }

    private static void CheckCandidateStringForForbiddenSubstrings(
        string literalValue,
        string relPath,
        int lineNo,
        string member,
        bool isOcrEngineFile,
        HashSet<string> forbiddenSixCharSubstrings,
        List<string> violations)
    {
        if (string.IsNullOrEmpty(literalValue) || literalValue.Length < MinAnswerSubstringLength)
        {
            return;
        }

        if (GenericCatalogLines.Contains(literalValue))
        {
            return;
        }

        for (int s = 0; s <= literalValue.Length - MinAnswerSubstringLength; s++)
        {
            string sub = literalValue.Substring(s, MinAnswerSubstringLength);
            if (!forbiddenSixCharSubstrings.Contains(sub))
            {
                continue;
            }

            if (IsAllowedSubstring(relPath, member, sub))
            {
                continue;
            }

            violations.Add($"{relPath}:{lineNo} ({member}) contains benchmark answer substring \"{sub}\" inside literal \"{TruncateForReport(literalValue)}\"");
            return;
        }
    }

    private static bool IsAllowedSubstring(string relPath, string member, string sub)
    {
        string exactKey = $"{relPath}::{member}";
        if (AllowedUiAndNonOcrLiteralsByFileAndMember.TryGetValue(exactKey, out var entry))
        {
            StringComparison cmp = entry.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            foreach (string phrase in entry.AllowedPhrases)
            {
                if (phrase.Contains(sub, cmp))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static (List<string> Segments, string CombinedStatic) ExtractInterpolatedStaticParts(string rawBody)
    {
        var segments = new List<string>();
        var current = new StringBuilder();
        var combined = new StringBuilder();
        int depth = 0;
        for (int i = 0; i < rawBody.Length; i++)
        {
            char c = rawBody[i];
            if (c == '{')
            {
                if (i + 1 < rawBody.Length && rawBody[i + 1] == '{')
                {
                    current.Append('{');
                    combined.Append('{');
                    i++;
                    continue;
                }

                if (depth == 0 && current.Length > 0)
                {
                    segments.Add(current.ToString());
                    current.Clear();
                }

                depth++;
            }
            else if (c == '}')
            {
                if (i + 1 < rawBody.Length && rawBody[i + 1] == '}')
                {
                    current.Append('}');
                    combined.Append('}');
                    i++;
                    continue;
                }

                if (depth > 0)
                {
                    depth--;
                }
            }
            else if (depth == 0)
            {
                current.Append(c);
                combined.Append(c);
            }
        }

        if (current.Length > 0)
        {
            segments.Add(current.ToString());
        }

        return (segments, combined.ToString());
    }

    private static string StripCommentsPreservingNewlines(string src)
    {
        var sb = new StringBuilder(src.Length);
        bool inLineComment = false;
        bool inBlockComment = false;
        bool inString = false;
        bool inVerbatimString = false;
        bool inChar = false;

        for (int i = 0; i < src.Length; i++)
        {
            char c = src[i];
            char next = i + 1 < src.Length ? src[i + 1] : '\0';

            if (inLineComment)
            {
                if (c == '\n')
                {
                    inLineComment = false;
                    sb.Append('\n');
                }

                continue;
            }

            if (inBlockComment)
            {
                if (c == '*' && next == '/')
                {
                    inBlockComment = false;
                    i++;
                    continue;
                }

                if (c == '\n')
                {
                    sb.Append('\n');
                }

                continue;
            }

            if (inVerbatimString)
            {
                sb.Append(c);
                if (c == '"')
                {
                    if (next == '"')
                    {
                        sb.Append(next);
                        i++;
                    }
                    else
                    {
                        inVerbatimString = false;
                    }
                }

                continue;
            }

            if (inString)
            {
                sb.Append(c);
                if (c == '\\' && next != '\0')
                {
                    sb.Append(next);
                    i++;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (inChar)
            {
                sb.Append(c);
                if (c == '\\' && next != '\0')
                {
                    sb.Append(next);
                    i++;
                }
                else if (c == '\'')
                {
                    inChar = false;
                }

                continue;
            }

            if (c == '/' && next == '/')
            {
                inLineComment = true;
                i++;
                continue;
            }

            if (c == '/' && next == '*')
            {
                inBlockComment = true;
                i++;
                continue;
            }

            if (c == '@' && next == '"')
            {
                inVerbatimString = true;
                sb.Append(c);
                sb.Append(next);
                i++;
                continue;
            }

            if (c == '"')
            {
                inString = true;
                sb.Append(c);
                continue;
            }

            if (c == '\'')
            {
                inChar = true;
                sb.Append(c);
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static string[] MapEnclosingMemberPerLine(string[] lines)
    {
        var result = new string[lines.Length];
        string currentType = "Global";
        string currentMember = "Global";
        int braceDepth = 0;
        int memberStartDepth = -1;

        var typeDeclRegex = new Regex(@"\b(?:class|struct|record|interface|enum)\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);
        var methodOrPropRegex = new Regex(@"\b(?:public|private|protected|internal|static|async|virtual|override|sealed)\b[^;={]*?\b([A-Za-z_][A-Za-z0-9_]*)\s*(?:\(|=>|\{)", RegexOptions.Compiled);

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            Match tm = typeDeclRegex.Match(line);
            if (tm.Success)
            {
                currentType = tm.Groups[1].Value;
                if (memberStartDepth < 0)
                {
                    currentMember = currentType;
                }
            }

            if (braceDepth <= 2)
            {
                Match mm = methodOrPropRegex.Match(line);
                if (mm.Success)
                {
                    string candidate = mm.Groups[1].Value;
                    if (!string.Equals(candidate, currentType, StringComparison.Ordinal) &&
                        candidate is not ("if" or "for" or "foreach" or "while" or "switch" or "catch" or "using" or "return" or "new"))
                    {
                        currentMember = candidate;
                        memberStartDepth = braceDepth;
                    }
                }
            }

            result[i] = currentMember;

            foreach (char ch in line)
            {
                if (ch == '{')
                {
                    braceDepth++;
                }
                else if (ch == '}')
                {
                    braceDepth = Math.Max(0, braceDepth - 1);
                    if (memberStartDepth >= 0 && braceDepth <= memberStartDepth)
                    {
                        currentMember = currentType;
                        memberStartDepth = -1;
                    }
                }
            }
        }

        return result;
    }

    private static int GetLineNumberAtOffset(string text, int offset)
    {
        int line = 1;
        int max = Math.Min(offset, text.Length);
        for (int i = 0; i < max; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    private static bool IsBuildArtifactPath(string fullPath)
    {
        string norm = fullPath.Replace('\\', '/');
        return norm.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
               norm.Contains("/obj/", StringComparison.OrdinalIgnoreCase);
    }

    private static string TruncateForReport(string s)
    {
        return s.Length <= 48 ? s : s[..48] + "...";
    }
}
