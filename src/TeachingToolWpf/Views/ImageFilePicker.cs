using System.Windows;
using Microsoft.Win32;

namespace TeachingToolWpf.Views;

internal static class ImageFilePicker
{
    internal static string? Select(Window owner)
    {
        var dialog = new OpenFileDialog
        {
            Title = "배경 이미지 열기 · 대용량 이미지는 최초 캐시 생성이 필요합니다",
            Filter = "이미지|*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.webp|모든 파일|*.*"
        };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }
}
