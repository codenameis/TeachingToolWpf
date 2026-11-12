using System.ComponentModel;
using System.Windows.Input;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Interaction;
using VisionCanvas.Presentation;
using VisionCanvas.Rendering;
using VisionCanvas.SampleShell.Commands;
using VisionCanvas.SampleShell.Services;
using VisionGeometry;

namespace VisionCanvas.SampleShell.ViewModels;

/// <summary>
/// Skia/Vulkan/Wgpu 독립 샘플 앱이 공유하는 편집 화면 상태·명령 기반 뷰모델.
/// 도형·경계·계층·이력 불변식은 공통 <see cref="EditorSession"/>이 판단한다.
/// </summary>
public abstract class EditorViewModelBase : INotifyPropertyChanged, IDisposable
{
    private const int SampleImageSidePixels = 8192;
    private const string SampleImageKey = "sample:8192:8192";
    private const string SampleImageName = "8K 예제";
    private const double ZoomInStepFactor = 1.25;
    private const double ZoomOutStepFactor = 0.8;
    private const double ViewportCenterDivisor = 2.0;
    private const double DefaultRoiScreenDipSize = 120.0;
    private const double MaximumRoiImageFractionDivisor = 4.0;

    // [Math Why]: 100,020,001셀(10001×10001 논리 검증 밀도)에서 최소 피치 0.025px를 만족하려면 min(W,H) * 0.45 / 10001 >= 0.025 -> 556px 이상이어야 한다.
    private const double BatchGridSpanRatio = 0.45;
    private const int HundredMillionLogicalSpan = 10001;
    private const double MinimumLogicalCellPitchPixels = 0.025;
    private const int BatchGridSideCount = 101;
    private const double GridCellFillRatio = 0.65;
    private const double MinimumCellDimensionPixels = 1e-4;

    private readonly EditorSession _editor;
    private readonly ImageCoordinator _images;
    private readonly RoiClipboard _clipboard = new();
    private readonly List<EditorCommand> _commands = new(24);
    private bool _isDisposed;
    private CancellationTokenSource? _preparation;
    private RoiShapeKind _shape;
    private string _status = "파일을 열거나 8K 예제를 선택하세요.";
    private string _renderer;

    protected EditorViewModelBase(
        EditorSession editor,
        InteractionController input,
        ViewportLayout layout,
        ImageCoordinator images,
        IImageFilePicker picker,
        string rendererLabel,
        string? initialRendererStatus = null)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(picker);
        ArgumentException.ThrowIfNullOrWhiteSpace(rendererLabel);

        _editor = editor;
        _images = images;
        Input = input;
        Layout = layout;
        HeaderTitle = $"VisionCanvas / {rendererLabel}";
        _renderer = string.IsNullOrWhiteSpace(initialRendererStatus)
            ? $"{rendererLabel} 장치 준비 중…"
            : initialRendererStatus;
        KeyAction = HandleKey;
        Navigation = new ViewportNavigationViewModel(editor, input, layout, () => IsInputEnabled);
        AlignmentTest = new AlignmentTestViewModel(editor, input, layout, images, Refresh, ReportStatus);

        OpenCommand = CreateAsync(async () =>
        {
            if (picker.Pick() is { } image)
            {
                Input.Cancel();
                await images.ReplaceAsync(image);
            }
        }, () => IsInputEnabled);

        SampleCommand = CreateAsync(async () =>
        {
            Input.Cancel();
            await images.ReplaceAsync(new(SampleImageKey, SampleImageName, SampleImageSidePixels, SampleImageSidePixels));
        }, () => IsInputEnabled);

        FitCommand = Create(() =>
        {
            var image = _editor.State.Image!;
            _editor.SetViewport(ViewportTransform.Fit(image.Width, image.Height, Layout.Width, Layout.Height));
        });

        ActualSizeCommand = Create(() => Zoom(1.0 / (Layout.Dpi * _editor.State.Viewport.Scale)));
        ZoomInCommand = Create(() => Zoom(ZoomInStepFactor));
        ZoomOutCommand = Create(() => Zoom(ZoomOutStepFactor));
        SelectCommand = Create(() => Input.Tool = EditorTool.Select);
        PanCommand = Create(() => Input.Tool = EditorTool.Pan);
        AddCommand = Create(() => Add(isGroup: false));
        HundredMillionCommand = Create(AddHundredMillion);
        AddParentCommand = Create(() => Add(isGroup: true));
        DrawCommand = Create(() => Draw(isGroup: false));
        DrawParentCommand = Create(() => Draw(isGroup: true));
        CompleteCommand = Create(Input.CompletePolygon, () => CanEdit && Input.CanCompletePolygon);
        CancelCommand = Create(() =>
        {
            _images.Cancel();
            _preparation?.Cancel();
            Input.Cancel();
        }, () => !_isDisposed && Input.IsEnabled);

        SelectAllCommand = Create(_editor.SelectAll);
        GroupCommand = Create(() => _editor.GroupSelection(), () => CanEdit && _editor.State.SelectionFacts.CanGroup);
        UngroupCommand = Create(_editor.UngroupSelection, () => CanEdit && _editor.State.SelectionFacts.HasGroup);
        CopyCommand = Create(() => _clipboard.Copy(_editor.State), () => CanEdit && HasSelection);
        PasteCommand = CreateAsync(PasteAsync, () => CanEdit && _clipboard.HasContent);
        DeleteCommand = Create(_editor.DeleteSelection, () => CanEdit && HasSelection);
        ResetAngleCommand = Create(_editor.ResetAngles, () => CanEdit && HasSelection);
        UndoCommand = CreateAsync(() => HistoryAsync(redo: false), () => IsInputEnabled && _editor.CanUndo);
        RedoCommand = CreateAsync(() => HistoryAsync(redo: true), () => IsInputEnabled && _editor.CanRedo);

        _editor.Changed += DocumentChanged;
        Input.Changed += Refresh;
        Layout.Changed += Refresh;
        _images.Changed += Refresh;
        _images.Status += ReportStatus;
        Refresh();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string HeaderTitle { get; }

    public InteractionController Input { get; }

    public ViewportLayout Layout { get; }

    public Func<Key, bool, bool, bool> KeyAction { get; }

    public RenderScene Scene { get; private set; } = null!;

    public ViewportNavigationViewModel Navigation { get; }

    public AlignmentTestViewModel AlignmentTest { get; }

    public bool IsNavigationEnabled => Input.IsEnabled && !_isDisposed;

    public bool IsInputEnabled => IsNavigationEnabled && !_images.IsBusy && _preparation is null;

    private bool CanEdit => IsInputEnabled && _editor.State.Image is not null && !Input.IsDragging;

    private bool HasSelection => _editor.State.SelectionCount > 0;

    public string Status => _status;

    public string RendererStatus => _renderer;

    public string Summary => $"{_images.Image?.Name ?? "이미지 없음"} · ROI {_editor.State.RoiCount:N0} · 선택 {_editor.State.SelectionCount:N0} · {_editor.State.Viewport.Scale:P0}";

    public string Tool => Input.Tool switch
    {
        EditorTool.Draw when Input.DrawAsGroup => "부모 그리기",
        EditorTool.Draw => "ROI 그리기 · 다각형은 점 입력 후 Enter",
        EditorTool.Pan => "화면 이동",
        _ => "선택 · 핸들로 크기/각도 편집"
    };

    public IReadOnlyList<RoiShapeKind> Shapes { get; } =
    [
        RoiShapeKind.Rectangle,
        RoiShapeKind.Circle,
        RoiShapeKind.Triangle,
        RoiShapeKind.Polygon
    ];

    public RoiShapeKind Shape
    {
        get => _shape;
        set
        {
            if (_shape != value)
            {
                Input.Cancel();
                _shape = value;
                Input.DrawShape = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Shape)));
            }
        }
    }

    public EditorCommand OpenCommand { get; }

    public EditorCommand SampleCommand { get; }

    public EditorCommand FitCommand { get; }

    public EditorCommand ActualSizeCommand { get; }

    public EditorCommand ZoomInCommand { get; }

    public EditorCommand ZoomOutCommand { get; }

    public EditorCommand SelectCommand { get; }

    public EditorCommand PanCommand { get; }

    public EditorCommand AddCommand { get; }

    public EditorCommand AddParentCommand { get; }

    public EditorCommand HundredMillionCommand { get; }

    /// <summary>Skia 샘플 및 회귀 테스트와의 명령 호환 별칭.</summary>
    public EditorCommand AddRepeatedCommand => HundredMillionCommand;

    public EditorCommand DrawCommand { get; }

    public EditorCommand DrawParentCommand { get; }

    public EditorCommand CompleteCommand { get; }

    public EditorCommand CancelCommand { get; }

    public EditorCommand SelectAllCommand { get; }

    public EditorCommand GroupCommand { get; }

    public EditorCommand UngroupCommand { get; }

    public EditorCommand CopyCommand { get; }

    public EditorCommand PasteCommand { get; }

    public EditorCommand DeleteCommand { get; }

    public EditorCommand ResetAngleCommand { get; }

    public EditorCommand UndoCommand { get; }

    public EditorCommand RedoCommand { get; }

    private EditorCommand Create(Action execute, Func<bool>? canExecute = null) => CreateAsync(() =>
    {
        execute();
        Refresh();
        return Task.CompletedTask;
    }, canExecute ?? (() => CanEdit));

    private EditorCommand CreateAsync(Func<Task> execute, Func<bool> canExecute)
    {
        var command = new EditorCommand(execute, canExecute, ReportError);
        _commands.Add(command);
        return command;
    }

    private void Zoom(double factor) => _editor.SetViewport(
        _editor.State.Viewport.ZoomAt(new PointD(Layout.Width / ViewportCenterDivisor, Layout.Height / ViewportCenterDivisor), factor));

    private Guid? ResolveSelectedGroupParentId()
        => _editor.State.Selection.Count == 1
           && _editor.State.SelectionFacts.First is { } roi
           && _editor.State.IsGroupRoi(roi.Id)
            ? roi.Id
            : null;

    private void Draw(bool isGroup)
    {
        Input.DrawParentId = isGroup ? null : ResolveSelectedGroupParentId();
        Input.DrawAsGroup = isGroup;
        Input.DrawShape = Shape;
        Input.Tool = EditorTool.Draw;
    }

    private async Task PasteAsync()
    {
        using var cancellation = new CancellationTokenSource();
        _preparation = cancellation;
        Refresh();
        try
        {
            var prepared = await _editor.PreparePasteAsync(_clipboard, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            _editor.CommitPreparedPaste(prepared);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            ReportStatus("붙여넣기를 취소했습니다.");
        }
        finally
        {
            _preparation = null;
            Refresh();
        }
    }

    private void AddHundredMillion()
    {
        var image = _editor.State.Image!;
        double pitchCheck = Math.Min(image.Width, image.Height) * BatchGridSpanRatio / HundredMillionLogicalSpan;
        if (pitchCheck < MinimumLogicalCellPitchPixels)
        {
            throw new InvalidOperationException("1억 ROI 테스트에는 가로·세로 556px 이상의 이미지가 필요합니다.");
        }

        Input.Cancel();
        double pitch = Math.Min(image.Width, image.Height) * BatchGridSpanRatio / BatchGridSideCount;
        var center = new PointD(image.Width / ViewportCenterDivisor, image.Height / ViewportCenterDivisor);
        RoiGeometry[] geometries = CreateGridGeometries(BatchGridSideCount, BatchGridSideCount, pitch, center);
        _editor.AddRange(geometries, selectAdded: true, isNameVisible: false);
        Input.Tool = EditorTool.Select;
        ReportStatus($"{geometries.Length:N0}개 일반 ROI 추가 · 확대 후 더블클릭으로 개별 선택 · Ctrl+G 그룹화 · Ctrl+Z로 추가 취소");
    }

    private static RoiGeometry[] CreateGridGeometries(int columns, int rows, double pitch, PointD center)
    {
        double width = Math.Max(pitch * GridCellFillRatio, MinimumCellDimensionPixels);
        double halfColumns = (columns - 1) / ViewportCenterDivisor;
        double halfRows = (rows - 1) / ViewportCenterDivisor;
        var geometries = new RoiGeometry[columns * rows];
        int index = 0;

        for (int row = 0; row < rows; row++)
        {
            double y = center.Y + ((row - halfRows) * pitch);
            for (int column = 0; column < columns; column++)
            {
                double x = center.X + ((column - halfColumns) * pitch);
                geometries[index++] = new RoiGeometry(new PointD(x, y), width, width, 0);
            }
        }

        return geometries;
    }

    private void Add(bool isGroup)
    {
        if (Shape == RoiShapeKind.Polygon)
        {
            Draw(isGroup);
            return;
        }

        var image = _editor.State.Image!;
        Guid? parent = isGroup ? null : ResolveSelectedGroupParentId();
        PointD center = parent is { } id
            ? _editor.State.Rois[id].Geometry.Center
            : _editor.State.Viewport.ScreenToImage(new PointD(Layout.Width / ViewportCenterDivisor, Layout.Height / ViewportCenterDivisor));
        double size = Math.Min(DefaultRoiScreenDipSize / _editor.State.Viewport.Scale, Math.Min(image.Width, image.Height) / MaximumRoiImageFractionDivisor);
        double halfSize = size / ViewportCenterDivisor;
        center = new PointD(
            Math.Clamp(center.X, halfSize, image.Width - halfSize),
            Math.Clamp(center.Y, halfSize, image.Height - halfSize));
        RoiGeometry geometry = RoiGeometry.Create(Shape, center, size, size);

        if (isGroup)
        {
            _editor.AddParent(geometry);
        }
        else if (parent is { } parentId)
        {
            _editor.AddChild(parentId, geometry);
        }
        else
        {
            _editor.Add(geometry);
        }
    }

    private Task HistoryAsync(bool redo)
    {
        Input.Cancel();
        return _images.HistoryAsync(redo);
    }

    public bool HandleKey(Key key, bool control, bool shift)
    {
        if (!Input.IsEnabled)
        {
            return false;
        }

        if (CanEdit && ViewportKeyboard.Nudge(Input, key, control, shift))
        {
            return true;
        }

        EditorCommand? command = (key, control, shift) switch
        {
            (Key.Escape, _, _) => CancelCommand,
            (Key.Enter, _, _) => CompleteCommand,
            (Key.Delete, _, _) => DeleteCommand,
            (Key.O, true, _) => OpenCommand,
            (Key.A, true, _) => SelectAllCommand,
            (Key.C, true, _) => CopyCommand,
            (Key.V, true, _) => PasteCommand,
            (Key.Z, true, false) => UndoCommand,
            (Key.Z, true, true) or (Key.Y, true, _) => RedoCommand,
            (Key.R, false, _) => DrawCommand,
            _ => null
        };

        if (command is null || !command.CanExecute(null))
        {
            return false;
        }

        command.Execute(null);
        return true;
    }

    private void DocumentChanged(object? sender, EventArgs args)
    {
        if (Input.DrawParentId is { } parent && !_editor.State.Rois.ContainsKey(parent))
        {
            Input.Tool = EditorTool.Select;
        }

        Refresh();
    }

    private void Refresh()
    {
        if (_isDisposed)
        {
            return;
        }

        if (!Input.IsEnabled)
        {
            _preparation?.Cancel();
        }

        Input.NavigationOnly = !IsInputEnabled;
        Navigation.Refresh();
        AlignmentTest.OnDocumentChanged();
        Input.SetViewportSize(Layout.Width, Layout.Height);
        Scene = new RenderScene(
            _editor.State,
            _images.Image,
            _images.Generation,
            Layout.Width,
            Layout.Height,
            Layout.Dpi,
            Input.Overlay,
            Transform: _editor.TransformPreview,
            Alignment: AlignmentTest.CurrentAlignment);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Scene)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsInputEnabled)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsNavigationEnabled)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Tool)));

        foreach (EditorCommand command in _commands)
        {
            command.Refresh();
        }
    }

    public void ReportError(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        ReportStatus($"오류: {error.Message}");
    }

    public void ReportStatus(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!_isDisposed)
        {
            _status = message;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
        }
    }

    public void ReportRenderer(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!_isDisposed)
        {
            _renderer = message;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RendererStatus)));
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_isDisposed || !disposing)
        {
            return;
        }

        _isDisposed = true;
        _editor.Changed -= DocumentChanged;
        Input.Changed -= Refresh;
        Layout.Changed -= Refresh;
        _images.Changed -= Refresh;
        _images.Status -= ReportStatus;
        Input.Cancel();
        _images.Dispose();
        _preparation?.Cancel();

        foreach (EditorCommand command in _commands)
        {
            command.Refresh();
        }
    }
}
