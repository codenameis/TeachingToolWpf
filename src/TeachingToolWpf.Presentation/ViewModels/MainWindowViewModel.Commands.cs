using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Windows.Input;
using TeachingToolWpf.Commands;
using TeachingToolWpf.Models;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Interaction;
using VisionCanvas.Presentation;
using VisionCanvas.Rendering;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.ViewModels;

/// <summary>메인 창의 명령 40개를 한곳에서 연결한다. 생성자는 객체 조립만 남긴다.</summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>버튼·단축키가 호출할 명령을 만든다. 실행 조건(CanExecute)은 각 명령이 스스로 갖는다.</summary>
    /// <remarks>
    /// 명령 속성은 생성자에서 직접 대입하지 않고 이 메서드가 채운다. 그래서 컴파일러는 생성자가 끝날 때
    /// 속성이 비어 있다고 경고한다. 아래 <see cref="MemberNotNullAttribute"/> 목록은 "이 메서드가 돌아오면
    /// 이 속성들은 모두 null이 아니다"라고 컴파일러에게 알리는 선언이다. 목록이 곧 연결하는 명령의 전체 목록이며,
    /// 하나라도 빠뜨리면 컴파일러가 경고하므로 배선 누락을 빌드에서 잡을 수 있다.
    /// </remarks>
    [MemberNotNull(
        nameof(PreviousRowPage), nameof(NextRowPage))]                                   // 목록 페이지
    [MemberNotNull(
        nameof(SelectTool), nameof(PanTool), nameof(DrawTool), nameof(AddRoi),           // 도구 선택과
        nameof(AddParentRoi), nameof(DrawParentRoi), nameof(AddRepeatedTest),            // ROI 생성
        nameof(CompletePolygon), nameof(RemovePolygonPoint), nameof(CancelPolygon))]
    [MemberNotNull(
        nameof(ZoomIn), nameof(ZoomOut), nameof(ApplyZoom), nameof(Fit), nameof(ActualSize), // 화면 이동·확대
        nameof(Left), nameof(Right), nameof(Up), nameof(Down))]
    [MemberNotNull(
        nameof(Delete), nameof(Copy), nameof(Paste), nameof(Group), nameof(Ungroup),     // 편집과 이력
        nameof(ChooseChildRois), nameof(AttachToParent), nameof(ResetAngle), nameof(RotateLeft),
        nameof(RotateRight), nameof(ApplyProperties), nameof(SelectAll), nameof(Undo), nameof(Redo))]
    [MemberNotNull(
        nameof(CancelImport), nameof(Initialize), nameof(OpenImage),                     // 이미지·라이브·샘플
        nameof(Sample8K), nameof(Sample24K), nameof(PatternSample), nameof(PrimitiveSample),
        nameof(StartLive), nameof(StopLive), nameof(RestartRenderer))]
    private void WireCommands(IImageDisplay display)
    {
        // 목록 페이지
        PreviousRowPage = Command(() => ChangeRowPage(_rowPage - 1), () => _rowPage > 0);
        NextRowPage = Command(() => ChangeRowPage(_rowPage + 1), () => (_rowPage + 1) * RowPageSize < Rows.Count);

        // 도구 선택과 ROI 생성
        SelectTool = Command(() => SetTool(EditorTool.Select));
        PanTool = Command(() => SetTool(EditorTool.Pan), navigation: true);
        DrawTool = Command(() => StartRoiDrawing(SelectedGroupParent), CanCreateRoi);
        AddRoi = Command(AddRoiInView, CanCreateRoi);
        AddParentRoi = Command(AddParentRoiInView, HasImage);
        DrawParentRoi = Command(StartParentDrawing, HasImage);
        AddRepeatedTest = Command(AddRepeatedTestInDocument, HasImage);
        CompletePolygon = Command(Input.CompletePolygon, () => Input.CanCompletePolygon);
        RemovePolygonPoint = Command(Input.RemovePolygonPoint, () => Input.PolygonPointCount > 0);
        CancelPolygon = Command(Input.Cancel, () => Input.PolygonPointCount > 0);

        // 화면 이동·확대
        ZoomIn = Command(() => Zoom(1.25), HasImage, true);
        ZoomOut = Command(() => Zoom(.8), HasImage, true);
        ApplyZoom = Command(SetZoom, HasImage, true);
        Fit = Command(FitImage, HasImage, true);
        ActualSize = Command(() => ZoomTo(1 / Layout.Dpi), HasImage, true);
        Left = Command(() => Pan(100, 0), navigation: true);
        Right = Command(() => Pan(-100, 0), navigation: true);
        Up = Command(() => Pan(0, 100), navigation: true);
        Down = Command(() => Pan(0, -100), navigation: true);

        // 편집과 이력
        Delete = Async(() => EditCompletion = PrepareEditAsync("선택 ROI 삭제", Session.PrepareDeleteAsync), HasSelection);
        Copy = Command(CopySelection, HasSelection);
        Paste = Async(() => PasteCompletion = PasteAsync(), () => HasImage() && _clipboard.HasContent);
        Group = Async(() => EditCompletion = PrepareEditAsync("부모 ROI로 그룹화", Session.PrepareGroupAsync), () => !HasRepeatedSelection && Session.State.SelectionFacts.CanGroup);
        ChooseChildRois = Command(ChooseRoisToAttach, () => HasSelection() && !HasRepeatedSelection &&
            Session.State.Selection.All(id => Session.State.Rois[id].ParentId is null));
        AttachToParent = Async(AttachRoisToParentAsync, () => _roisToAttach.Length > 0 && CanAttachToSelectedRoi());
        Ungroup = Async(() => EditCompletion = PrepareEditAsync("그룹 해제", Session.PrepareUngroupAsync), () => !HasRepeatedSelection && Session.State.SelectionFacts.HasGroup);
        ResetAngle = Command(Session.ResetAngles, () => Session.State.SelectionCount == 1 && !HasRepeatedSelection);
        RotateLeft = Command(() => Rotate(-15), () => Session.State.SelectionCount == 1);
        RotateRight = Command(() => Rotate(15), () => Session.State.SelectionCount == 1);
        ApplyProperties = Command(Properties.Apply, () => Properties.HasSingleSelection);
        SelectAll = Async(() => EditCompletion = PrepareEditAsync("전체 선택", Session.PrepareSelectAllAsync), () => Session.State.RoiCount != 0);
        Undo = Async(() => EditCompletion = HistoryAsync(false), () => Session.CanUndo);
        Redo = Async(() => EditCompletion = HistoryAsync(true), () => Session.CanRedo);

        // 이미지·라이브·샘플
        CancelImport = new(Images.Cancel, () => Images.IsBusy);
        Initialize = Async(InitializeAsync);
        OpenImage = Async(OpenAsync, () => _selectImage is not null);
        Sample8K = Async(() => LoadSampleAsync(8192, 8192));
        Sample24K = Async(() => LoadSampleAsync(24576, 32768));
        PatternSample = Async(LoadPatternSampleAsync);
        PrimitiveSample = Async(LoadPrimitiveSampleAsync, () => Inspections.IsIdle);
        StartLive = Async(StartLiveAsync, () => !Live.IsRunning);
        StopLive = Async(Live.StopAsync, () => Live.IsRunning);
        RestartRenderer = Async(display.RestartAsync);
    }

    private async Task LoadPrimitiveSampleAsync()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "primitive-shapes.png");
        await Images.ReplaceAsync(new(path, "일반 도형 샘플", 640, 480, ImageFileVersion.Read(path)));
        if (Session.State.Image?.Key == path)
        {
            Inspections.Selected = InspectionToolKind.PrimitiveShapes;
        }
    }

    /// <summary>동기 명령을 만들어 <see cref="RefreshCommands"/> 목록에 넣는다.</summary>
    /// <param name="navigation">true면 편집 잠금과 무관하게 화면 이동만 허용한다.</param>
    private RelayCommand Command(Action action, Func<bool>? available = null, bool navigation = false)
    {
        var command = new RelayCommand(() => RunGuarded(action), () => (navigation ? CanNavigate : CanEdit) && !Input.IsDragging && (available?.Invoke() ?? true));
        _syncCommands.Add(command);
        return command;
    }

    /// <summary>비동기 명령을 만들어 <see cref="RefreshCommands"/> 목록에 넣는다. 실패는 상태 표시줄로 보낸다.</summary>
    private AsyncCommand Async(Func<Task> action, Func<bool>? available = null)
    {
        var command = new AsyncCommand(action, ReportError, () => CanEdit && !Input.IsDragging && (available?.Invoke() ?? true));
        _asyncCommands.Add(command);
        return command;
    }
}
