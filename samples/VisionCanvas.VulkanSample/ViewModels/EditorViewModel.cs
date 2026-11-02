using System.ComponentModel;
using System.Windows.Input;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Interaction;
using VisionCanvas.Presentation;
using VisionCanvas.Rendering;
using VisionCanvas.VulkanSample.Commands;
using VisionCanvas.VulkanSample.Services;
using VisionGeometry;

namespace VisionCanvas.VulkanSample.ViewModels;

/// <summary>화면 상태와 명령을 연결한다. 도형·경계·계층·이력은 공통 EditorSession이 판단한다.</summary>
public sealed class EditorViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly EditorSession _editor;
    private readonly ImageCoordinator _images;
    private readonly RoiClipboard _clipboard = new();
    private readonly List<EditorCommand> _commands = [];
    private bool _disposed;
    private CancellationTokenSource? _preparation;
    private RoiShapeKind _shape;
    private string _status = "파일을 열거나 8K 예제를 선택하세요.";
    private string _renderer = "Vulkan 장치 준비 중…";

    public EditorViewModel(EditorSession editor, InteractionController input, ViewportLayout layout, ImageCoordinator images, IImageFilePicker picker)
    {
        _editor = editor;
        _images = images;
        Input = input;
        Layout = layout;
        KeyAction = HandleKey;
        Navigation = new(editor, input, layout, () => IsInputEnabled);
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
            await images.ReplaceAsync(new("sample:8192:8192", "8K 예제", 8192, 8192));
        }, () => IsInputEnabled);
        FitCommand = Create(() =>
        {
            var image = _editor.State.Image!;
            _editor.SetViewport(ViewportTransform.Fit(image.Width, image.Height, Layout.Width, Layout.Height));
        });
        ActualSizeCommand = Create(() => Zoom(1 / (Layout.Dpi * _editor.State.Viewport.Scale)));
        ZoomInCommand = Create(() => Zoom(1.25));
        ZoomOutCommand = Create(() => Zoom(.8));
        SelectCommand = Create(() => Input.Tool = EditorTool.Select);
        PanCommand = Create(() => Input.Tool = EditorTool.Pan);
        AddCommand = Create(() => Add(false));
        HundredMillionCommand = Create(AddHundredMillion);
        AddParentCommand = Create(() => Add(true));
        DrawCommand = Create(() => Draw(false));
        DrawParentCommand = Create(() => Draw(true));
        CompleteCommand = Create(Input.CompletePolygon, () => CanEdit && Input.CanCompletePolygon);
        CancelCommand = Create(() =>
        {
            _images.Cancel();
            _preparation?.Cancel();
            Input.Cancel();
        }, () => !_disposed && Input.IsEnabled);
        SelectAllCommand = Create(_editor.SelectAll);
        GroupCommand = Create(() => _editor.GroupSelection(), () => CanEdit && _editor.State.SelectionFacts.CanGroup);
        UngroupCommand = Create(_editor.UngroupSelection, () => CanEdit && _editor.State.SelectionFacts.HasGroup);
        CopyCommand = Create(() => _clipboard.Copy(_editor.State), () => CanEdit && HasSelection);
        PasteCommand = CreateAsync(async () =>
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
        }, () => CanEdit && _clipboard.HasContent);
        DeleteCommand = Create(_editor.DeleteSelection, () => CanEdit && HasSelection);
        ResetAngleCommand = Create(_editor.ResetAngles, () => CanEdit && HasSelection);
        UndoCommand = CreateAsync(() => HistoryAsync(false), () => IsInputEnabled && _editor.CanUndo);
        RedoCommand = CreateAsync(() => HistoryAsync(true), () => IsInputEnabled && _editor.CanRedo);
        _editor.Changed += DocumentChanged;
        Input.Changed += Refresh;
        Layout.Changed += Refresh;
        _images.Changed += Refresh;
        _images.Status += ReportStatus;
        Refresh();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public InteractionController Input
    {
        get;
    }
    public ViewportLayout Layout
    {
        get;
    }
    public Func<Key, bool, bool, bool> KeyAction
    {
        get;
    }
    public RenderScene Scene { get; private set; } = null!;
    public ViewportNavigationViewModel Navigation { get; }

    public bool IsNavigationEnabled => Input.IsEnabled && !_disposed;

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
    public IReadOnlyList<RoiShapeKind> Shapes { get; } = [RoiShapeKind.Rectangle, RoiShapeKind.Circle, RoiShapeKind.Triangle, RoiShapeKind.Polygon];
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
                PropertyChanged?.Invoke(this, new(nameof(Shape)));
            }
        }
    }

    public EditorCommand OpenCommand
    {
        get;
    }
    public EditorCommand SampleCommand
    {
        get;
    }
    public EditorCommand FitCommand
    {
        get;
    }
    public EditorCommand ActualSizeCommand
    {
        get;
    }
    public EditorCommand ZoomInCommand
    {
        get;
    }
    public EditorCommand ZoomOutCommand
    {
        get;
    }
    public EditorCommand SelectCommand
    {
        get;
    }
    public EditorCommand PanCommand
    {
        get;
    }
    public EditorCommand AddCommand
    {
        get;
    }
    public EditorCommand HundredMillionCommand
    {
        get;
    }
    public EditorCommand AddParentCommand
    {
        get;
    }
    public EditorCommand DrawCommand
    {
        get;
    }
    public EditorCommand DrawParentCommand
    {
        get;
    }
    public EditorCommand CompleteCommand
    {
        get;
    }
    public EditorCommand CancelCommand
    {
        get;
    }
    public EditorCommand SelectAllCommand
    {
        get;
    }
    public EditorCommand GroupCommand
    {
        get;
    }
    public EditorCommand UngroupCommand
    {
        get;
    }
    public EditorCommand CopyCommand
    {
        get;
    }
    public EditorCommand PasteCommand
    {
        get;
    }
    public EditorCommand DeleteCommand
    {
        get;
    }
    public EditorCommand ResetAngleCommand
    {
        get;
    }
    public EditorCommand UndoCommand
    {
        get;
    }
    public EditorCommand RedoCommand
    {
        get;
    }

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

    private void Zoom(double factor) => _editor.SetViewport(_editor.State.Viewport.ZoomAt(new(Layout.Width / 2, Layout.Height / 2), factor));

    private Guid? Parent() => _editor.State.Selection.Count == 1 && _editor.State.SelectionFacts.First is { } roi && _editor.State.IsGroupRoi(roi.Id) ? roi.Id : null;

    private void Draw(bool group)
    {
        Input.DrawParentId = group ? null : Parent();
        Input.DrawAsGroup = group;
        Input.DrawShape = Shape;
        Input.Tool = EditorTool.Draw;
    }

    private void AddHundredMillion()
    {
        var image = _editor.State.Image!;
        var pitchCheck = Math.Min(image.Width, image.Height) * .45 / 10001;
        if (pitchCheck < .025)
        {
            throw new InvalidOperationException("1억 ROI 테스트에는 가로·세로 556px 이상의 이미지가 필요합니다.");
        }
        Input.Cancel();
        const int sideCount = 101;
        var pitch = Math.Min(image.Width, image.Height) * .45 / sideCount;
        var geometries = CreateGridGeometries(sideCount, sideCount, pitch, new(image.Width / 2d, image.Height / 2d));
        _editor.AddRange(geometries, selectAdded: true, isNameVisible: false);
        Input.Tool = EditorTool.Select;
        ReportStatus($"{geometries.Length:N0}개 일반 ROI 추가 · 확대 후 더블클릭으로 개별 선택 · Ctrl+G 그룹화 · Ctrl+Z로 추가 취소");
    }

    private static RoiGeometry[] CreateGridGeometries(int columns, int rows, double pitch, PointD center)
    {
        var width = Math.Max(pitch * .65, 1e-4);
        var halfColumns = (columns - 1) / 2d;
        var halfRows = (rows - 1) / 2d;
        var geometries = new RoiGeometry[columns * rows];
        var index = 0;
        for (var row = 0; row < rows; row++)
        {
            var y = center.Y + ((row - halfRows) * pitch);
            for (var column = 0; column < columns; column++)
            {
                var x = center.X + ((column - halfColumns) * pitch);
                geometries[index++] = new RoiGeometry(new(x, y), width, width, 0);
            }
        }
        return geometries;
    }

    private void Add(bool group)
    {
        if (Shape == RoiShapeKind.Polygon)
        {
            Draw(group);
            return;
        }
        var image = _editor.State.Image!;
        var parent = group ? null : Parent();
        var center = parent is { } id ? _editor.State.Rois[id].Geometry.Center :
            _editor.State.Viewport.ScreenToImage(new(Layout.Width / 2, Layout.Height / 2));
        var size = Math.Min(120 / _editor.State.Viewport.Scale, Math.Min(image.Width, image.Height) / 4d);
        center = new(Math.Clamp(center.X, size / 2, image.Width - (size / 2)), Math.Clamp(center.Y, size / 2, image.Height - (size / 2)));
        var geometry = RoiGeometry.Create(Shape, center, size, size);
        if (group)
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
        var command = (key, control, shift) switch
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
        if (_disposed)
        {
            return;
        }
        if (!Input.IsEnabled)
        {
            _preparation?.Cancel();
        }
        Input.NavigationOnly = !IsInputEnabled;
        Navigation.Refresh();
        Input.SetViewportSize(Layout.Width, Layout.Height);
        Scene = new(_editor.State, _images.Image, _images.Generation, Layout.Width, Layout.Height, Layout.Dpi, Input.Overlay, Transform: _editor.TransformPreview);
        PropertyChanged?.Invoke(this, new(nameof(Scene)));
        PropertyChanged?.Invoke(this, new(nameof(IsInputEnabled)));
        PropertyChanged?.Invoke(this, new(nameof(IsNavigationEnabled)));
        PropertyChanged?.Invoke(this, new(nameof(Summary)));
        PropertyChanged?.Invoke(this, new(nameof(Tool)));
        foreach (var command in _commands)
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
        if (!_disposed)
        {
            _status = message;
            PropertyChanged?.Invoke(this, new(nameof(Status)));
        }
    }

    public void ReportRenderer(string message)
    {
        if (!_disposed)
        {
            _renderer = message;
            PropertyChanged?.Invoke(this, new(nameof(RendererStatus)));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _editor.Changed -= DocumentChanged;
        Input.Changed -= Refresh;
        Layout.Changed -= Refresh;
        _images.Changed -= Refresh;
        _images.Status -= ReportStatus;
        Input.Cancel();
        _images.Dispose();
        _preparation?.Cancel();
        foreach (var command in _commands)
        {
            command.Refresh();
        }
    }
}
