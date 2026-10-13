using System.Globalization;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Interaction;
using VisionGeometry;

namespace TeachingToolWpf.Presentation;

public sealed record MaskRow(int Index, string Name);

/// <summary>마스크 목록과 수치 입력 초안. 문서 변경과 이력은 EditorSession에 위임한다.</summary>
public sealed class RoiMaskViewModel : ObservableObject
{
    private readonly EditorSession _session;
    private readonly InteractionController _input;
    private readonly Action<string> _status;
    private Roi? _owner;
    private bool _canEdit;
    private bool _showMasks = true;
    private int _selectedIndex = -1;
    private string _x = "0";
    private string _y = "0";
    private string _width = "0";
    private string _height = "0";

    public RoiMaskViewModel(EditorSession session, InteractionController input, Action<string> status)
    {
        _session = session;
        _input = input;
        _status = status;
        DrawRectangle = new(() => Start(MaskShape.Rectangle), CanDraw);
        DrawEllipse = new(() => Start(MaskShape.Ellipse), CanDraw);
        Finish = new(() => _input.Tool = EditorTool.Select, () => IsDrawing);
        Remove = new(() => Edit(() => _session.RemoveMask(_owner!.Id, SelectedIndex)), () => CanChange && HasSelectedMask);
        Clear = new(() => Edit(() => _session.ClearMasks(_owner!.Id)), () => CanChange && Rows.Count > 0);
        Apply = new(ApplyValues, () => CanChange && HasSelectedMask);
    }

    public event Action? Changed;
    public RelayCommand DrawRectangle
    {
        get;
    }
    public RelayCommand DrawEllipse
    {
        get;
    }
    public RelayCommand Finish
    {
        get;
    }
    public RelayCommand Remove
    {
        get;
    }
    public RelayCommand Clear
    {
        get;
    }
    public RelayCommand Apply
    {
        get;
    }
    public IReadOnlyList<MaskRow> Rows { get; private set; } = [];
    public bool HasOwner => _owner is not null;
    public bool HasMasks => Rows.Count > 0;
    public bool HasSelectedMask => _owner is not null && SelectedIndex >= 0 && SelectedIndex < _owner.Masks.Length;
    public bool IsDrawing => _input.Tool == EditorTool.Mask;
    public bool CanToggleVisibility => !IsDrawing;
    public bool IsRectangleDrawing => IsDrawing && _input.MaskShape == MaskShape.Rectangle;
    public bool IsEllipseDrawing => IsDrawing && _input.MaskShape == MaskShape.Ellipse;
    private bool CanChange => _canEdit && !_input.IsDragging && !_session.IsPreviewing && HasOwner;
    private bool CanDraw() => CanChange && Rows.Count < EditorSession.MaximumMasksPerRoi;
    public string Summary => _owner is null ? "검사 ROI 하나를 선택하세요." : $"{_owner.Name} · 제외 영역 {Rows.Count}개";
    public string Hint => IsDrawing ? "ROI 안에서 드래그하세요. 연속 추가 가능 · 완료/Esc로 종료" :
        "학습: 가린 픽셀 제외\n선택 검사: 마스크와 겹치는 대상 제외";
    public string Error { get; private set; } = "";
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

    public bool ShowMasks
    {
        get => _showMasks;
        set
        {
            if (Set(ref _showMasks, value))
            {
                Changed?.Invoke();
            }
        }
    }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (Set(ref _selectedIndex, value))
            {
                ReadValues();
                RefreshCommands();
                Notify(nameof(HasSelectedMask));
                Changed?.Invoke();
            }
        }
    }

    public void Update(bool canEdit)
    {
        _canEdit = canEdit;
        var state = _session.State;
        var owner = state.SelectionCount == 1 && state.Selection.Count == 1 && !state.IsGroupingRoi(state.Selection.Single())
            ? state.Rois[state.Selection.Single()] : null;
        if (_owner != owner)
        {
            var sameOwner = _owner?.Id == owner?.Id;
            var added = sameOwner && owner?.Masks.Length > _owner?.Masks.Length;
            _owner = owner;
            Rows = owner?.Masks.Select((mask, index) => new MaskRow(index,
                $"{index + 1:00}  ·  {(mask.Shape == MaskShape.Rectangle ? "사각형" : "타원")}")).ToArray() ?? [];
            _selectedIndex = owner is null || Rows.Count == 0 ? -1 : added || !sameOwner ? Rows.Count - 1 : Math.Min(_selectedIndex, Rows.Count - 1);
            Error = "";
            ReadValues();
            Notify(null);
        }
        Notify(nameof(IsDrawing));
        Notify(nameof(CanToggleVisibility));
        Notify(nameof(IsRectangleDrawing));
        Notify(nameof(IsEllipseDrawing));
        Notify(nameof(Hint));
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        foreach (var command in new[] { DrawRectangle, DrawEllipse, Finish, Remove, Clear, Apply })
        {
            command.Refresh();
        }
    }

    private void Start(MaskShape shape)
    {
        ShowMasks = true;
        _input.StartMask(shape);
        _status(Hint);
    }

    private void ReadValues()
    {
        if (!HasSelectedMask)
        {
            return;
        }
        var bounds = _owner!.Masks[SelectedIndex].Bounds;
        X = Format(bounds.Left * _owner.Geometry.Width);
        Y = Format(bounds.Top * _owner.Geometry.Height);
        Width = Format(bounds.Width * _owner.Geometry.Width);
        Height = Format(bounds.Height * _owner.Geometry.Height);
    }

    private static string Format(double value) => value.ToString("0.###", CultureInfo.CurrentCulture);

    private static double ParseNormalized(string text, double original, double length)
    {
        // 표시만 반올림한다. 수정하지 않은 축은 원래 정밀도를 유지한다.
        if (text == Format(original * length))
        {
            return original;
        }
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) || !double.IsFinite(value))
        {
            throw new ArgumentException("위치와 크기에 유효한 숫자를 입력하세요.");
        }
        return value / length;
    }

    private void ApplyValues()
    {
        Edit(() =>
        {
            var geometry = _owner!.Geometry;
            var bounds = _owner.Masks[SelectedIndex].Bounds;
            var x = ParseNormalized(X, bounds.Left, geometry.Width);
            var y = ParseNormalized(Y, bounds.Top, geometry.Height);
            var width = ParseNormalized(Width, bounds.Width, geometry.Width);
            var height = ParseNormalized(Height, bounds.Height, geometry.Height);
            var right = x == bounds.Left && width == bounds.Width ? bounds.Right : x + width;
            var bottom = y == bounds.Top && height == bounds.Height ? bounds.Bottom : y + height;
            var region = new MaskRegion(_owner.Masks[SelectedIndex].Shape,
                new(x, y, right, bottom));
            _session.ReplaceMask(_owner.Id, SelectedIndex, region);
        });
    }

    private void Edit(Action action)
    {
        try
        {
            action();
            Error = "";
        }
        catch (ArgumentException error)
        {
            Error = error.Message;
        }
        catch (InvalidOperationException error)
        {
            Error = error.Message;
        }
        Notify(nameof(Error));
    }
}
