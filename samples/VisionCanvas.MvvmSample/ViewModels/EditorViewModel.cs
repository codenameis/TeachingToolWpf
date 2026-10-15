using System.ComponentModel;
using System.Windows.Input;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Interaction;
using VisionCanvas.MvvmSample.Commands;
using VisionCanvas.MvvmSample.Models;
using VisionCanvas.MvvmSample.Services;
using VisionCanvas.Presentation;
using VisionCanvas.Rendering;
using VisionGeometry;

namespace VisionCanvas.MvvmSample.ViewModels;

/// <summary>
/// 화면에 보여 줄 값과 사용자가 실행할 명령을 제공하는 ViewModel이다.
/// 버튼 → 명령 → Core 편집 → 변경 알림 → Scene 갱신 → Canvas 표시 순서로 연결된다.
/// ROI 문서 자체는 EditorSession이 관리하므로 여기서 기하·계층·이력을 다시 구현하지 않는다.
/// </summary>
// INotifyPropertyChanged는 '속성이 바뀌었다'고 WPF에 알리는 약속이다.
// IDisposable은 창을 닫을 때 작업 취소와 이벤트 연결 해제를 명시적으로 할 수 있게 한다.
public sealed class EditorViewModel : INotifyPropertyChanged, IDisposable
{
    // _로 시작하는 필드는 이 VM 내부에서 기억하는 값이다.
    // readonly는 연결된 객체를 다른 객체로 교체하지 못하게 한다. 객체 내부 상태까지 불변인 것은 아니다.
    private readonly EditorSession _editor;
    private readonly ImageCoordinator _images;
    private readonly CameraModel? _camera;
    private RoiIdSet? _listedSelection;
    private IReadOnlyDictionary<Guid, Roi>? _listedRois;
    private bool _refreshingRoiItems;
    private RoiItemViewModel? _selectedRoi;
    private readonly RoiClipboard _clipboard = new();
    private readonly List<EditorCommand> _commands = [];
    private bool _disposed;
    private string _status = "이미지를 열거나 8K 예제를 선택하세요.";

    public EditorViewModel(EditorSession editor, InteractionController input, ViewportLayout layout,
        ImageCoordinator images, IImageFilePicker picker, CameraModel? camera = null)
    {
        // App이 만든 객체들을 받는다. 여기서 Window나 파일 로더를 직접 만들지 않으므로
        // 테스트할 때 실제 창 없이 같은 VM을 사용할 수 있다.
        _editor = editor;
        _images = images;
        _camera = camera;
        Input = input;
        Layout = layout;
        KeyAction = HandleKey;
        // () => ... 는 '나중에 호출할 함수'를 전달하는 문법이다. 생성 순간 파일 창이 열리는 것은 아니다.
        // 두 번째 함수는 실행 가능 여부다. 파일 로딩 중에는 다른 열기 요청을 시작하지 않는다.
        OpenCommand = CreateAsync(async () =>
        {
            // is { } reference는 null이 아닐 때만 선택 결과를 reference라는 변수에 담는다.
            if (picker.Pick() is { } reference)
            {
                // 진행 중인 드래그를 먼저 취소해야 이전 이미지의 편집이 새 이미지에 섞이지 않는다.
                Input.Cancel();
                await _images.ReplaceAsync(reference);
            }
        }, () => IsInputEnabled);
        SampleCommand = CreateAsync(async () =>
        {
            Input.Cancel();
            // sample:...은 라이브러리가 알아보는 예제 이미지 식별자다. 실제 파일 경로가 아니다.
            await _images.ReplaceAsync(new("sample:8192:8192", "8K 예제", 8192, 8192));
        }, () => IsInputEnabled);
        LoadModelCommand = CreateAsync(async () =>
        {
            Input.Cancel();
            Input.Tool = EditorTool.Select;
            await _images.LoadDocumentAsync(_camera!.Image, _camera.Rois);
        }, () => IsInputEnabled && _camera is not null && !Input.IsDragging);
        // 여기서는 명령과 동작을 연결만 한다. 실제 좌표 변환·편집 규칙은 아래 메서드와 Core가 맡는다.
        FitCommand = Create(Fit);
        ZoomInCommand = Create(() => Zoom(1.25));
        ZoomOutCommand = Create(() => Zoom(.8));
        SelectCommand = Create(() => Input.Tool = EditorTool.Select);
        PanCommand = Create(() => Input.Tool = EditorTool.Pan);
        DrawRectangleCommand = Create(() => Draw(RoiShapeKind.Rectangle));
        DrawCircleCommand = Create(() => Draw(RoiShapeKind.Circle));
        DrawPolygonCommand = Create(() => Draw(RoiShapeKind.Polygon));
        DrawParentCommand = Create(() => Draw(RoiShapeKind.Rectangle, true));
        CompletePolygonCommand = Create(Input.CompletePolygon, () => CanEdit && Input.CanCompletePolygon);
        CancelCommand = Create(() =>
        {
            _images.Cancel();
            Input.Cancel();
        }, () => !_disposed);
        // 그룹화 가능 여부도 Core의 판단을 사용한다. VM에서 같은 계층 규칙을 중복 작성하지 않는다.
        GroupCommand = Create(() => _editor.GroupSelection(), () => CanEdit && _editor.State.SelectionFacts.CanGroup);
        UngroupCommand = Create(_editor.UngroupSelection, () => CanEdit && _editor.State.SelectionFacts.HasGroup);
        DeleteCommand = Create(_editor.DeleteSelection, () => CanEdit && HasSelection);
        SelectAllCommand = Create(_editor.SelectAll);
        CopyCommand = Create(() => _clipboard.Copy(_editor.State), () => CanEdit && HasSelection);
        PasteCommand = Create(() => _editor.Paste(_clipboard), () => CanEdit && _clipboard.HasContent);
        UndoCommand = CreateAsync(() => HistoryAsync(false), () => IsInputEnabled && _editor.CanUndo);
        RedoCommand = CreateAsync(() => HistoryAsync(true), () => IsInputEnabled && _editor.CanRedo);
        // 문서·드래그 미리보기·창 크기·이미지 중 어느 것이 바뀌어도 표시 내용을 다시 준비한다.
        // 변경 사실을 알리는 이벤트를 구독하는 방식이다. Dispose에서 반드시 연결을 끊는다.
        _editor.Changed += DocumentChanged;
        Input.Changed += Refresh;
        Layout.Changed += Refresh;
        _images.Changed += Refresh;
        _images.Status += ReportStatus;
        Refresh();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    // public 속성은 XAML이 읽는 창구다. { get; }는 외부에서 값을 바꿀 수 없는 속성이다.
    public InteractionController Input { get; }
    public ViewportLayout Layout { get; }
    public Func<Key, bool, bool, bool> KeyAction { get; }
    // Scene은 '현재 무엇을 그릴지'를 담은 데이터다. 픽셀을 직접 그리는 객체는 아니다.
    // null!의 !는 컴파일러에 초기화를 보장한다고 알린다. 실제 값은 생성자의 Refresh에서 넣는다.
    public RenderScene Scene { get; private set; } = null!;
    public bool IsInputEnabled => !_disposed && !_images.IsBusy;
    private bool CanEdit => IsInputEnabled && _editor.State.Image is not null && !Input.IsDragging;
    private bool HasSelection => _editor.State.SelectionCount > 0;
    public string Status => _status;
    public string CameraName => _camera?.Name ?? "VisionCanvas";
    public IReadOnlyList<RoiItemViewModel> RoiItems { get; private set; } = [];
    // 목록에서 고른 ROI도 Canvas와 같은 Core 문서로 전달한다. 선택 규칙을 따로 복제하지 않는다.
    public RoiItemViewModel? SelectedRoi
    {
        get => _selectedRoi;
        set
        {
            if (_refreshingRoiItems || _selectedRoi?.Id == value?.Id || !CanEdit)
            {
                return;
            }
            Input.Cancel();
            Input.Tool = EditorTool.Select;
            _editor.Select(value is null ? [] : [value.Id]);
        }
    }
    // => 형태의 속성은 읽을 때 값을 계산한다. ??는 앞의 값이 null일 때 사용할 기본값이다.
    // $"...{값}..."는 문자열에 값을 넣으며, N0는 정수 자릿수 구분, P0는 소수점 없는 백분율 표시다.
    public string Summary => $"{_images.Image?.Name ?? "이미지 없음"} · ROI {_editor.State.RoiCount:N0} · 선택 {_editor.State.SelectionCount:N0} · {_editor.State.Viewport.Scale:P0}";
    public string ToolDescription => Input.Tool switch
    {
        EditorTool.Draw when Input.DrawAsGroup => "부모 그리기 · 사각형",
        EditorTool.Draw => $"{(Input.DrawParentId is null ? "ROI" : "자식 ROI")} 그리기 · {ShapeName}",
        EditorTool.Pan => "화면 이동",
        _ => "선택 · 드래그로 이동, 핸들로 크기·각도 편집"
    };
    private string ShapeName => Input.DrawShape switch
    {
        RoiShapeKind.Circle => "원",
        RoiShapeKind.Polygon => "다각형 · 점을 찍고 Enter로 완료",
        _ => "사각형"
    };

    // XAML의 Command 바인딩과 HandleKey가 아래의 동일한 명령 객체들을 사용한다.
    public EditorCommand OpenCommand { get; }
    public EditorCommand SampleCommand { get; }
    public EditorCommand LoadModelCommand { get; }
    public EditorCommand FitCommand { get; }
    public EditorCommand ZoomInCommand { get; }
    public EditorCommand ZoomOutCommand { get; }
    public EditorCommand SelectCommand { get; }
    public EditorCommand PanCommand { get; }
    public EditorCommand DrawRectangleCommand { get; }
    public EditorCommand DrawCircleCommand { get; }
    public EditorCommand DrawPolygonCommand { get; }
    public EditorCommand DrawParentCommand { get; }
    public EditorCommand CompletePolygonCommand { get; }
    public EditorCommand CancelCommand { get; }
    public EditorCommand GroupCommand { get; }
    public EditorCommand UngroupCommand { get; }
    public EditorCommand DeleteCommand { get; }
    public EditorCommand SelectAllCommand { get; }
    public EditorCommand CopyCommand { get; }
    public EditorCommand PasteCommand { get; }
    public EditorCommand UndoCommand { get; }
    public EditorCommand RedoCommand { get; }

    // 즉시 끝나는 동작도 같은 명령 클래스로 감싼다. CompletedTask는 '이미 끝난 작업'이며 새 스레드가 아니다.
    // Copy처럼 문서가 바뀌지 않는 동작 뒤에도 붙여넣기 버튼의 활성 상태는 다시 확인해야 한다.
    private EditorCommand Create(Action execute, Func<bool>? canExecute = null) => CreateAsync(() =>
    {
        execute();
        RefreshCommands();
        return Task.CompletedTask;
    }, canExecute ?? (() => CanEdit));

    private EditorCommand CreateAsync(Func<Task> execute, Func<bool> canExecute)
    {
        var command = new EditorCommand(execute, canExecute, ReportError);
        _commands.Add(command);
        return command;
    }

    private void Fit()
    {
        // 이 명령은 이미지가 있을 때만 실행된다(CanEdit). 실제 맞춤 배율 계산은 기하 라이브러리에 맡긴다.
        var image = _editor.State.Image!;
        _editor.SetViewport(ViewportTransform.Fit(image.Width, image.Height, Layout.Width, Layout.Height));
    }

    // 화면 중심을 기준으로 확대한다. 원본 이미지 크기를 바꾸는 것이 아니라 보는 위치와 배율을 바꾼다.
    private void Zoom(double factor) => _editor.SetViewport(_editor.State.Viewport.ZoomAt(new(Layout.Width / 2, Layout.Height / 2), factor));

    private void Draw(RoiShapeKind shape, bool parent = false)
    {
        // '그리기 버튼'은 도구만 준비한다. 실제 ROI 생성은 이후 마우스 입력을 받은 Input/Core가 수행한다.
        var state = _editor.State;
        var selected = state.SelectionFacts.First;
        // 그룹 하나를 선택했으면 그 안의 자식으로, 그 외에는 독립 ROI로 그린다.
        // 부모 그리기는 별도 그룹을 만드는 명령이므로 선택 그룹의 자식으로 연결하지 않는다.
        Input.DrawParentId = !parent && state.Selection.Count == 1 && selected is not null && state.IsGroupRoi(selected.Id)
            ? selected.Id : null;
        Input.DrawAsGroup = parent;
        Input.DrawShape = shape;
        Input.Tool = EditorTool.Draw;
    }

    private Task HistoryAsync(bool redo)
    {
        // Undo가 이미지 교체까지 되돌릴 수도 있다. 문서만 되돌리지 않고 원본 재열기를 함께 조율한다.
        Input.Cancel();
        return _images.HistoryAsync(redo);
    }

    public bool HandleKey(Key key, bool control, bool shift)
    {
        // 키와 Ctrl/Shift 상태 세 값을 묶어서 비교한다. _는 해당 값이 무엇이든 상관없다는 뜻이다.
        // 버튼과 같은 명령을 실행하므로 단축키에 편집 규칙을 따로 복사할 필요가 없다.
        var command = (key, control, shift) switch
        {
            (Key.Escape, _, _) => CancelCommand,
            (Key.Enter, _, _) => CompletePolygonCommand,
            (Key.Delete, _, _) => DeleteCommand,
            (Key.O, true, _) => OpenCommand,
            (Key.A, true, _) => SelectAllCommand,
            (Key.C, true, _) => CopyCommand,
            (Key.V, true, _) => PasteCommand,
            (Key.Z, true, false) => UndoCommand,
            (Key.Z, true, true) or (Key.Y, true, _) => RedoCommand,
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
        // 자식 그리기 준비 후 Undo로 부모가 사라졌다면, 없는 부모에 그리려는 상태를 해제한다.
        if (Input.Tool == EditorTool.Draw && Input.DrawParentId is { } parent && !_editor.State.Rois.ContainsKey(parent))
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
        // DIP는 WPF의 화면 크기 단위다. DPI 배율이 달라도 입력과 표시가 같은 위치를 가리켜야 한다.
        Input.SetViewportSize(Layout.Width, Layout.Height);
        // 기존 문서와 미리보기 데이터를 묶는다. 이 줄에서 큰 이미지의 픽셀 배열을 복사하지 않는다.
        Scene = new(_editor.State, _images.Image, _images.Generation, Layout.Width, Layout.Height, Layout.Dpi,
            Input.Overlay, Transform: _editor.TransformPreview);
        // 속성에 값을 넣는 것만으로 WPF가 변경을 알아채지는 못한다. 바뀐 속성의 이름을 알린다.
        // nameof(Scene)은 "Scene"이라는 문자열을 만들며, 속성 이름을 바꿀 때 함께 추적할 수 있다.
        PropertyChanged?.Invoke(this, new(nameof(Scene)));
        PropertyChanged?.Invoke(this, new(nameof(IsInputEnabled)));
        PropertyChanged?.Invoke(this, new(nameof(Summary)));
        PropertyChanged?.Invoke(this, new(nameof(ToolDescription)));
        RefreshRoiItems();
        RefreshCommands();
    }

    private void RefreshRoiItems()
    {
        // ItemsSource 교체 중 WPF가 일시적으로 선택을 비워도 실제 문서의 선택은 지우지 않는다.
        _refreshingRoiItems = true;
        try
        {
            UpdateRoiItems();
        }
        finally
        {
            _refreshingRoiItems = false;
        }
    }

    private void UpdateRoiItems()
    {
        var state = _editor.State;
        // 화면 이동만으로 목록을 다시 만들지 않는다. 불변 문서의 목록 참조가 바뀔 때만 갱신한다.
        if (!ReferenceEquals(_listedRois, state.Rois))
        {
            _listedRois = state.Rois;
            RoiItems = state.Rois.Values.OrderBy(roi => roi.Order).Select(roi => new RoiItemViewModel(
                roi.Id, new string(' ', (RoiHierarchy.Depth(state.Rois, roi.Id) - 1) * 3) + roi.Name,
                state.IsGroupRoi(roi.Id) ? "그룹" : roi.Geometry.ShapeName,
                state.IsGroupRoi(roi.Id) ? null : _camera?.Algorithms.GetValueOrDefault(roi.Id))).ToArray();
            PropertyChanged?.Invoke(this, new(nameof(RoiItems)));
            _listedSelection = null;
        }
        if (!ReferenceEquals(_listedSelection, state.Selection))
        {
            _listedSelection = state.Selection;
            _selectedRoi = state.Selection.Count == 1 ? RoiItems.FirstOrDefault(item => state.Selection.Contains(item.Id)) : null;
            PropertyChanged?.Invoke(this, new(nameof(SelectedRoi)));
        }
    }

    private void RefreshCommands()
    {
        // 명령을 실행하는 것이 아니다. 버튼들에게 '지금 실행 가능한지 다시 물어봐 달라'고 알린다.
        foreach (var command in _commands)
        {
            command.Refresh();
        }
    }

    public void ReportError(Exception error) => ReportStatus($"오류: {error.Message}");

    public void ReportStatus(string status)
    {
        if (!_disposed)
        {
            _status = status;
            PropertyChanged?.Invoke(this, new(nameof(Status)));
        }
    }

    public void Dispose()
    {
        // 여러 경로에서 종료 처리가 불려도 정리는 한 번만 수행한다.
        if (_disposed)
        {
            return;
        }
        // 취소 도중에도 변경 이벤트가 생길 수 있으므로 먼저 종료 표시를 하고 구독을 해제한다.
        _disposed = true;
        _editor.Changed -= DocumentChanged;
        Input.Changed -= Refresh;
        Layout.Changed -= Refresh;
        _images.Changed -= Refresh;
        _images.Status -= ReportStatus;
        Input.Cancel();
        _images.Dispose();
        RefreshCommands();
    }
}
