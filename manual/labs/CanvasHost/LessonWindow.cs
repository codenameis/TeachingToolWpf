using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VisionCanvas.Controls;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Interaction;
using VisionCanvas.Rendering;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Manual;

/// <summary>소스·편집·입력·장면을 연결하는 작은 학습 호스트. 편집 규칙과 GPU 수명은 라이브러리에 둔다.</summary>
internal sealed class LessonWindow : Window
{
    private readonly EditorSession _session = new();
    private readonly Dx12Viewport _viewport = new();
    private readonly InteractionController _input;
    private readonly TextBlock _status = new() { Margin = new Thickness(8) };
    private ImageInfo? _image;
    private long _generation;
    private bool _closing;

    public LessonWindow(bool softwareRendering)
    {
        Title = "VisionCanvas 학습 호스트 · 8K 합성 영상";
        Width = 1100;
        Height = 760;
        _input = new InteractionController(_session);
        _viewport.Input = _input;
        if (softwareRendering)
        {
            _viewport.DevicePreference = RenderDevicePreference.Software;
        }

        var toolbar = new WrapPanel();
        AddButton(toolbar, "사각형 추가", AddRectangle);
        AddButton(toolbar, "선택 도구", () => _input.Tool = EditorTool.Select);
        AddButton(toolbar, "화면 이동", () => _input.Tool = EditorTool.Pan);
        AddButton(toolbar, "화면 맞춤", Fit);
        AddButton(toolbar, "삭제", _session.DeleteSelection);
        AddButton(toolbar, "Undo", _session.Undo);
        AddButton(toolbar, "Redo", _session.Redo);
        AddButton(toolbar, "취소", _input.Cancel);
        var layout = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        layout.Children.Add(toolbar);
        layout.Children.Add(_status);
        layout.Children.Add(_viewport);
        Content = layout;

        _session.Changed += (_, _) => Publish();
        _input.Changed += Publish;
        _viewport.LayoutChanged += Publish;
        _viewport.RendererStatusChanged += message => _status.Text = message;
        _viewport.InputFailed += error => _status.Text = error.Message;
        _viewport.Tiles.Failed += message => Dispatcher.BeginInvoke(() => _status.Text = message);
        _viewport.KeyAction = (key, _, _) =>
        {
            if (key != Key.Escape)
            {
                return false;
            }
            _input.Cancel();
            return true;
        };
        Loaded += (_, _) => InitializeImage();
        Closing += (_, _) =>
        {
            _closing = true;
            _input.Cancel();
        };
    }

    private void InitializeImage()
    {
        if (_image is not null)
        {
            return;
        }
        var source = new CheckerTileSource();
        var info = source.Info;
        _generation = _viewport.SetSource(source); // 이후 source는 viewport의 worker가 해제한다.
        _image = info;
        _session.InitializeImage(new ImageReference(info.Key, info.Name, info.Width, info.Height),
            ViewportTransform.Fit(info.Width, info.Height, _viewport.ViewWidth, _viewport.ViewHeight));
        AddRectangle();
    }

    private void AddRectangle()
    {
        if (_image is null)
        {
            return;
        }
        _input.Cancel();
        _session.Add(new RoiGeometry(new PointD(4096, 4096), 1600, 800, 15));
    }

    private void Fit()
    {
        if (_image is not null)
        {
            _session.SetViewport(ViewportTransform.Fit(_image.Width, _image.Height,
                _viewport.ViewWidth, _viewport.ViewHeight));
        }
    }

    private void Publish()
    {
        if (_image is null || _closing)
        {
            return;
        }
        _input.SetViewportSize(_viewport.ViewWidth, _viewport.ViewHeight);
        _viewport.Submit(new RenderScene(_session.State, _image, _generation,
            _viewport.ViewWidth, _viewport.ViewHeight, _viewport.Dpi,
            _input.Overlay, Transform: _session.TransformPreview));
    }

    private void AddButton(Panel toolbar, string text, Action action)
    {
        var button = new Button { Content = text, Margin = new Thickness(4), Padding = new Thickness(8, 4, 8, 4) };
        button.Click += (_, _) =>
        {
            try
            {
                _input.Cancel();
                action();
            }
            catch (Exception error)
            {
                _status.Text = error.Message;
            }
        };
        toolbar.Children.Add(button);
    }
}
