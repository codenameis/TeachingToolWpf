using System.ComponentModel;
using VisionCanvas.MvvmSample.Commands;

namespace VisionCanvas.MvvmSample.ViewModels;

// 두 카메라를 묶는 화면 책임만 맡는다. 각 카메라의 문서·입력·이력은 해당 EditorViewModel이 관리한다.
public sealed class InspectionModelViewModel : IDisposable
{
    private bool _disposed;
    public InspectionModelViewModel(string name, EditorViewModel firstCamera, EditorViewModel secondCamera)
    {
        Name = name;
        FirstCamera = firstCamera;
        SecondCamera = secondCamera;
        LoadCommand = new(async () =>
        {
            // 독립된 두 화면의 로딩이다. 한쪽 실패는 그 카메라의 Status에 남기며 다른 쪽을 숨기지 않는다.
            await Task.WhenAll(FirstCamera.LoadModelCommand.ExecuteAsync(), SecondCamera.LoadModelCommand.ExecuteAsync());
        }, () => !_disposed && FirstCamera.LoadModelCommand.CanExecute(null) && SecondCamera.LoadModelCommand.CanExecute(null),
            error => { FirstCamera.ReportError(error); SecondCamera.ReportError(error); });
        FirstCamera.PropertyChanged += CameraChanged;
        SecondCamera.PropertyChanged += CameraChanged;
    }
    public string Name { get; }
    public EditorViewModel FirstCamera { get; }
    public EditorViewModel SecondCamera { get; }
    public EditorCommand LoadCommand { get; }

    private void CameraChanged(object? sender, PropertyChangedEventArgs args) => LoadCommand.Refresh();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        FirstCamera.PropertyChanged -= CameraChanged;
        SecondCamera.PropertyChanged -= CameraChanged;
        FirstCamera.Dispose();
        SecondCamera.Dispose();
        LoadCommand.Refresh();
    }
}
