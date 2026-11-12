using System.IO;
using System.Windows;
using Microsoft.Win32;
using VisionImaging.Abstractions;

namespace VisionCanvas.SampleShell.Services;

public sealed class ImageFilePicker : IImageFilePicker
{
    private const string DefaultDialogTitle = "이미지 열기";
    private const string DefaultFileFilter = "이미지|*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.webp|모든 파일|*.*";

    private readonly Window _owner;
    private readonly string _dialogTitle;

    public ImageFilePicker(Window owner, string dialogTitle = DefaultDialogTitle)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(dialogTitle);

        _owner = owner;
        _dialogTitle = dialogTitle;
    }

    public ImageReference? Pick()
    {
        var dialog = new OpenFileDialog
        {
            Title = _dialogTitle,
            Filter = DefaultFileFilter
        };

        return dialog.ShowDialog(_owner) == true
            ? new ImageReference(
                dialog.FileName,
                Path.GetFileName(dialog.FileName),
                0,
                0,
                ImageFileVersion.Read(dialog.FileName))
            : null;
    }
}
