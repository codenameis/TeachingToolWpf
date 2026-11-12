using System.Globalization;
using VisionCanvas.Core.Editing;
using VisionImaging.Abstractions;

namespace VisionCanvas.Filters.Wpf.ViewModels;

/// <summary>필터 파라미터 텍스트/슬라이더 초안 값, 검증 메시지, 일시적 드래그 편집 상태를 관리한다.</summary>
public sealed class FilterParameterDraft
{
    public const string ValidationFailureGuidance =
        "필터 값 확인: 임계값 기존 0–255 / 원본 0–65535, Edge 하한 < 상한, 반경 정수 1–31, sigma 0 초과–100 이하.";

    private const int ByteMaximum = 255;
    private const int Gray16Maximum = 65535;
    private const int DefaultRadius = 3;
    private const double DefaultThreshold = 128;
    private const double DefaultEdgeLow = 50;
    private const double DefaultEdgeHigh = 150;
    private const double DefaultSigma = 1;
    private const int MinimumRadius = 1;
    private const int MaximumRadius = 31;
    private const double MinimumSigma = 0.01;
    private const double MaximumSigma = 100;

    private RoiFilterKind _selectedFilter;
    private string _threshold = "128";
    private string _edgeLow = "50";
    private string _edgeHigh = "150";
    private string _radius = "3";
    private string _sigma = "1";
    private RoiFilterExecution _execution;
    private int _inputMaximum = ByteMaximum;
    private double _radiusValue = DefaultRadius;
    private double _sigmaValue = DefaultSigma;
    private double _thresholdValue = DefaultThreshold;
    private double _edgeLowValue = DefaultEdgeLow;
    private double _edgeHighValue = DefaultEdgeHigh;
    private string _validationMessage = "";
    private bool _isEditing;
    private FilterSettings? _originalSettings;
    private FilterSettings? _draftSettings;
    private IReadOnlyList<Roi>? _editingRois;

    public RoiFilterKind SelectedFilter => _selectedFilter;
    public string Threshold => _threshold;
    public string EdgeLow => _edgeLow;
    public string EdgeHigh => _edgeHigh;
    public string Radius => _radius;
    public string Sigma => _sigma;
    public RoiFilterExecution Execution => _execution;

    public double RadiusValue => _radiusValue;
    public double SigmaValue => _sigmaValue;
    public double ThresholdValue => _thresholdValue;
    public double EdgeLowValue => _edgeLowValue;
    public double EdgeHighValue => _edgeHighValue;
    public double ThresholdMaximum => Math.Max(IsBinarize ? ByteMaximum : _inputMaximum, _thresholdValue);

    public string ValidationMessage => _validationMessage;
    public bool IsEditing => _isEditing;
    public FilterSettings? DraftSettings => _draftSettings;
    public IReadOnlyList<Roi>? EditingRois => _editingRois;

    public bool IsBinarize => FilterSettings.UsesByteThresholdKind(_selectedFilter);
    public bool IsEdge => _selectedFilter == RoiFilterKind.Edge;
    public bool IsIndustrial => FilterSettings.IsIndustrialKind(_selectedFilter);
    public bool UsesRadius => FilterSettings.UsesRadiusKind(_selectedFilter);
    public bool UsesSigma => FilterSettings.UsesSigmaKind(_selectedFilter);
    public bool UsesThreshold => IsBinarize || FilterSettings.UsesNativeThresholdKind(_selectedFilter);
    public string ThresholdCaption => FilterCatalog.GetThresholdCaption(_selectedFilter);

    public bool SetSelectedFilter(RoiFilterKind value)
    {
        if (_selectedFilter == value)
        {
            return false;
        }

        _selectedFilter = value;
        return true;
    }

    public bool SetThreshold(string value) => SetString(ref _threshold, value);
    public bool SetEdgeLow(string value) => SetString(ref _edgeLow, value);
    public bool SetEdgeHigh(string value) => SetString(ref _edgeHigh, value);
    public bool SetRadius(string value) => SetString(ref _radius, value);
    public bool SetSigma(string value) => SetString(ref _sigma, value);

    public bool SetExecution(RoiFilterExecution value)
    {
        if (_execution == value)
        {
            return false;
        }

        _execution = value;
        return true;
    }

    public bool SetValidationMessage(string value) => SetString(ref _validationMessage, value);

    public bool UpdateInputMaximum(PixelKind? pixelKind)
    {
        ArgumentNullException.ThrowIfNull(this);
        var maximum = pixelKind switch
        {
            null => _inputMaximum,
            PixelKind.Gray16 => Gray16Maximum,
            _ => ByteMaximum
        };

        if (_inputMaximum == maximum)
        {
            return false;
        }

        _inputMaximum = maximum;
        return true;
    }

    public void RefreshSliderValues(Action<string> notifyPropertyChanged)
    {
        ArgumentNullException.ThrowIfNull(notifyPropertyChanged);
        UpdateSlider(_radius, MinimumRadius, MaximumRadius, ref _radiusValue, nameof(RadiusValue), notifyPropertyChanged);
        UpdateSlider(_sigma, MinimumSigma, MaximumSigma, ref _sigmaValue, nameof(SigmaValue), notifyPropertyChanged);
        if (UpdateSlider(_threshold, 0, IsBinarize ? ByteMaximum : Gray16Maximum, ref _thresholdValue, nameof(ThresholdValue), notifyPropertyChanged))
        {
            notifyPropertyChanged(nameof(ThresholdMaximum));
        }
        UpdateSlider(_edgeLow, 0, ByteMaximum, ref _edgeLowValue, nameof(EdgeLowValue), notifyPropertyChanged);
        UpdateSlider(_edgeHigh, 0, ByteMaximum, ref _edgeHighValue, nameof(EdgeHighValue), notifyPropertyChanged);
    }

    public FilterSettings? TryParseSettings(out string validationError)
    {
        try
        {
            static double ParseNumber(string text) => double.Parse(text, CultureInfo.CurrentCulture);
            var threshold = UsesThreshold ? ParseNumber(_threshold) : DefaultThreshold;
            var edgeLow = IsEdge ? ParseNumber(_edgeLow) : DefaultEdgeLow;
            var edgeHigh = IsEdge ? ParseNumber(_edgeHigh) : DefaultEdgeHigh;
            var radius = UsesRadius ? int.Parse(_radius, CultureInfo.CurrentCulture) : DefaultRadius;
            var sigma = UsesSigma ? ParseNumber(_sigma) : DefaultSigma;
            var execution = IsIndustrial ? _execution : RoiFilterExecution.CpuOnly;
            var settings = new FilterSettings(_selectedFilter, threshold, edgeLow, edgeHigh, radius, sigma, execution);
            settings.Validate();
            validationError = "";
            return settings;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            validationError = ValidationFailureGuidance;
            return null;
        }
    }

    public void LoadSettings(FilterSettings settings, Action<string> notifyPropertyChanged)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(notifyPropertyChanged);

        if (SetValidationMessage(""))
        {
            notifyPropertyChanged(nameof(ValidationMessage));
        }
        if (SetSelectedFilter(settings.Kind))
        {
            notifyPropertyChanged(nameof(SelectedFilter));
        }
        if (SetThreshold(settings.Threshold.ToString(CultureInfo.CurrentCulture)))
        {
            notifyPropertyChanged(nameof(Threshold));
        }
        if (SetEdgeLow(settings.EdgeLow.ToString(CultureInfo.CurrentCulture)))
        {
            notifyPropertyChanged(nameof(EdgeLow));
        }
        if (SetEdgeHigh(settings.EdgeHigh.ToString(CultureInfo.CurrentCulture)))
        {
            notifyPropertyChanged(nameof(EdgeHigh));
        }
        if (SetRadius(settings.Radius.ToString(CultureInfo.CurrentCulture)))
        {
            notifyPropertyChanged(nameof(Radius));
        }
        if (SetSigma(settings.Sigma.ToString(CultureInfo.CurrentCulture)))
        {
            notifyPropertyChanged(nameof(Sigma));
        }
        if (SetExecution(settings.Execution))
        {
            notifyPropertyChanged(nameof(Execution));
        }
    }

    public void BeginEdit(FilterSettings initialSettings, IReadOnlyList<Roi> selectedRois)
    {
        ArgumentNullException.ThrowIfNull(initialSettings);
        ArgumentNullException.ThrowIfNull(selectedRois);
        _originalSettings = _draftSettings = initialSettings;
        _editingRois = selectedRois;
        _isEditing = true;
    }

    public void UpdateDraftSettings(FilterSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _draftSettings = settings;
    }

    public FilterSettings? ResetEdit()
    {
        var original = _originalSettings;
        _isEditing = false;
        _originalSettings = null;
        _draftSettings = null;
        _editingRois = null;
        return original;
    }

    private static bool SetString(ref string field, string value)
    {
        if (EqualityComparer<string>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        return true;
    }

    private static bool UpdateSlider(
        string text,
        double minimum,
        double maximum,
        ref double field,
        string propertyName,
        Action<string> notifyPropertyChanged)
    {
        if (!double.TryParse(text, CultureInfo.CurrentCulture, out var value)
            || !double.IsFinite(value)
            || value < minimum
            || value > maximum
            || EqualityComparer<double>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        notifyPropertyChanged(propertyName);
        return true;
    }
}
