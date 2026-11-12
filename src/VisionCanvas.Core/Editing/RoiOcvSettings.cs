namespace VisionCanvas.Core.Editing;

/// <summary>
/// Per-ROI taught-font OCV settings stored in the document state and tracked by Undo/Redo.
/// </summary>
public sealed record RoiOcvSettings(
    string ExpectedText = "",
    string FormatMask = "",
    string FontId = RoiOcvSettings.DefaultFontId)
{
    public const string DefaultFontId = "default";

    public static RoiOcvSettings Empty { get; } = new();

    public static string NormalizeFontId(string? fontId)
        => string.IsNullOrWhiteSpace(fontId) ? DefaultFontId : fontId.Trim();
}
