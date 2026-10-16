using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace AnomalyMonitor.Services;

public sealed class MonitorFiles(Window owner) : IMonitorFiles
{
    public string? OpenVideo()
    {
        var dialog = new OpenFileDialog { Title = "영상 파일 열기", Filter = "영상 파일|*.mp4;*.avi;*.mkv;*.mov;*.wmv|모든 파일|*.*" };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }
    public string? OpenModel()
    {
        var dialog = new OpenFileDialog { Title = "정상 모델 불러오기", Filter = "정상 모델|*.json" };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }
    public string? SaveModel()
    {
        var dialog = new SaveFileDialog { Title = "정상 모델 저장", Filter = "정상 모델|*.json", FileName = "normal-model.json" };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }
    public void ShowEvidence(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".jpg", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("증거 파일은 JPEG 이미지만 열 수 있습니다.");
        }
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("증거 이미지를 찾을 수 없습니다.");
        }

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}
