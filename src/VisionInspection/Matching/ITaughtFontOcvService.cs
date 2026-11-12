using System.Collections.Immutable;
using System.IO;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// Service contract for taught-font OCV font registration, multi-font persistence, and ROI print quality verification.
/// </summary>
public interface ITaughtFontOcvService
{
    bool HasGoldenFont { get; }

    int RegisteredCharacterCount { get; }

    string RegisteredCharactersSummary { get; }

    bool HasGoldenFontForId(string? fontId);

    int GetRegisteredCharacterCount(string? fontId);

    string GetRegisteredCharactersSummary(string? fontId);

    Task<OcvTeachingOutcome> TeachFromRoiAsync(
        ImageReference image,
        bool live,
        InspectionSuiteRoiInput roi,
        string expectedText,
        OcvPolaritySelection polarity,
        float foregroundContrastOffset,
        bool appendToExistingFont,
        CancellationToken token,
        string fontId = "default");

    Task<ImmutableArray<OcvRoiInspectionItem>> VerifyRoisAsync(
        ImageReference image,
        bool live,
        IReadOnlyList<OcvRoiVerifyRequest> requests,
        OcvPolaritySelection polarity,
        float foregroundContrastOffset,
        CancellationToken token);

    void SaveFontModel(Stream destination, string fontId = "default");

    void LoadFontModel(Stream source, string fontId = "default");

    void ClearFontModel(string? fontId = null);

    IReadOnlyDictionary<string, string> ExportAllFontModelsJson();

    void ImportAllFontModelsJson(IReadOnlyDictionary<string, string>? fontModelsJson, bool clearExisting = true);
}
