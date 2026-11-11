using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TeachingToolWpf.Fluent2.Tests.OcrBenchmark;

/// <summary>
/// Defines a physical print group (identical printing mechanism, typeface, polarity, and equipment)
/// in <c>labels.json</c> (schemaVersion 2) so OCV golden teaching and verification never mix fonts across groups.
/// </summary>
public sealed record OcrPrintGroupSpec(
    [property: JsonPropertyName("groupId")] string GroupId,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("printMethod")] string PrintMethod,
    [property: JsonPropertyName("darkOnLight")] bool DarkOnLight,
    [property: JsonPropertyName("dotClosingRadiusX")] int DotClosingRadiusX,
    [property: JsonPropertyName("dotClosingRadiusY")] int DotClosingRadiusY,
    [property: JsonPropertyName("rationale")] string Rationale,
    [property: JsonPropertyName("teachEntryIds")] IReadOnlyList<string> TeachEntryIds,
    [property: JsonPropertyName("verifyEntryIds")] IReadOnlyList<string> VerifyEntryIds,
    [property: JsonPropertyName("perturbedCandidateEntryIds")] IReadOnlyList<string> PerturbedCandidateEntryIds,
    [property: JsonPropertyName("realIndependentNotRunReason")] string? RealIndependentNotRunReason = null,
    [property: JsonPropertyName("perturbedNotRunReason")] string? PerturbedNotRunReason = null,
    [property: JsonPropertyName("ocvSpecimens")] IReadOnlyList<OcrOcvSpecimenEntry>? OcvSpecimens = null);
