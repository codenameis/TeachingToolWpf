using System.Globalization;
using TeachingToolWpf.Commands;
using TeachingToolWpf.Infrastructure;
using VisionCanvas.Layers;

namespace TeachingToolWpf.ViewModels;

public sealed class CrosshairSettingsViewModel : ObservableObject
{
    private static readonly ImageCrosshairGraphic s_default = new(new(1, .85f, .2f, 1));
    private bool _isVisible;
    private string _color = "";
    private string _thickness = "";
    private string _error = "";

    public ImageCrosshairGraphic Graphic { get; private set; } = s_default;
    public string Color
    {
        get => _color; set => Set(ref _color, value);
    }
    public string Thickness
    {
        get => _thickness; set => Set(ref _thickness, value);
    }
    public string Error
    {
        get => _error; private set => Set(ref _error, value);
    }
    public string ToggleLabel => IsVisible ? "크로스헤어 끄기" : "크로스헤어 켜기";
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (Set(ref _isVisible, value))
            {
                Notify(nameof(ToggleLabel));
                Changed?.Invoke();
            }
        }
    }

    public RelayCommand Toggle
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

    public CrosshairSettingsViewModel()
    {
        Toggle = new(() => IsVisible = !IsVisible);
        Apply = new(ApplySettings);
        Reset = new(() => { Publish(s_default); Refresh(); });
        Refresh();
    }

    private void ApplySettings()
    {
        ImageCrosshairGraphic next;
        try
        {
            next = new(DisplayStyleInput.ParseColor(Color, Graphic.Color, "크로스헤어"),
                DisplayStyleInput.ParseThickness(Thickness, "크로스헤어"));
        }
        catch (ArgumentException error)
        {
            Error = error.Message;
            return;
        }
        Publish(next);
        Refresh();
    }

    private void Publish(ImageCrosshairGraphic graphic)
    {
        if (Graphic == graphic)
        {
            return;
        }
        Graphic = graphic;
        Notify(nameof(Graphic));
        Changed?.Invoke();
    }

    private void Refresh()
    {
        Color = DisplayStyleInput.FormatColor(Graphic.Color);
        Thickness = Graphic.Thickness.ToString("R", CultureInfo.CurrentCulture);
        Error = "";
    }
}
