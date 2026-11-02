using System.Globalization;
using TeachingToolWpf.Infrastructure;
using VisionCanvas.Rendering;

namespace TeachingToolWpf.ViewModels;

/// <summary>설정 페이지의 역할별 입력. 유효한 전체 팔레트를 만들기 전에는 표시를 바꾸지 않는다.</summary>
public sealed class RoiStyles(string name) : ObservableObject
{
    private string _color = "";
    private string _selectedColor = "";
    private string _multiSelectedColor = "";
    private string _thickness = "";

    public string Name { get; } = name;
    public string Color
    {
        get => _color; set => Set(ref _color, value);
    }
    public string SelectedColor
    {
        get => _selectedColor; set => Set(ref _selectedColor, value);
    }
    public string MultiSelectedColor
    {
        get => _multiSelectedColor; set => Set(ref _multiSelectedColor, value);
    }
    public string Thickness
    {
        get => _thickness; set => Set(ref _thickness, value);
    }

    internal RoiStrokeStyle Build(RoiStrokeStyle current) => new(
        DisplayStyleInput.ParseColor(Color, current.Color, Name + " 기본"),
        DisplayStyleInput.ParseColor(SelectedColor, current.SelectedColor, Name + " 단일 선택"),
        DisplayStyleInput.ParseThickness(Thickness, Name),
        DisplayStyleInput.ParseColor(MultiSelectedColor, current.MultiSelectedColor, Name + " 다중 선택"));

    internal void Refresh(RoiStrokeStyle style)
    {
        Color = DisplayStyleInput.FormatColor(style.Color);
        SelectedColor = DisplayStyleInput.FormatColor(style.SelectedColor);
        MultiSelectedColor = DisplayStyleInput.FormatColor(style.MultiSelectedColor);
        Thickness = style.ThicknessDip.ToString("R", CultureInfo.CurrentCulture);
    }
}
