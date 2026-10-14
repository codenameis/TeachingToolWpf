using System.Globalization;
using TeachingToolWpf.Infrastructure;
using VisionCanvas.Core.Editing;
using VisionGeometry;

namespace TeachingToolWpf.ViewModels;

public sealed class RoiPropertiesViewModel(EditorSession editor) : ObservableObject
{
    private Guid? _selectedRoiId;
    private RepeatedRoiCell? _selectedCell;
    private string _name = "";

    private string _centerX = "";

    private string _centerY = "";

    private string _width = "";

    private string _height = "";

    private string _angleDegrees = "";

    private string _relativeAngle = "—";
    private int _selectedTypeIndex;
    private string _searchHorizontal = "20";
    private string _searchVertical = "20";
    private bool _isNameVisible = true;

    public bool HasSingleSelection => _selectedRoiId is not null || _selectedCell is not null;
    public bool CanEditMetadata => _selectedRoiId is not null;

    public bool IsGroup => _selectedRoiId is { } id && editor.State.IsGroupRoi(id);
    public string GroupDescription => _selectedRoiId is { } id && IsGroup
        ? $"{RoiHierarchy.LevelName(editor.State.Snapshot.GroupLevels.GetValueOrDefault(id))} · 자식 관리 전용 (검사 대상 아님)" : "";

    public bool CanChangeType => CanEditMetadata && !IsGroup;

    public IReadOnlyList<string> Types { get; } = ["일반 ROI", "검색 범위 ROI"];

    public int SelectedType
    {
        get => _selectedTypeIndex;
        set
        {
            if (_selectedTypeIndex == value || !CanChangeType || value is < 0 or > 1)
            {
                return;
            }
            var roi = editor.State.Rois[_selectedRoiId!.Value];
            // 타입만 확정한다. 이름·좌표·여유 숫자의 미완성 입력은 함께 적용하지 않는다.
            editor.SetProperties(roi.Id, roi.Name, roi.Geometry,
                value == 1 ? new RoiSearchRange(20, 20) : null, roi.IsNameVisible);
            Set(ref _selectedTypeIndex, value);
            Notify(nameof(HasSearchRange));
        }
    }

    public bool HasSearchRange => CanChangeType && SelectedType == 1;

    public string SearchHorizontal
    {
        get => _searchHorizontal;
        set => Set(ref _searchHorizontal, value);
    }

    public string SearchVertical
    {
        get => _searchVertical;
        set => Set(ref _searchVertical, value);
    }

    public bool IsNameVisible
    {
        get => _isNameVisible;
        set => Set(ref _isNameVisible, value);
    }

    public string ShapeDescription => SelectedGeometry?.ShapeName ?? "";

    public string SizeHint => _selectedRoiId is { } value && editor.State.Rois[value].Geometry.Shape.Kind == RoiShapeKind.Circle
        ? "원: 너비·높이는 같은 지름으로 적용됩니다." : "원본 이미지 픽셀 기준";

    private RoiGeometry? SelectedGeometry => _selectedRoiId is { } id ? editor.State.Rois[id].Geometry
        : _selectedCell is { } cell ? editor.State.RepeatedRois[cell.BatchId].Geometry(cell.Index) : null;

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    public string X
    {
        get => _centerX;
        set => Set(ref _centerX, value);
    }

    public string Y
    {
        get => _centerY;
        set => Set(ref _centerY, value);
    }

    public string Width
    {
        get => _width;
        set => Set(ref _width, value);
    }

    public string Height
    {
        get => _height;
        set => Set(ref _height, value);
    }

    public string Angle
    {
        get => _angleDegrees;
        set => Set(ref _angleDegrees, value);
    }

    public string RelativeAngle
    {
        get => _relativeAngle;
        private set => Set(ref _relativeAngle, value);
    }

    private static string Format(double value) => value.ToString("0.###", CultureInfo.CurrentCulture);

    public void Refresh()
    {
        _selectedRoiId = editor.State.SelectionCount == 1 && editor.State.Selection.Count == 1 ? editor.State.Selection.Single() : null;
        _selectedCell = editor.State.SingleRepeatedCell;
        Notify(nameof(HasSingleSelection));
        Notify(nameof(CanEditMetadata));
        Notify(nameof(ShapeDescription));
        Notify(nameof(SizeHint));
        Notify(nameof(IsGroup));
        Notify(nameof(GroupDescription));
        Notify(nameof(CanChangeType));
        Notify(nameof(HasSearchRange));
        if (_selectedCell is { } cell)
        {
            var selected = SelectedGeometry!;
            if (editor.TransformPreview is { } preview)
            {
                selected = selected.WithFrame(preview.Transform.Apply(selected.Center), selected.Width, selected.Height, selected.Angle + preview.Transform.AngleDegrees);
            }

            Name = $"{editor.State.RepeatedRois[cell.BatchId].Name} · 셀 {cell.Index:N0}";
            X = Format(selected.Center.X);
            Y = Format(selected.Center.Y);
            Width = Format(selected.Width);
            Height = Format(selected.Height);
            Angle = Format(selected.Angle);
            RelativeAngle = $"배치 {cell.BatchId:N} / 셀 {cell.Index}";
            return;
        }
        if (_selectedRoiId is not { } value)
        {
            Name = X = Y = Width = Height = Angle = "";
            RelativeAngle = "—";
            return;
        }
        var roi = editor.State.Rois[value];
        var geometry = editor.TransformPreview?.Geometry(roi) ?? roi.Geometry;
        Name = roi.Name;
        IsNameVisible = roi.IsNameVisible;
        Set(ref _selectedTypeIndex, roi.SearchRange is null || IsGroup ? 0 : 1, nameof(SelectedType));
        Notify(nameof(HasSearchRange));
        SearchHorizontal = Format(roi.SearchRange?.Horizontal ?? 20);
        SearchVertical = Format(roi.SearchRange?.Vertical ?? 20);
        X = Format(geometry.Center.X);
        Y = Format(geometry.Center.Y);
        Width = Format(geometry.Width);
        Height = Format(geometry.Height);
        Angle = Format(geometry.Angle);
        RelativeAngle = roi.ParentId is { } parent ? Format(RoiGeometry.NormalizeAngle(geometry.Angle -
            (editor.TransformPreview?.Geometry(editor.State.Rois[parent]) ?? editor.State.Rois[parent].Geometry).Angle)) + "°" : "루트 ROI";
    }

    public void Apply()
    {
        if (_selectedRoiId is null && _selectedCell is null)
        {
            return;
        }
        var value = _selectedRoiId.GetValueOrDefault();

        static double Parse(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) && double.IsFinite(value)
            ? value : throw new ArgumentException("속성에는 유한한 숫자를 입력하세요.");
        var original = SelectedGeometry!;
        var newWidth = Parse(Width);
        var newHeight = Parse(Height);
        if (original.Shape.Kind == RoiShapeKind.Circle)
        {
            if (Width != Format(original.Width))
            {
                newHeight = newWidth;
            }
            else
            {
                newWidth = newHeight;
            }
        }
        if (_selectedCell is { } cell)
        {
            editor.SetRepeatedGeometry(cell, original.WithFrame(new(Parse(X), Parse(Y)), newWidth, newHeight, Parse(Angle)));
            return;
        }
        var searchRange = IsGroup ? editor.State.Rois[value].SearchRange
            : HasSearchRange ? new RoiSearchRange(Parse(SearchHorizontal), Parse(SearchVertical)) : null;
        editor.SetProperties(value, Name, original.WithFrame(new(Parse(X), Parse(Y)), newWidth, newHeight, Parse(Angle)), searchRange, IsNameVisible);
    }
}
