using System.Collections.Immutable;
using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using VisionCanvas.Controls;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionCanvas.Core.Interaction;
using VisionCanvas.Rendering;
using VisionGeometry;
using VisionImaging;
using VisionImaging.Abstractions;

namespace VisionCanvas.Sample;

internal sealed class SampleWindow : Window
{
    private readonly ImageLoader _loader;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly WrapPanel _toolbar = new();
    private readonly TextBlock _status = new() { Margin = new(8) };
    private readonly ListBox _rois = new() { SelectionMode = SelectionMode.Extended, DisplayMemberPath = nameof(RoiRow.Label) };
    private readonly TaskCompletionSource<string> _rendererReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<string> _rendererErrors = [];
    private ImmutableDictionary<Guid, Roi>? _rowSource;
    private ImageInfo? _image;
    private ResultOverlay? _overlay;
    private long _generation;
    private bool _busy;
    private bool _updatingRows;
    private bool _changingImage;

    private sealed record RoiRow(Guid Id, string Label);

    internal EditorSession Session { get; } = new();
    internal Dx12Viewport Viewport { get; } = new();
    internal InteractionController Input
    {
        get;
    }
    internal ObservedTileSource? Source
    {
        get; private set;
    }
    internal Task<string> RendererReady => _rendererReady.Task;
    internal IReadOnlyList<string> RendererErrors => _rendererErrors;

    internal SampleWindow(string[] args)
    {
        if (args.Contains("--software-rendering"))
        {
            Viewport.DevicePreference = RenderDevicePreference.Software;
        }
        Title = "VisionCanvas 독립 호스트";
        Width = 1280;
        Height = 820;
        MinWidth = 900;
        MinHeight = 600;
        var outputIndex = Array.IndexOf(args, "--output");
        var output = Path.GetFullPath(outputIndex >= 0 && outputIndex + 1 < args.Length
            ? args[outputIndex + 1] : "artifacts/vision-canvas-sample");
        var cache = args.Contains("--smoke") ? Path.Combine(output, "image-cache")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VisionCanvas.Sample", "ImageCache");
        _loader = new(cache);
        Input = new(Session);
        Viewport.Input = Input;
        Viewport.InputEnabled = () => !_busy;
        Viewport.KeyAction = HandleKey;
        Viewport.InputFailed += ShowError;
        Viewport.LayoutChanged += Publish;
        Viewport.RendererStatusChanged += RendererStatusChanged;
        Viewport.Tiles.Failed += message => Dispatcher.BeginInvoke(() => ShowError(new IOException(message)));
        Session.Changed += (_, _) => Publish();
        Input.Changed += Publish;
        _rois.SelectionChanged += (_, _) =>
        {
            if (!_updatingRows && !_busy)
            {
                Session.Select(_rois.SelectedItems.Cast<RoiRow>().Select(row => row.Id));
            }
        };
        BuildContent();
        PreviewKeyDown += (_, e) => e.Handled = HandleKey(e.Key,
            Keyboard.Modifiers.HasFlag(ModifierKeys.Control), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        Closing += (_, _) =>
        {
            Input.Cancel();
            _lifetime.Cancel();
        };
        Loaded += async (_, _) =>
        {
            if (args.Contains("--smoke"))
            {
                await SampleSmoke.RunAsync(this, output);
            }
            else
            {
                await RunAsync(() => LoadImageAsync(SampleReference(8192, 8192)));
            }
        };
    }

    private void BuildContent()
    {
        AddButton("파일 열기", OpenFileAsync);
        AddButton("8K 샘플", () => LoadImageAsync(SampleReference(8192, 8192)));
        AddButton("24K 샘플", () => LoadImageAsync(SampleReference(24576, 8192)));
        AddButton("맞춤", Fit);
        AddButton("+", () => Zoom(1.25));
        AddButton("−", () => Zoom(.8));
        AddButton("선택", () => Input.Tool = EditorTool.Select);
        AddButton("이동", () => Input.Tool = EditorTool.Pan);
        AddButton("사각형 추가", () => AddRoi(RoiShapeKind.Rectangle, false));
        AddButton("원 추가", () => AddRoi(RoiShapeKind.Circle, false));
        AddButton("폴리곤 그리기", BeginPolygon);
        AddButton("폴리곤 완료", Input.CompletePolygon);
        AddButton("취소", Input.Cancel);
        AddButton("자식 추가", () => AddRoi(RoiShapeKind.Rectangle, true));
        AddButton("회전 +15°", () => Session.RotateSelection(15, Session.State.SelectedSpatial.Bounds.Center));
        AddButton("삭제", Session.DeleteSelection);
        AddButton("Undo", () => HistoryAsync(false));
        AddButton("Redo", () => HistoryAsync(true));
        AddButton("결과 표시", () => ShowOverlay("외부에서 전달한 결과"));

        var root = new DockPanel();
        DockPanel.SetDock(_toolbar, Dock.Top);
        root.Children.Add(_toolbar);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);
        var panel = new DockPanel { Width = 220, Margin = new(8) };
        var help = new TextBlock
        {
            Text = "ROI 목록 · Ctrl로 다중 선택\n자식은 선택한 ROI에 연결됩니다.\n폴리곤: 점 클릭 후 Enter/완료\nEscape: 드래그·점 입력 취소",
            TextWrapping = TextWrapping.Wrap,
            Margin = new(0, 0, 0, 8)
        };
        DockPanel.SetDock(help, Dock.Top);
        panel.Children.Add(help);
        panel.Children.Add(_rois);
        DockPanel.SetDock(panel, Dock.Right);
        root.Children.Add(panel);
        root.Children.Add(Viewport);
        Content = root;
    }

    private void AddButton(string label, Action action) => AddButton(label, () =>
    {
        action();
        return Task.CompletedTask;
    });

    private void AddButton(string label, Func<Task> action)
    {
        var button = new Button { Content = label, Margin = new(3), Padding = new(8, 5, 8, 5) };
        button.Click += async (_, _) => await RunAsync(action);
        _toolbar.Children.Add(button);
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy)
        {
            return;
        }
        _busy = true;
        _toolbar.IsEnabled = false;
        try
        {
            await action();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            _busy = false;
            _toolbar.IsEnabled = true;
        }
    }

    private Task OpenFileAsync()
    {
        var dialog = new OpenFileDialog { Filter = "이미지|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff|모든 파일|*.*" };
        return dialog.ShowDialog(this) == true
            ? LoadImageAsync(new(dialog.FileName, Path.GetFileName(dialog.FileName), 0, 0, DiskPyramidSource.Version(dialog.FileName)))
            : Task.CompletedTask;
    }

    internal static ImageReference SampleReference(int width, int height)
        => new($"sample:{width}:{height}", $"{width} × {height}", width, height);

    internal async Task LoadImageAsync(ImageReference reference)
    {
        var source = await _loader.LoadAsync(reference, new Progress<string>(message => _status.Text = message), _lifetime.Token);
        var image = reference with
        {
            Name = source.Info.Name,
            Width = source.Info.Width,
            Height = source.Info.Height
        };
        InstallSource(source, () =>
        {
            var view = ViewportTransform.Fit(image.Width, image.Height, Viewport.ViewWidth, Viewport.ViewHeight);
            if (Session.State.Image is null)
            {
                Session.InitializeImage(image, view);
            }
            else
            {
                Session.SetImage(image, view);
            }
        });
    }

    internal async Task HistoryAsync(bool redo)
    {
        Input.Cancel();
        if (redo ? !Session.CanRedo : !Session.CanUndo)
        {
            return;
        }
        var target = redo ? Session.PreviewRedo() : Session.PreviewUndo();
        if (target.Image != Session.State.Image)
        {
            if (target.Image is null)
            {
                return;
            }
            // 복원할 원본을 독립적으로 연 뒤에만 이력 위치를 변경한다.
            var source = await _loader.LoadAsync(target.Image, new Progress<string>(message => _status.Text = message), _lifetime.Token);
            InstallSource(source, ApplyHistory);
        }
        else
        {
            ApplyHistory();
        }

        void ApplyHistory()
        {
            if (redo)
            {
                Session.Redo();
            }
            else
            {
                Session.Undo();
            }
        }
    }

    private void InstallSource(ITileSource source, Action updateDocument)
    {
        var observed = new ObservedTileSource(source);
        var transferred = false;
        Input.Cancel();
        _changingImage = true;
        try
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            updateDocument();
            _generation = Viewport.SetSource(observed);
            transferred = true;
            Source = observed;
            _image = observed.Info;
            _overlay = null;
        }
        finally
        {
            _changingImage = false;
            if (!transferred)
            {
                observed.Dispose();
            }
        }
        Publish();
    }

    private void AddRoi(RoiShapeKind kind, bool child)
    {
        if (Session.State.Image is not { } image)
        {
            return;
        }
        var size = Math.Min(image.Width, image.Height) / 6d;
        var center = new PointD(image.Width / 2d, image.Height / 2d);
        if (child)
        {
            if (Session.State.Selection.Count != 1)
            {
                throw new InvalidOperationException("자식을 추가할 부모 ROI 하나를 선택하세요.");
            }
            var parent = Session.State.Rois[Session.State.Selection.Single()];
            Session.AddChild(parent.Id, RoiGeometry.Create(kind, parent.Geometry.Center, parent.Geometry.Width / 3, parent.Geometry.Height / 3));
        }
        else
        {
            Session.Add(RoiGeometry.Create(kind, center, size, size));
        }
    }

    private void BeginPolygon()
    {
        Input.DrawParentId = null;
        Input.DrawShape = RoiShapeKind.Polygon;
        Input.Tool = EditorTool.Draw;
    }

    internal void Fit()
    {
        if (Session.State.Image is { } image)
        {
            Session.SetViewport(ViewportTransform.Fit(image.Width, image.Height, Viewport.ViewWidth, Viewport.ViewHeight));
        }
    }

    internal void Zoom(double factor) => Session.SetViewport(Session.State.Viewport.ZoomAt(new(Viewport.ViewWidth / 2, Viewport.ViewHeight / 2), factor));

    internal ResultOverlay ShowOverlay(string label)
    {
        var image = Session.State.Image ?? throw new InvalidOperationException("먼저 이미지를 열어 주세요.");
        var center = new PointD(image.Width * .6, image.Height * .5);
        var size = Math.Min(image.Width, image.Height) * .13;
        var outline = new RoiGeometry(center, size, size, 20).Outline.ToImmutableArray();
        var color = new Vector4(0, .9f, 1, 1);
        _overlay = new(label, color, [], [new(outline, center, 20 * Math.PI / 180, label, color, [center])], true)
        {
            BatchLabels = true
        };
        Publish();
        return _overlay;
    }

    private void Publish()
    {
        if (_changingImage || _lifetime.IsCancellationRequested)
        {
            return;
        }
        Input.SetViewportSize(Viewport.ViewWidth, Viewport.ViewHeight);
        Viewport.Submit(new(Session.State, _image, _generation, Viewport.ViewWidth, Viewport.ViewHeight, Viewport.Dpi,
            Input.Overlay, Transform: Session.TransformPreview, Inspection: _overlay));
        _updatingRows = true;
        try
        {
            if (!ReferenceEquals(_rowSource, Session.State.Rois))
            {
                _rowSource = Session.State.Rois;
                _rois.ItemsSource = _rowSource.Values.OrderBy(roi => roi.Order)
                    .Select(roi => new RoiRow(roi.Id, $"{(roi.ParentId is null ? "부모" : "↳ 자식")} · {roi.Name} · {roi.Geometry.ShapeName}")).ToArray();
            }
            _rois.SelectedItems.Clear();
            foreach (RoiRow row in _rois.Items)
            {
                if (Session.State.Selection.Contains(row.Id))
                {
                    _rois.SelectedItems.Add(row);
                }
            }
        }
        finally
        {
            _updatingRows = false;
        }
        _status.Text = $"{_image?.Name} · ROI {Session.State.Rois.Count} · {Session.State.Viewport.Scale:P0} · 도구 {Input.Tool}";
    }

    private bool HandleKey(Key key, bool control, bool shift)
    {
        if (_busy)
        {
            return false;
        }
        if (key == Key.Escape)
        {
            Input.Cancel();
        }
        else if (key == Key.Enter && Input.CanCompletePolygon)
        {
            Input.CompletePolygon();
        }
        else if (key == Key.Delete)
        {
            Session.DeleteSelection();
        }
        else if (control && key == Key.Z)
        {
            _ = RunAsync(() => HistoryAsync(shift));
        }
        else if (control && key == Key.Y)
        {
            _ = RunAsync(() => HistoryAsync(true));
        }
        else
        {
            return false;
        }
        return true;
    }

    private void RendererStatusChanged(string message)
    {
        if (message.StartsWith("DX12 ·", StringComparison.Ordinal))
        {
            _rendererReady.TrySetResult(message);
        }
        else if (message.StartsWith("렌더 오류:", StringComparison.Ordinal) || message.StartsWith("D3D12 ", StringComparison.Ordinal))
        {
            _rendererErrors.Add(message);
        }
        _status.Text = message;
    }

    private void ShowError(Exception error) => _status.Text = error.Message;
}
