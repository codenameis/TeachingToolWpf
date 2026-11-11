using System.Collections.Immutable;
using System.IO;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// Service contract for taught-font OCV font registration, persistence, and ROI print quality verification.
/// </summary>
public interface ITaughtFontOcvService
{
    bool HasGoldenFont { get; }

    int RegisteredCharacterCount { get; }

    string RegisteredCharactersSummary { get; }

    Task<OcvTeachingOutcome> TeachFromRoiAsync(
        ImageReference image,
        bool live,
        InspectionSuiteRoiInput roi,
        string expectedText,
        OcvPolaritySelection polarity,
        float foregroundContrastOffset,
        bool appendToExistingFont,
        CancellationToken token);

    Task<ImmutableArray<OcvRoiInspectionItem>> VerifyRoisAsync(
        ImageReference image,
        bool live,
        IReadOnlyList<OcvRoiVerifyRequest> requests,
        OcvPolaritySelection polarity,
        float foregroundContrastOffset,
        CancellationToken token);

    void SaveFontModel(Stream destination);

    void LoadFontModel(Stream source);

    void ClearFontModel();
}
