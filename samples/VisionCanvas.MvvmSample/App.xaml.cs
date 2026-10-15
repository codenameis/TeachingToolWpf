using System.IO;
using System.Windows;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Interaction;
using VisionCanvas.Controls;
using VisionCanvas.MvvmSample.Models;
using VisionCanvas.MvvmSample.Services;
using VisionCanvas.MvvmSample.ViewModels;
using VisionCanvas.MvvmSample.Views;
using VisionCanvas.Presentation;
using VisionImaging;

namespace VisionCanvas.MvvmSample;

// 여기서부터 읽으면 앱 전체의 연결 관계를 볼 수 있다.
// App은 객체들을 조립한다. 각 객체가 실제로 하는 일은 해당 클래스에 맡긴다.
// partial은 같은 클래스를 여러 파일에 나누어 정의한다는 뜻이다. WPF가 생성한 코드와 짝을 이룬다.
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // View는 보이는 창, editor는 편집 중인 문서와 Undo/Redo를 관리한다.
        // ROI는 이미지 위에서 선택·편집하는 관심 영역(사각형, 원 등)을 뜻한다.
        var window = new InspectionModelWindow();
        var definition = SampleInspectionModel.Create();
        // 큰 이미지에서 필요한 조각을 빠르게 읽기 위한 캐시다. 원본 파일을 덮어쓰지 않는다.
        var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VisionCanvas.MvvmSample", "ImageCache");
        // loader는 이미지를 읽고, Viewport는 표시하며, coordinator는 교체 순서와 소유권을 관리한다.
        // 이미 만든 객체를 생성자에 전달하는 방식이 '의존성 주입'이다.
        var loader = new DemoImageLoader(new ImageLoader(cache));
        var picker = new ImageFilePicker(window);
        var model = new InspectionModelViewModel(definition.Name,
            CreateCamera(definition.FirstCamera, window.FirstViewport),
            CreateCamera(definition.SecondCamera, window.SecondViewport));
        // 창의 LoadCommand는 이 VM에, 각 EditorView의 OpenCommand는 카메라 VM에 바인딩된다.
        window.DataContext = model;
        // 닫기 시작: 작업 취소와 이벤트 연결 해제. 닫은 뒤: 화면의 GPU/네이티브 자원 해제.
        // +=는 이벤트가 발생했을 때 실행할 함수를 연결한다. (_, _)는 사용하지 않는 전달값이다.
        window.Closing += (_, _) => model.Dispose();
        MainWindow = window;
        window.Show();

        EditorViewModel CreateCamera(CameraModel camera, Dx12Viewport viewport)
        {
            // 카메라마다 문서·입력·이력·표시 수명이 따로 있다. 공유하는 것은 상태 없는 파일 로더뿐이다.
            var editor = new EditorSession();
            // layout은 크기와 DPI만 전달하고, input은 마우스 동작을 같은 문서의 편집 요청으로 바꾼다.
            var layout = new ViewportLayout();
            var images = new ImageCoordinator(editor, viewport, layout, loader);
            return new(editor, new InteractionController(editor), layout, images, picker, camera);
        }
    }
}
