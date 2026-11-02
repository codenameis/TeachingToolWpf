using System.Globalization;
using System.Windows.Input;
using TeachingToolWpf.Commands;
using TeachingToolWpf.Infrastructure;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Interaction;
using VisionCanvas.Diagnostics;
using VisionCanvas.Presentation;
using VisionCanvas.Rendering;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.ViewModels;

public sealed class RepeatedRoiViewModel : ObservableObject, IDisposable
{
    private const int DefaultSideCount = 101;
    private readonly IImageDisplay _display;
    private readonly ImageReference _initialImage;
    private readonly Guid[] _cellIds;
    private readonly double _pitch;
    private readonly CancellationTokenSource _loading = new();
    private bool _disposed;
    private ImageInfo? _image;
    private long _generation;
    private string _status = "준비 중…";
    private string _performance = "";

    public ViewportLayout Layout { get; }
    public RenderScene? Scene { get; private set; }
    public Func<Key, bool, bool, bool> KeyHandler => HandleKey;
    public string Adapter { get; private set; } = "";
    public EditorSession Session { get; } = new();
    public InteractionController Input { get; }
    public int Columns => DefaultSideCount;
    public int Rows => DefaultSideCount;
    public int Count => _cellIds.Length;
    public double Pitch => _pitch;
    public ImageReference Image => _initialImage;
    public Guid? SelectedId => Session.State.SelectionCount == 1 ? Session.State.Selection.Single() : null;
    public int? SelectedIndex => SelectedId is { } id ? Array.IndexOf(_cellIds, id) switch { >= 0 and var idx => idx, _ => null } : null;
    public RoiGeometry SelectionGeometry => SelectedId is { } id && Session.State.Rois.TryGetValue(id, out var roi)
        ? roi.Geometry
        : new(Session.State.SelectedSpatial.Bounds.Center, Math.Max(1, Session.State.SelectedSpatial.Bounds.Width), Math.Max(1, Session.State.SelectedSpatial.Bounds.Height), 0);
    public string CountText => $"{Session.State.RoiCount:N0}개 ROI · {Columns:N0} × {Rows:N0}";
    public string SelectionText => SelectedIndex is { } index
        ? $"선택: 행 {(index / Columns) + 1:N0}, 열 {(index % Columns) + 1:N0}"
        : $"전체 {Session.State.SelectionCount:N0}개 선택";
    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }
    public string Performance
    {
        get => _performance;
        set => Set(ref _performance, value);
    }
    public string X { get; set; } = "";
    public string Y { get; set; } = "";
    public string Width { get; set; } = "";
    public string Height { get; set; } = "";
    public string Angle { get; set; } = "";
    public string Row { get; set; } = "51";
    public string Column { get; set; } = "51";
    public RelayCommand SelectAll { get; }
    public RelayCommand SelectCell { get; }
    public RelayCommand Fit { get; }
    public RelayCommand Detail { get; }
    public RelayCommand Undo { get; }
    public RelayCommand Redo { get; }
    public RelayCommand RotateLeft { get; }
    public RelayCommand RotateRight { get; }
    public RelayCommand MoveLeft { get; }
    public RelayCommand MoveRight { get; }
    public RelayCommand MoveUp { get; }
    public RelayCommand MoveDown { get; }
    public RelayCommand Apply { get; }
    public RelayCommand Pan { get; }
    public RelayCommand Select { get; }

    public RepeatedRoiViewModel(IImageDisplay display, ViewportLayout layout, ImageReference image)
    {
        _display = display;
        _initialImage = image;
        Layout = layout;
        Input = new(Session);
        Session.InitializeImage(image, ViewportTransform.Identity);
        _pitch = Math.Min(image.Width, image.Height) * .45 / DefaultSideCount;
        var geometries = MainWindowViewModel.CreateGridGeometries(DefaultSideCount, DefaultSideCount, _pitch, new(image.Width / 2d, image.Height / 2d));
        _cellIds = Session.AddRange(geometries, selectAdded: true, isNameVisible: false).ToArray();
        SelectAll = new(Session.SelectAll);
        SelectCell = new(() => RunGuarded(() =>
        {
            if (!int.TryParse(Row, out var row) || !int.TryParse(Column, out var column) ||
                row < 1 || row > Rows || column < 1 || column > Columns)
            {
                throw new ArgumentException($"행·열은 1~{Columns} 사이의 정수입니다.");
            }
            SelectIndex(((row - 1) * Columns) + column - 1);
        }));
        Fit = new(() => Session.SetViewport(ViewportTransform.Fit(image.Width, image.Height, Layout.Width, Layout.Height)));
        Detail = new(() =>
        {
            var center = SelectionGeometry.Center;
            var scale = Math.Clamp(12 / _pitch, 1, 128);
            Session.SetViewport(new(scale, new PointD(Layout.Width / 2, Layout.Height / 2) - (center * scale)));
        });
        Undo = new(Session.Undo, () => Session.CanUndo);
        Redo = new(Session.Redo, () => Session.CanRedo);
        RotateLeft = new(() => Rotate(-15));
        RotateRight = new(() => Rotate(15));
        MoveLeft = new(() => Move(new(-50 / Session.State.Viewport.Scale, 0)));
        MoveRight = new(() => Move(new(50 / Session.State.Viewport.Scale, 0)));
        MoveUp = new(() => Move(new(0, -50 / Session.State.Viewport.Scale)));
        MoveDown = new(() => Move(new(0, 50 / Session.State.Viewport.Scale)));
        Apply = new(() => RunGuarded(() =>
        {
            if (SelectedId is { } id)
            {
                Session.SetGeometry(id, new(new(Number(X), Number(Y)), Number(Width), Number(Height), Number(Angle)));
            }
        }), () => SelectedId is not null && !Session.IsPreviewing);
        Pan = new(() =>
        {
            Input.Cancel();
            Input.Tool = EditorTool.Pan;
            Status = "화면 이동 · 드래그";
        });
        Select = new(() =>
        {
            Input.Cancel();
            Input.Tool = EditorTool.Select;
            Status = "선택 · 더블클릭: 개별 ROI · 드래그: 이동 · 원형 핸들: 회전";
        });
        Session.Changed += Changed;
        Input.Changed += Publish;
        Layout.Changed += Publish;
        Changed(this, EventArgs.Empty);
    }

    public Guid CellId(int index) => _cellIds[index];

    public RoiGeometry CellGeometry(int index) => Session.State.Rois[_cellIds[index]].Geometry;

    public void SelectIndex(int? index)
    {
        if (index is null)
        {
            Session.SelectAll();
        }
        else
        {
            Session.Select([_cellIds[index.Value]]);
        }
    }

    private static double Number(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) && double.IsFinite(value)
        ? value : throw new ArgumentException("유효한 숫자를 입력하세요.");

    private void RunGuarded(Action action)
    {
        try
        {
            action();
            Status = "적용 완료";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Status = ex.Message;
        }
    }

    public void Move(PointD delta) => RunGuarded(() =>
    {
        Input.Cancel();
        Session.TranslateSelection(delta);
    });

    public void Rotate(double degrees) => RunGuarded(() =>
    {
        Input.Cancel();
        Session.RotateSelection(degrees, SelectionGeometry.Center);
    });

    public async Task LoadImageAsync(IImageLoader loader, CancellationToken token)
    {
        ITileSource? source = await loader.LoadAsync(_initialImage, new Progress<string>(text => Status = text), token);
        try
        {
            token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            var image = source.Info;
            _generation = _display.SetSource(source);
            source = null;
            _image = image;
            Fit.Execute(null);
            Status = "이미지 중심에 생성 완료 · 전체 드래그/회전 · 더블클릭으로 개별 선택 · Esc 취소";
        }
        finally
        {
            source?.Dispose();
        }
    }

    public async Task InitializeAsync(IImageLoader loader)
    {
        try
        {
            await LoadImageAsync(loader, _loading.Token);
        }
        catch (Exception error)
        {
            ReportError(error);
            throw;
        }
    }

    public void ReportError(Exception error) => Status = error is OperationCanceledException ? "읽기 취소" : error.Message;

    public void UpdateRendererStatus(string status)
    {
        Adapter = status;
        Status = status;
    }

    public void UpdatePerformance(RenderMetrics metrics)
    {
        var gpu = metrics.GpuMilliseconds is { } elapsed ? $"{elapsed:F2} ms" : "미측정 (WARP)";
        Performance = $"CPU {metrics.CpuMilliseconds:F2} ms · GPU {gpu} · 최근 프레임 간격 {metrics.FrameIntervalMilliseconds:F2} ms";
    }

    private void Changed(object? sender, EventArgs args)
    {
        Publish();
        if (!Session.IsPreviewing)
        {
            var geometry = SelectionGeometry;
            X = geometry.Center.X.ToString("G17", CultureInfo.CurrentCulture);
            Y = geometry.Center.Y.ToString("G17", CultureInfo.CurrentCulture);
            Width = geometry.Width.ToString("G17", CultureInfo.CurrentCulture);
            Height = geometry.Height.ToString("G17", CultureInfo.CurrentCulture);
            Angle = geometry.Angle.ToString("G17", CultureInfo.CurrentCulture);
            foreach (var property in new[] { nameof(X), nameof(Y), nameof(Width), nameof(Height), nameof(Angle), nameof(SelectionText), nameof(CountText) })
            {
                Notify(property);
            }
            Undo.Refresh();
            Redo.Refresh();
            Apply.Refresh();
        }
    }

    public void Publish()
    {
        if (_disposed)
        {
            return;
        }
        Input.SetViewportSize(Layout.Width, Layout.Height);
        Scene = new(Session.State, _image, _generation,
            Layout.Width, Layout.Height, Layout.Dpi, Input.Overlay, Transform: Session.TransformPreview);
        Notify(nameof(Scene));
    }

    public bool HandleKey(Key key, bool control, bool shift)
    {
        if (key == Key.Escape)
        {
            Input.Cancel();
            Session.CancelPreview();
            return true;
        }
        var command = (control, shift, key) switch
        {
            (true, false, Key.Z) => Undo,
            (true, _, Key.Y) or (true, true, Key.Z) => Redo,
            (true, _, Key.A) => SelectAll,
            _ => null
        };
        if (command is null)
        {
            return false;
        }
        if (command.CanExecute(null))
        {
            command.Execute(null);
        }
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _loading.Cancel();
        _loading.Dispose();
        Session.Changed -= Changed;
        Input.Changed -= Publish;
        Layout.Changed -= Publish;
    }
}
