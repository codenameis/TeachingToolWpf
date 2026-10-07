using System.Globalization;
using VisionCanvas.Core.Editing;
using VisionGeometry;

namespace TeachingToolWpf.Presentation;

public sealed class RoiPropertiesViewModel(EditorSession editor) : ObservableObject
{
    private Guid? _id;
    private string _name = "";

    private string _x = "";

    private string _y = "";

    private string _width = "";

    private string _height = "";

    private string _angle = "";

    private string _relative = "—";
    private int _selectedType;
    private string _searchHorizontal = "20";
    private string _searchVertical = "20";
    private bool _isNameVisible = true;

    public bool HasSingleSelection => _id is not null;

    public bool IsGrouping => _id is { } id && editor.State.IsGroupingRoi(id);

    public bool CanChangeType => HasSingleSelection && !IsGrouping;

    public IReadOnlyList<string> Types { get; } = ["일반 ROI", "검색 범위 ROI"];

    public int SelectedType
    {
        get => _selectedType;
        set
        {
            if (Set(ref _selectedType, value))
            {
                Notify(nameof(HasSearchRange));
            }
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

    public string ShapeDescription => _id is { } value ? editor.State.Rois[value].Geometry.ShapeName : "";

    public string SizeHint => _id is { } value && editor.State.Rois[value].Geometry.Shape.Kind == RoiShapeKind.Circle
        ? "원: 너비·높이는 같은 지름으로 적용됩니다." : "원본 이미지 픽셀 기준";

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    public string X
    {
        get => _x;
        set => Set(ref _x, value);
    }

    public string Y
    {
        get => _y;
        set => Set(ref _y, value);
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
        get => _angle;
        set => Set(ref _angle, value);
    }

    public string RelativeAngle
    {
        get => _relative;
        private set => Set(ref _relative, value);
    }

    private static string Format(double value) => value.ToString("0.###", CultureInfo.CurrentCulture);

    public void Refresh()
    {
        _id = editor.State.Selection.Count == 1 ? editor.State.Selection.Single() : null;
        Notify(nameof(HasSingleSelection));
        Notify(nameof(ShapeDescription));
        Notify(nameof(SizeHint));
        Notify(nameof(IsGrouping));
        Notify(nameof(CanChangeType));
        Notify(nameof(HasSearchRange));
        if (_id is not { } value)
        {
            Name = X = Y = Width = Height = Angle = "";
            RelativeAngle = "—";
            return;
        }
        var roi = editor.State.Rois[value];
        var geometry = editor.TransformPreview?.Geometry(roi) ?? roi.Geometry;
        Name = roi.Name;
        IsNameVisible = roi.IsNameVisible;
        SelectedType = roi.SearchRange is null || IsGrouping ? 0 : 1;
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
        if (_id is not { } value)
        {
            return;
        }

        static double Parse(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) && double.IsFinite(value)
            ? value : throw new ArgumentException("속성에는 유한한 숫자를 입력하세요.");
        var original = editor.State.Rois[value].Geometry;
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
        var searchRange = IsGrouping ? editor.State.Rois[value].SearchRange
            : HasSearchRange ? new RoiSearchRange(Parse(SearchHorizontal), Parse(SearchVertical)) : null;
        editor.SetProperties(value, Name, original.WithFrame(new(Parse(X), Parse(Y)), newWidth, newHeight, Parse(Angle)), searchRange, IsNameVisible);
    }
}
