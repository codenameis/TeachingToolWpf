namespace VisionCanvas.Core.Editing;

/// <summary>
/// Per-ROI taught-font OCV settings stored in the document state and tracked by Undo/Redo.
/// </summary>
public sealed record RoiOcvSettings(
    string ExpectedText = "",
    string FormatMask = "")
{
    public static RoiOcvSettings Empty { get; } = new();
}
