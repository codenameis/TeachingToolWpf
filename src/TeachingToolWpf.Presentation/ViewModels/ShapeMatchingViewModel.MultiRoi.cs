using System.Collections.Immutable;
using System.Diagnostics;
using System.Numerics;
using System.Windows.Media;
using TeachingToolWpf.Models;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionCanvas.Core.Editing;
using VisionCanvas.Rendering;
using VisionGeometry;

namespace TeachingToolWpf.ViewModels;

public sealed partial class ShapeMatchingViewModel
{
    private ImageSource? ResolvePatternThumbnailForRoi(Guid? roiId)
    {
        if (roiId is { } id)
        {
            var matched = ActiveMethodPatterns.LastOrDefault(p => p.TeachingRoiId == id);
            if (matched?.ThumbnailImage is { } roiThumb)
            {
                return roiThumb;
            }
        }

        return _selectedRegisteredPattern?.ThumbnailImage;
    }

    private void RecordTrainedRoiAssignment(Guid roiId, string? roiName)
    {
        string resolvedName = string.IsNullOrWhiteSpace(roiName) ? $"ROI-{roiId.ToString()[..4]}" : roiName;
        var badge = RoiAlgorithmAssignment.FormatAlgorithmBadge(InspectionToolKind.Pattern, Method);
        var accent = RoiAlgorithmAssignment.GetAlgorithmAccent(InspectionToolKind.Pattern, Method);
        var patternItem = ActiveMethodPatterns.LastOrDefault(p => p.TeachingRoiId == roiId) ?? _selectedRegisteredPattern;
        var roleTag = patternItem?.IsDefect == true ? "[불량(NG)]" : "[정상(OK)]";
        RoiAssignmentRecorded?.Invoke(new RoiAlgorithmAssignment(
            RoiId: roiId,
            RoiName: resolvedName,
            Tool: InspectionToolKind.Pattern,
            PatternMethod: Method,
            SuiteAlgorithm: null,
            AlgorithmBadge: badge,
            AlgorithmAccent: accent,
            ParameterSummary: $"최소 점수 {MinimumScore:F2} · 최대 결과 {MaximumMatches}개",
            VerdictBadge: "학습됨",
            VerdictAccent: "#38BDF8",
            LastResultSummary: $"{badge} {roleTag} 학습 완료 · 검사 대기",
            PatternThumbnail: patternItem?.ThumbnailImage));
    }

    private void RecordSingleRoiPatternResult(
        Guid? roiId,
        string? roiName,
        RoiGeometry? searchRegion,
        InspectionVerdict verdict,
        int matchCount,
        double topScore,
        string poseSummary,
        double elapsedMilliseconds)
    {
        var badge = RoiAlgorithmAssignment.FormatAlgorithmBadge(InspectionToolKind.Pattern, Method);
        var accent = RoiAlgorithmAssignment.GetAlgorithmAccent(InspectionToolKind.Pattern, Method);
        var (verdictBadge, verdictAccent) = FormatVerdictBadgeAndAccent(verdict, matchCount);
        string resolvedName = string.IsNullOrWhiteSpace(roiName)
            ? (roiId is { } id ? $"ROI-{id.ToString()[..4]}" : "전체 영상")
            : roiName;
        string scoreSummary = matchCount > 0 ? $"점수 {topScore:F3} ({matchCount}개)" : "미검출 (0개)";
        BoundsD? bounds = searchRegion is not null ? BoundsD.FromPoints(searchRegion.Outline) : null;

        if (roiId is { } targetId)
        {
            RoiAssignmentRecorded?.Invoke(new RoiAlgorithmAssignment(
                RoiId: targetId,
                RoiName: resolvedName,
                Tool: InspectionToolKind.Pattern,
                PatternMethod: Method,
                SuiteAlgorithm: null,
                AlgorithmBadge: badge,
                AlgorithmAccent: accent,
                ParameterSummary: $"최소 점수 {MinimumScore:F2} · 최대 결과 {MaximumMatches}개",
                VerdictBadge: verdictBadge,
                VerdictAccent: verdictAccent,
                LastResultSummary: $"{verdictBadge} · {scoreSummary} · {poseSummary} ({elapsedMilliseconds:F1} ms)",
                PatternThumbnail: ResolvePatternThumbnailForRoi(targetId)));
        }

        StructuredLogRecorded?.Invoke(new StructuredInspectionLogEntry(
            EntryId: Guid.NewGuid(),
            Timestamp: DateTimeOffset.Now,
            RoiId: roiId,
            RoiName: resolvedName,
            Tool: InspectionToolKind.Pattern,
            AlgorithmBadge: badge,
            Verdict: verdict,
            VerdictBadge: verdictBadge,
            VerdictAccent: verdictAccent,
            ScoreSummary: scoreSummary,
            PoseSummary: poseSummary,
            ElapsedMilliseconds: elapsedMilliseconds,
            TargetBounds: bounds));
    }

    private static (string Badge, string Accent) FormatVerdictBadgeAndAccent(InspectionVerdict verdict, int matchCount) => verdict switch
    {
        InspectionVerdict.Pass => ("PASS", "#34D399"),
        InspectionVerdict.Fail => ("NG", "#F87171"),
        InspectionVerdict.Warning => ("WARN", "#FBBF24"),
        InspectionVerdict.Review => matchCount > 0 ? ("PASS", "#34D399") : ("NG", "#F87171"),
        _ => ("WAIT", "#94A3B8")
    };

    private async Task SearchMultipleRoisAsync(long startedTimestamp)
    {
        var image = _image!;
        var targets = _state is not null
            ? InspectionSelection.ResolveTargetLeafRois(_state, _lastTransformPreview)
            : [];
        var method = Method;
        var live = _live;

        await RunAsync(async (token, epoch) =>
        {
            Status = $"대상 리프 ROI {targets.Length}개 개별 패턴 검사 중…";
            var allMarkers = ImmutableArray.CreateBuilder<MatchMarker>();
            var rowStrings = new List<string>(targets.Length);
            int passCount = 0;
            int failCount = 0;
            int warnCount = 0;
            double totalMs = 0.0;

            for (int i = 0; i < targets.Length; i++)
            {
                var target = targets[i];
                var roiId = target.RoiId;
                var roiName = string.IsNullOrWhiteSpace(target.RoiName)
                    ? (roiId is { } id ? $"ROI-{id.ToString()[..4]}" : $"ROI #{i + 1}")
                    : target.RoiName!;
                var searchRegion = target.SearchSelection ?? target.Selection;
                var exclusion = target.Exclusion;

                if (IsPatMaxMethod && _patMaxState.Template is { } patMaxTemplate)
                {
                    var searchParams = _patMaxState.Settings.ToSearchParams(MinimumScore, MaximumMatches);
                    var output = await _patMaxService!.SearchAsync(image, live, patMaxTemplate, searchRegion, searchParams, token, exclusion);
                    if (!Current(epoch, token, method))
                    {
                        return;
                    }
                    totalMs += output.TotalMilliseconds;
                    var roiVerdict = output.MatchLimitExceeded
                        ? InspectionVerdict.Warning
                        : output.Matches.IsEmpty ? InspectionVerdict.Fail : InspectionVerdict.Pass;
                    AccumulateVerdict(roiVerdict, ref passCount, ref failCount, ref warnCount);
                    var color = InspectionResultPresentation.Color(roiVerdict);
                    var rawPoints = patMaxTemplate.PreviewPoints;
                    foreach (var match in output.Matches)
                    {
                        var rad = match.AngleDegrees * Math.PI / 180.0;
                        var cos = Math.Cos(rad) * match.Scale;
                        var sin = Math.Sin(rad) * match.Scale;
                        var features = rawPoints.IsDefaultOrEmpty
                            ? []
                            : rawPoints.Select(p => new PointD(match.X + (cos * p.X) - (sin * p.Y), match.Y + (sin * p.X) + (cos * p.Y))).ToImmutableArray();
                        allMarkers.Add(new MatchMarker(
                            new RoiGeometry(new(match.X, match.Y), patMaxTemplate.TeachingRegion.Width * match.Scale,
                                patMaxTemplate.TeachingRegion.Height * match.Scale * match.AspectRatio, match.AngleDegrees).Outline.ToImmutableArray(),
                            new(match.X, match.Y),
                            rad,
                            $"{roiName}: {match.Score:F3}",
                            color,
                            features));
                    }
                    if (output.Matches.IsEmpty && searchRegion is not null)
                    {
                        allMarkers.Add(new MatchMarker(
                            searchRegion.Outline.ToImmutableArray(),
                            searchRegion.Center,
                            searchRegion.Angle * Math.PI / 180.0,
                            $"{roiName}: NG (미검출)",
                            color,
                            []));
                    }
                    var topScore = output.Matches.IsEmpty ? 0.0 : output.Matches[0].Score;
                    var poseSummary = output.Matches.IsEmpty
                        ? "검출 없음"
                        : $"X={output.Matches[0].X:F2}, Y={output.Matches[0].Y:F2}, θ={output.Matches[0].AngleDegrees:F2}°";
                    rowStrings.Add($"[{roiName}] {(output.Matches.IsEmpty ? "NG (미검출)" : $"PASS ({output.Matches.Length}개)")} · 최고점수 {topScore:F3} · {poseSummary} ({output.TotalMilliseconds:F1} ms)");
                    RecordSingleRoiPatternResult(roiId, roiName, searchRegion, roiVerdict, output.Matches.Length, topScore, poseSummary, output.TotalMilliseconds);
                }
                else if (IsCpuMethod && ActiveCpuState.Template is { } cpuTemplate)
                {
                    var options = CreateCpuOptions();
                    var useCuda = ComputeMode == InspectionComputeMode.GpuPreprocessing;
                    var output = await _cpuService!.SearchAsync(image, live, cpuTemplate, searchRegion, options, token, exclusion, useCuda);
                    if (!Current(epoch, token, method))
                    {
                        return;
                    }
                    totalMs += output.TotalMilliseconds;
                    var result = output.Result;
                    var roiVerdict = (result.CandidatesTruncated || result.MatchLimitExceeded)
                        ? InspectionVerdict.Warning
                        : result.Matches.Count == 0 ? InspectionVerdict.Fail : InspectionVerdict.Pass;
                    AccumulateVerdict(roiVerdict, ref passCount, ref failCount, ref warnCount);
                    var color = InspectionResultPresentation.Color(roiVerdict);
                    var rawPoints = cpuTemplate.Matcher.PreviewPoints;
                    foreach (var match in result.Matches)
                    {
                        var rad = match.AngleDegrees * Math.PI / 180.0;
                        var cos = Math.Cos(rad) * match.Scale;
                        var sin = Math.Sin(rad) * match.Scale;
                        var features = rawPoints.Count == 0
                            ? []
                            : rawPoints.Select(p => new PointD(match.X + (cos * p.X) - (sin * p.Y), match.Y + (sin * p.X) + (cos * p.Y))).ToImmutableArray();
                        allMarkers.Add(new MatchMarker(
                            new RoiGeometry(new(match.X, match.Y), cpuTemplate.TeachingRegion.Width * match.Scale,
                                cpuTemplate.TeachingRegion.Height * match.Scale, match.AngleDegrees).Outline.ToImmutableArray(),
                            new(match.X, match.Y),
                            rad,
                            $"{roiName}: {match.Score:F3}",
                            color,
                            features));
                    }
                    if (result.Matches.Count == 0 && searchRegion is not null)
                    {
                        allMarkers.Add(new MatchMarker(
                            searchRegion.Outline.ToImmutableArray(),
                            searchRegion.Center,
                            searchRegion.Angle * Math.PI / 180.0,
                            $"{roiName}: NG (미검출)",
                            color,
                            []));
                    }
                    var topScore = result.Matches.Count == 0 ? 0.0 : result.Matches[0].Score;
                    var poseSummary = result.Matches.Count == 0
                        ? "검출 없음"
                        : $"X={result.Matches[0].X:F2}, Y={result.Matches[0].Y:F2}, θ={result.Matches[0].AngleDegrees:F2}°";
                    rowStrings.Add($"[{roiName}] {(result.Matches.Count == 0 ? "NG (미검출)" : $"PASS ({result.Matches.Count}개)")} · 최고점수 {topScore:F3} · {poseSummary} ({output.TotalMilliseconds:F1} ms)");
                    RecordSingleRoiPatternResult(roiId, roiName, searchRegion, roiVerdict, result.Matches.Count, topScore, poseSummary, output.TotalMilliseconds);
                }
                else if (!IsShapeMethod && _templateState.Model is { } templateModel && _templateState.TeachingRegion is { } tplTeachingRegion)
                {
                    var options = CreateTemplateSearchOptions();
                    var output = await _templateService!.SearchAsync(image, live, templateModel, searchRegion, options, token, exclusion);
                    if (!Current(epoch, token, method))
                    {
                        return;
                    }
                    totalMs += output.TotalMilliseconds;
                    var decision = InspectionDecision.Evaluate(output);
                    var roiVerdict = output.Matches.IsEmpty ? InspectionVerdict.Fail : decision.Verdict == InspectionVerdict.Review ? InspectionVerdict.Pass : decision.Verdict;
                    AccumulateVerdict(roiVerdict, ref passCount, ref failCount, ref warnCount);
                    var color = InspectionResultPresentation.Color(roiVerdict);
                    foreach (var marker in InspectionResultPresentation.Markers(tplTeachingRegion, output))
                    {
                        allMarkers.Add(marker with { Label = $"{roiName}: {marker.Label}", Color = color });
                    }
                    if (output.Matches.IsEmpty && searchRegion is not null)
                    {
                        allMarkers.Add(new MatchMarker(
                            searchRegion.Outline.ToImmutableArray(),
                            searchRegion.Center,
                            searchRegion.Angle * Math.PI / 180.0,
                            $"{roiName}: NG (미검출)",
                            color,
                            []));
                    }
                    var topScore = output.Matches.IsEmpty ? 0.0 : output.Matches[0].Score;
                    var poseSummary = output.Matches.IsEmpty
                        ? "검출 없음"
                        : $"X={output.Matches[0].ReferencePoint.X:F2}, Y={output.Matches[0].ReferencePoint.Y:F2}";
                    rowStrings.Add($"[{roiName}] {(output.Matches.IsEmpty ? "NG (미검출)" : $"PASS ({output.Matches.Length}개)")} · 최고점수 {topScore:F3} · {poseSummary} ({output.TotalMilliseconds:F1} ms)");
                    RecordSingleRoiPatternResult(roiId, roiName, searchRegion, roiVerdict, output.Matches.Length, topScore, poseSummary, output.TotalMilliseconds);
                }
                else if (IsShapeMethod && ActiveShapeState.Model is { } shapeModel && ActiveShapeState.TeachingRegion is { } shapeTeachingRegion)
                {
                    var options = CreateShapeSearchOptions();
                    var rotation = ActiveRotationAcceptance;
                    var output = await _service.SearchAsync(image, live, shapeModel, searchRegion, options, token, exclusion);
                    if (!Current(epoch, token, method))
                    {
                        return;
                    }
                    totalMs += output.TotalMilliseconds;
                    var decision = InspectionDecision.Evaluate(output, rotation, shapeModel.TrainingReferenceAngle);
                    var roiVerdict = output.Matches.IsEmpty ? InspectionVerdict.Fail : decision.Verdict == InspectionVerdict.Review ? InspectionVerdict.Pass : decision.Verdict;
                    AccumulateVerdict(roiVerdict, ref passCount, ref failCount, ref warnCount);
                    var color = InspectionResultPresentation.Color(roiVerdict);
                    foreach (var marker in InspectionResultPresentation.Markers(shapeModel, shapeTeachingRegion, output, rotation))
                    {
                        allMarkers.Add(marker with { Label = $"{roiName}: {marker.Label}" });
                    }
                    if (output.Matches.IsEmpty && searchRegion is not null)
                    {
                        allMarkers.Add(new MatchMarker(
                            searchRegion.Outline.ToImmutableArray(),
                            searchRegion.Center,
                            searchRegion.Angle * Math.PI / 180.0,
                            $"{roiName}: NG (미검출)",
                            color,
                            []));
                    }
                    var topScore = output.Matches.IsEmpty ? 0.0 : output.Matches[0].Score;
                    var poseSummary = output.Matches.IsEmpty
                        ? "검출 없음"
                        : $"X={output.Matches[0].Pose.X:F2}, Y={output.Matches[0].Pose.Y:F2}, θ={RoiGeometry.NormalizeAngle(output.Matches[0].Pose.Angle * 180 / Math.PI):F2}°";
                    rowStrings.Add($"[{roiName}] {(roiVerdict == InspectionVerdict.Pass ? $"PASS ({output.Matches.Length}개)" : roiVerdict == InspectionVerdict.Warning ? "WARN" : "NG")} · 최고점수 {topScore:F3} · {poseSummary} ({output.TotalMilliseconds:F1} ms)");
                    RecordSingleRoiPatternResult(roiId, roiName, searchRegion, roiVerdict, output.Matches.Length, topScore, poseSummary, output.TotalMilliseconds);
                }
            }

            Verdict = failCount > 0
                ? InspectionVerdict.Fail
                : warnCount > 0
                    ? InspectionVerdict.Warning
                    : InspectionVerdict.Pass;
            Vector4 summaryColor = InspectionResultPresentation.Color(Verdict.Value);
            string summaryHeader = $"개별 ROI 패턴 검사 ({targets.Length}개) · PASS {passCount} / NG {failCount} / WARN {warnCount} · 총 {totalMs:F1} ms";
            _inspectionNotice = summaryHeader;
            _result = new ResultOverlay(
                summaryHeader,
                summaryColor,
                SearchBoundary(image, null),
                allMarkers.ToImmutable(),
                ShowFeatures)
            {
                BatchLabels = true
            };
            SetResultDetails(summaryHeader, rowStrings);
            Status = summaryHeader;
            PublishResult();
        });
    }

    private static void AccumulateVerdict(InspectionVerdict verdict, ref int passCount, ref int failCount, ref int warnCount)
    {
        switch (verdict)
        {
            case InspectionVerdict.Pass:
            case InspectionVerdict.Review:
                passCount++;
                break;
            case InspectionVerdict.Warning:
                warnCount++;
                break;
            default:
                failCount++;
                break;
        }
    }
}
