using VisionImaging.Abstractions;

namespace VisionCanvas.Presentation;

/// <summary>Image ownership and device operations, independent of a control or render scene.</summary>
public interface IImageDisplay
{
    /// <summary>Transfers source ownership on success. On failure the caller still owns it.</summary>
    long SetSource(ITileSource source);
    Task RestartAsync();
}
