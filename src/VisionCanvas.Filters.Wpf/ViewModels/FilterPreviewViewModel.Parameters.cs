using System.Globalization;
using VisionCanvas.Core.Editing;

namespace VisionCanvas.Filters.Wpf.ViewModels;

public sealed partial class FilterPreviewViewModel
{
    private bool _parameterEditing;
    private FilterSettings? _parameterOriginal;
    private FilterSettings? _draftSettings;
    private IReadOnlyList<Roi>? _parameterRois;
    private int _inputMaximum = 255;
    private double _radiusValue = 3;
    private double _sigmaValue = 1;
    private double _thresholdValue = 128;
    private double _edgeLowValue = 50;
    private double _edgeHighValue = 150;
    private string _validationMessage = "";

    public string ValidationMessage
    {
        get => _validationMessage;
        private set => Set(ref _validationMessage, value);
    }

    public RelayCommand BeginParameterEdit
    {
        get;
    }
    public RelayCommand EndParameterEdit
    {
        get;
    }
    public RelayCommand CancelParameterEdit
    {
        get;
    }

    public double RadiusValue
    {
        get => _radiusValue;
        set => Radius = Math.Round(value).ToString(CultureInfo.CurrentCulture);
    }
    public double SigmaValue
    {
        get => _sigmaValue;
        set => Sigma = Math.Round(value, 2).ToString(CultureInfo.CurrentCulture);
    }
    public double ThresholdValue
    {
        get => _thresholdValue;
        set => Threshold = Math.Round(value).ToString(CultureInfo.CurrentCulture);
    }
    public double EdgeLowValue
    {
        get => _edgeLowValue;
        set => EdgeLow = Math.Round(value).ToString(CultureInfo.CurrentCulture);
    }
    public double EdgeHighValue
    {
        get => _edgeHighValue;
        set => EdgeHigh = Math.Round(value).ToString(CultureInfo.CurrentCulture);
    }
    public double ThresholdMaximum => Math.Max(IsBinarize ? 255 : _inputMaximum, _thresholdValue);

    private void RefreshSliderValues()
    {
        UpdateSlider(Radius, 1, 31, ref _radiusValue, nameof(RadiusValue));
        UpdateSlider(Sigma, .01, 100, ref _sigmaValue, nameof(SigmaValue));
        if (UpdateSlider(Threshold, 0, IsBinarize ? 255 : 65535, ref _thresholdValue, nameof(ThresholdValue)))
        {
            Notify(nameof(ThresholdMaximum));
        }
        UpdateSlider(EdgeLow, 0, 255, ref _edgeLowValue, nameof(EdgeLowValue));
        UpdateSlider(EdgeHigh, 0, 255, ref _edgeHighValue, nameof(EdgeHighValue));
    }

    private bool UpdateSlider(string text, double minimum, double maximum, ref double field, string property)
    {
        // Invalid/incomplete text is never coerced back through a slider into a valid recipe.
        return double.TryParse(text, CultureInfo.CurrentCulture, out var value) && double.IsFinite(value) &&
            value >= minimum && value <= maximum && Set(ref field, value, property);
    }

    private bool TryReadSettings(out FilterSettings settings)
    {
        try
        {
            static double Number(string value) => double.Parse(value, CultureInfo.CurrentCulture);
            settings = new(SelectedFilter, UsesThreshold ? Number(Threshold) : 128,
                IsEdge ? Number(EdgeLow) : 50, IsEdge ? Number(EdgeHigh) : 150,
                UsesRadius ? int.Parse(Radius, CultureInfo.CurrentCulture) : 3, UsesSigma ? Number(Sigma) : 1,
                IsIndustrial ? Execution : RoiFilterExecution.CpuOnly);
            settings.Validate();
            ValidationMessage = "";
            return true;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            settings = new(RoiFilterKind.None);
            Status = ValidationMessage = "필터 값 확인: 임계값 기존 0–255 / 원본 0–65535, Edge 하한 < 상한, 반경 정수 1–31, sigma 0 초과–100 이하.";
            return false;
        }
    }

    private void LoadSettings(FilterSettings settings)
    {
        ValidationMessage = "";
        Set(ref _selectedFilter, settings.Kind, nameof(SelectedFilter));
        Set(ref _threshold, settings.Threshold.ToString(CultureInfo.CurrentCulture), nameof(Threshold));
        Set(ref _edgeLow, settings.EdgeLow.ToString(CultureInfo.CurrentCulture), nameof(EdgeLow));
        Set(ref _edgeHigh, settings.EdgeHigh.ToString(CultureInfo.CurrentCulture), nameof(EdgeHigh));
        Set(ref _radius, settings.Radius.ToString(CultureInfo.CurrentCulture), nameof(Radius));
        Set(ref _sigma, settings.Sigma.ToString(CultureInfo.CurrentCulture), nameof(Sigma));
        Set(ref _execution, settings.Execution, nameof(Execution));
        NotifyFilterFields();
    }

    private void StartParameterEdit()
    {
        if (!TryReadSettings(out var settings))
        {
            return;
        }
        _parameterOriginal = _draftSettings = settings;
        // One snapshot per gesture; pointer/value updates replace only the small settings record.
        _parameterRois = _document.Selection.Select(id => _document.Rois[id]).ToArray();
        _parameterEditing = true;
        RefreshCommands();
    }

    private void CompleteParameterEdit()
    {
        var valid = TryReadSettings(out _);
        ResetParameterEdit();
        if (valid)
        {
            Register();
        }
        else
        {
            Schedule(force: true, invalidate: true);
        }
        RefreshCommands();
    }

    private void CancelParameterChanges()
    {
        var original = _parameterOriginal;
        ResetParameterEdit();
        if (original is not null)
        {
            LoadSettings(original);
        }
        Schedule(force: true, invalidate: true);
        RefreshCommands();
    }

    private void ResetParameterEdit()
    {
        _parameterEditing = false;
        _parameterOriginal = _draftSettings = null;
        _parameterRois = null;
    }
}
