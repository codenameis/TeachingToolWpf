using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VisionCanvas.Core.Editing;
using VisionCanvas.Layers;
using VisionCanvas.Rendering;
using VisionGeometry;
using VisionImaging;
using VisionImaging.Abstractions;

namespace VisionAnomaly.Controls;

public sealed record VideoAnnotation(Rect Bounds, string Label);

/// <summary>알고리즘을 모르는 표시 컨트롤. 전달받은 프레임과 같은 프레임의 표시 자료만 함께 게시한다.</summary>
public partial class CameraMonitorControl : UserControl, IDisposable
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(CameraMonitorControl), new PropertyMetadata("카메라"));
    public static readonly DependencyProperty StatusTextProperty = DependencyProperty.Register(nameof(StatusText), typeof(string), typeof(CameraMonitorControl), new PropertyMetadata("연결 안 됨"));
    public static readonly DependencyProperty DetailTextProperty = DependencyProperty.Register(nameof(DetailText), typeof(string), typeof(CameraMonitorControl), new PropertyMetadata(""));
    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(nameof(IsSelected), typeof(bool), typeof(CameraMonitorControl), new PropertyMetadata(false));
    public static readonly DependencyProperty SelectCommandProperty = DependencyProperty.Register(nameof(SelectCommand), typeof(ICommand), typeof(CameraMonitorControl));
    public string Title
    {
        get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value);
    }
    public string StatusText
    {
        get => (string)GetValue(StatusTextProperty); set => SetValue(StatusTextProperty, value);
    }
    public string DetailText
    {
        get => (string)GetValue(DetailTextProperty); set => SetValue(DetailTextProperty, value);
    }
    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty); set => SetValue(IsSelectedProperty, value);
    }
    public ICommand? SelectCommand
    {
        get => (ICommand?)GetValue(SelectCommandProperty); set => SetValue(SelectCommandProperty, value);
    }

    private readonly object _sync = new();
    private VideoFrame? _pending;
    private IReadOnlyList<VideoAnnotation> _annotations = [];
    private Rect? _region;
    private bool _scheduled, _disposed;
    private ImageInfo? _info;
    private long _generation;
    public long DisplayedSequence
    {
        get; private set;
    }
    public event Action<string>? RendererStatusChanged;
    public VisionCanvas.Controls.Dx12Viewport Viewport => Surface;

    public CameraMonitorControl()
    {
        InitializeComponent();
        Surface.LayoutChanged += UpdateScene;
        Surface.RendererStatusChanged += OnRendererStatus;
    }

    private void OnRendererStatus(string status) => RendererStatusChanged?.Invoke(status);

    /// <summary>frame의 소유권을 받는다. UI 대기 중 이전 프레임은 반환하고 최신 하나만 보관한다.</summary>
    public void Present(VideoFrame frame, IReadOnlyList<VideoAnnotation> annotations, Rect? region = null)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                frame.Dispose();
                return;
            }
            _pending?.Dispose();
            _pending = frame;
            _annotations = annotations;
            _region = region;
            if (_scheduled)
            {
                return;
            }

            _scheduled = true;
        }
        Dispatcher.BeginInvoke(Drain);
    }

    private void Drain()
    {
        VideoFrame? frame;
        IReadOnlyList<VideoAnnotation> annotations;
        Rect? region;
        lock (_sync)
        {
            frame = _pending;
            _pending = null;
            _scheduled = false;
            annotations = _annotations;
            region = _region;
        }
        if (frame is null)
        {
            return;
        }

        try
        {
            if (_disposed)
            {
                return;
            }

            if (_info is null || _info.Width != frame.Pixels.Width || _info.Height != frame.Pixels.Height)
            {
                var reference = new ImageReference("live:Bgra32", Title, frame.Pixels.Width, frame.Pixels.Height);
                var source = new LiveImageSource(reference);
                _info = source.Info;
                _generation = Surface.SetSource(source);
                Surface.Visibility = Visibility.Visible;
                EmptyMessage.Visibility = Visibility.Collapsed;
                UpdateScene();
            }
            var graphics = new List<CanvasGraphic>();
            if (region is { Width: > 0, Height: > 0 } box)
            {
                graphics.Add(new ShapeGraphic(Geometry(box), new(.55f, .87f, .74f, 1), 1));
            }

            foreach (var annotation in annotations)
            {
                graphics.Add(new ShapeGraphic(Geometry(annotation.Bounds), new(1, .49f, .39f, 1), 2));
            }
            Surface.Layers["Inspection"].ReplaceAll(graphics);
            DisplayedSequence = frame.Sequence;
            Surface.PublishFrame(frame.Retain());
        }
        finally { frame.Dispose(); }
    }

    private static RoiGeometry Geometry(Rect bounds) => new(new(bounds.X + (bounds.Width / 2), bounds.Y + (bounds.Height / 2)), bounds.Width, bounds.Height, 0);

    private void UpdateScene()
    {
        if (_info is null || _disposed || Surface.ViewWidth <= 0 || Surface.ViewHeight <= 0)
        {
            return;
        }

        var document = DocumentState.Empty with
        {
            Image = new(_info.Key, _info.Name, _info.Width, _info.Height),
            Viewport = ViewportTransform.Fit(_info.Width, _info.Height, Surface.ViewWidth, Surface.ViewHeight)
        };
        Surface.Scene = new(document, _info, _generation, Surface.ViewWidth, Surface.ViewHeight, Surface.Dpi, new(), IsLive: true);
    }

    public void Clear()
    {
        if (_disposed)
        {
            return;
        }
        lock (_sync)
        {
            _pending?.Dispose();
            _pending = null;
        }
        Surface.ClearFrames();
        Surface.Layers["Inspection"].Clear();
        Surface.Visibility = Visibility.Collapsed;
        EmptyMessage.Visibility = Visibility.Visible;
        _info = null;
        DisplayedSequence = 0;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pending?.Dispose();
            _pending = null;
        }
        Surface.LayoutChanged -= UpdateScene;
        Surface.RendererStatusChanged -= OnRendererStatus;
        Surface.Dispose();
    }
}
