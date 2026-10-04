using System.ComponentModel;
using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Rendering;

namespace TeachingToolWpf.Presentation;

public enum InspectionToolKind
{
    Pattern,
    PrimitiveShapes
}

public sealed record InspectionToolOption(InspectionToolKind Kind, string Name);

/// <summary>검사 도구 선택과 단일 실행을 조정하며 각 도구의 모델·판정 정책은 소유하지 않는다.</summary>
public sealed class InspectionToolsViewModel : ObservableObject, IDisposable
{
    private InspectionToolKind _selected;
    private DocumentState? _state;
    private long _generation;
    private bool _live;
    private bool _canExecute;
    private bool _updating;
    private bool _disposed;

    public InspectionToolsViewModel(ShapeMatchingViewModel pattern, PrimitiveShapeDetectionViewModel shapes)
    {
        Pattern = pattern;
        Shapes = shapes;
        Pattern.Changed += OnChanged;
        Shapes.Changed += OnChanged;
        Pattern.PropertyChanged += OnExecutionChanged;
        Shapes.PropertyChanged += OnExecutionChanged;
    }

    public event Action? Changed;
    public ShapeMatchingViewModel Pattern
    {
        get;
    }
    public PrimitiveShapeDetectionViewModel Shapes
    {
        get;
    }
    public IReadOnlyList<InspectionToolOption> Tools
    {
        get;
    } =
    [
        new(InspectionToolKind.Pattern, "학습 패턴 찾기"),
        new(InspectionToolKind.PrimitiveShapes, "일반 도형 찾기 · 학습 없음")
    ];
    public bool IsIdle => Pattern.IsIdle && Shapes.IsIdle;
    public bool ShowPatterns => Selected == InspectionToolKind.Pattern;
    public bool ShowShapes => Selected == InspectionToolKind.PrimitiveShapes;
    public ResultOverlay? Overlay => ShowPatterns ? Pattern.Overlay : Shapes.Overlay;

    public InspectionToolKind Selected
    {
        get => _selected;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            if (IsIdle && Set(ref _selected, value))
            {
                Notify(nameof(ShowPatterns));
                Notify(nameof(ShowShapes));
                UpdateAvailability();
                OnChanged();
            }
        }
    }

    public void Update(DocumentState state, long generation, bool live, bool canExecute)
    {
        _state = state;
        _generation = generation;
        _live = live;
        _canExecute = canExecute;
        UpdateAvailability();
    }

    private void UpdateAvailability()
    {
        if (_disposed || _updating || _state is null)
        {
            return;
        }
        _updating = true;
        try
        {
            Pattern.Update(_state, _generation, _live, _canExecute, ShowPatterns && Shapes.IsIdle);
            Shapes.Update(_state, _generation, _live, _canExecute, ShowShapes && Pattern.IsIdle);
            Notify(nameof(IsIdle));
        }
        finally
        {
            _updating = false;
        }
    }

    private void OnExecutionChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(IsIdle) or nameof(ShapeMatchingViewModel.IsBusy))
        {
            UpdateAvailability();
        }
    }

    private void OnChanged() => Changed?.Invoke();

    public void Dispose()
    {
        _disposed = true;
        Pattern.Changed -= OnChanged;
        Shapes.Changed -= OnChanged;
        Pattern.PropertyChanged -= OnExecutionChanged;
        Shapes.PropertyChanged -= OnExecutionChanged;
        Pattern.Dispose();
        Shapes.Dispose();
    }
}
