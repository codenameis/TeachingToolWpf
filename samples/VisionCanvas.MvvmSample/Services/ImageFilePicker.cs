using System.IO;
using System.Windows;
using Microsoft.Win32;
using VisionImaging.Abstractions;

namespace VisionCanvas.MvvmSample.Services;

// 파일 선택 약속을 실제 Windows 대화상자로 구현한다.
// owner를 전달하면 선택 창이 샘플 창에 소속되어, 뒤로 숨거나 다른 창과 혼동되는 일을 줄인다.
public sealed class ImageFilePicker(Window owner) : IImageFilePicker
{
    public ImageReference? Pick()
    {
        var dialog = new OpenFileDialog
        {
            Title = "이미지 열기",
            Filter = "이미지|*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.webp|모든 파일|*.*"
        };
        // '조건 ? 참일 때 값 : 거짓일 때 값' 문법이다. 취소하면 null을 돌려준다.
        // 여기서는 경로/이름/파일 버전만 얻는다. 너비·높이의 0은 아직 읽지 않았다는 표시다.
        // 실제 픽셀 읽기는 ImageCoordinator가 IImageLoader에 맡긴다.
        return dialog.ShowDialog(owner) == true
            ? new(dialog.FileName, Path.GetFileName(dialog.FileName), 0, 0, ImageFileVersion.Read(dialog.FileName))
            : null;
    }
}
