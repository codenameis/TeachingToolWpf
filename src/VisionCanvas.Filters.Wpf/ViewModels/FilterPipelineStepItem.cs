using VisionCanvas.Core.Editing;

namespace VisionCanvas.Filters.Wpf.ViewModels;

/// <summary>
/// Represents a single step in the ordered pre-inspection preprocessing filter pipeline list.
/// Users can toggle <see cref="IsEnabled"/>, reorder steps up/down, or remove a step so slow filters can be bypassed immediately.
/// </summary>
public sealed class FilterPipelineStepItem : ObservableObject
{
    private readonly Action<FilterPipelineStepItem>? _onChanged;
    private int _stepOrder;
    private bool _isEnabled;
    private FilterSettings _settings;

    public FilterPipelineStepItem(int stepOrder, FilterSettings settings, Action<FilterPipelineStepItem>? onChanged = null)
    {
        _stepOrder = stepOrder;
        _isEnabled = settings.IsEnabled;
        _settings = settings;
        _onChanged = onChanged;
    }

    public int StepOrder
    {
        get => _stepOrder;
        private set
        {
            if (Set(ref _stepOrder, value))
            {
                Notify(nameof(StepBadge));
            }
        }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (Set(ref _isEnabled, value))
            {
                _settings = _settings with { IsEnabled = value };
                Notify(nameof(Settings));
                Notify(nameof(SummaryText));
                _onChanged?.Invoke(this);
            }
        }
    }

    public FilterSettings Settings
    {
        get => _settings;
        private set
        {
            if (Set(ref _settings, value))
            {
                Notify(nameof(SummaryText));
            }
        }
    }

    public string StepBadge => $"#{StepOrder}";

    public string SummaryText => Settings.FormatSummary();

    public void UpdateSettings(FilterSettings updated)
    {
        Settings = updated with { IsEnabled = IsEnabled };
    }

    public void RefreshStepOrder(int oneBasedOrder)
    {
        StepOrder = oneBasedOrder;
    }
}
