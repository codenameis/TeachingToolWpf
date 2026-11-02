using System.IO;
using System.Windows;
using Microsoft.Win32;
using VisionImaging.Abstractions;

namespace VisionCanvas.WgpuSample.Services;

public interface IImageFilePicker
{
    ImageReference? Pick();
}

public sealed class ImageFilePicker(Window owner) : IImageFilePicker
{
    public ImageReference? Pick()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Wgpu Canvas · 이미지 열기",
            Filter = "이미지|*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.webp|모든 파일|*.*"
        };
        return dialog.ShowDialog(owner) == true
            ? new(dialog.FileName, Path.GetFileName(dialog.FileName), 0, 0, ImageFileVersion.Read(dialog.FileName)) : null;
    }
}

