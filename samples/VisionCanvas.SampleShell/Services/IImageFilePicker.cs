using VisionImaging.Abstractions;

namespace VisionCanvas.SampleShell.Services;

public interface IImageFilePicker
{
    ImageReference? Pick();
}
