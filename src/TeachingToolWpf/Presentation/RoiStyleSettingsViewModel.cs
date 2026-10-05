using System.Globalization;
using System.Numerics;
using System.Windows.Media;
using VisionCanvas.Rendering;

namespace TeachingToolWpf.Presentation;

/// <summary>Application display settings; publishes one immutable palette without editing the document.</summary>
public sealed class RoiStyleSettingsViewModel : ObservableObject
{
    private string _defaultThickness = "";

    private string _childThickness = "";

    private string _parentThickness = "";

    private string _error = "";

    public RoiStylePalette Palette
    {
        get;
        private set;
    } = RoiStylePalette.Standard;

    public string DefaultThickness
    {
        get => _defaultThickness;
        set => Set(ref _defaultThickness, value);
    }

    public string ChildThickness
    {
        get => _childThickness;
        set => Set(ref _childThickness, value);
    }

    public string ParentThickness
    {
        get => _parentThickness;
        set => Set(ref _parentThickness, value);
    }

    public string Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }

    public Brush DefaultColor
    {
        get;
    }

    public Brush ChildColor
    {
        get;
    }

    public Brush ParentColor
    {
        get;
    }

    public RelayCommand Apply
    {
        get;
    }

    public RelayCommand Reset
    {
        get;
    }

    public event Action? Changed;

    public RoiStyleSettingsViewModel()
    {
        DefaultColor = Swatch(Palette.Default.Color);
        ChildColor = Swatch(Palette.Child.Color);
        ParentColor = Swatch(Palette.Parent.Color);
        Apply = new(ApplySettings);
        Reset = new(ResetSettings);
        RefreshDrafts();
    }

    private void ApplySettings()
    {
        RoiStylePalette next;
        try
        {
            next = new(Palette.Default.WithThickness(Parse(DefaultThickness, "일반 ROI")),
                Palette.Child.WithThickness(Parse(ChildThickness, "자식 ROI")),
                Palette.Parent.WithThickness(Parse(ParentThickness, "부모 ROI")));
        }
        catch (ArgumentException ex)
        {
            Error = ex.Message;
            return;
        }
        Error = "";
        Publish(next);
        RefreshDrafts();
    }

    private static float Parse(string text, string role)
    {
        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value)
            || !float.IsFinite(value) || value < RoiStrokeStyle.MinimumThickness || value > RoiStrokeStyle.MaximumThickness)
        {
            throw new ArgumentException($"{role} 선 두께에 0.5~8 사이의 숫자를 입력하세요.");
        }

        return value;
    }

    private void ResetSettings()
    {
        Error = "";
        Publish(RoiStylePalette.Standard);
        RefreshDrafts();
    }

    private void Publish(RoiStylePalette next)
    {
        if (Palette == next)
        {
            return;
        }

        Palette = next;
        Notify(nameof(Palette));
        Changed?.Invoke();
    }

    private void RefreshDrafts()
    {
        DefaultThickness = Palette.Default.ThicknessDip.ToString("R", CultureInfo.CurrentCulture);
        ChildThickness = Palette.Child.ThicknessDip.ToString("R", CultureInfo.CurrentCulture);
        ParentThickness = Palette.Parent.ThicknessDip.ToString("R", CultureInfo.CurrentCulture);
    }

    private static Brush Swatch(Vector4 color)
    {
        static byte Channel(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255), 0, 255);
        var brush = new SolidColorBrush(Color.FromArgb(Channel(color.W), Channel(color.X), Channel(color.Y), Channel(color.Z)));
        brush.Freeze();
        return brush;
    }
}
