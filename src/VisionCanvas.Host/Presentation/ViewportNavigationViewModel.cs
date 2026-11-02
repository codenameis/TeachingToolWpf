using System.ComponentModel;
using System.Windows.Input;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Interaction;

namespace VisionCanvas.Presentation;

/// <summary>렌더러 공통 검사 잠금과 선택에 영향을 주지 않는 목록 탐색. 영상 소유권은 바꾸지 않는다.</summary>
public sealed class ViewportNavigationViewModel : INotifyPropertyChanged
{
    private readonly EditorSession _editor;
    private readonly InteractionController _input;
    private readonly Func<bool> _available;
    private readonly NavigationCommand _focus;
    private readonly NavigationCommand _fit;
    private object? _rowVersion;
    private RoiNavigationItem? _target;
    private bool _refreshingTargets;

    public ViewportNavigationViewModel(EditorSession editor, InteractionController input, ViewportLayout layout, Func<bool> available)
    {
        _editor = editor;
        _input = input;
        _available = available;
        _focus = new(() => editor.FocusRoi(_target!.Id, layout.Width, layout.Height),
            () => CanNavigate && _target is not null && Contains(_target.Id));
        _fit = new(() => editor.FitImage(layout.Width, layout.Height), () => CanNavigate);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public bool IsLocked
    {
        get => !_input.IsEnabled;
        set
        {
            _input.IsEnabled = !value;
            Refresh();
        }
    }
    public bool CanNavigate => _input.IsEnabled && _available() && !_input.IsDragging &&
        !_editor.IsPreviewing && _input.PolygonPointCount == 0 && _editor.State.Image is not null;
    public IReadOnlyList<RoiNavigationItem> Targets { get; private set; } = [];
    public RoiNavigationItem? Target
    {
        get => _target;
        set
        {
            if (_refreshingTargets || !CanNavigate || _target == value)
            {
                return;
            }
            _target = value;
            PropertyChanged?.Invoke(this, new(nameof(Target)));
            _focus.Refresh();
            _focus.Execute(null);
        }
    }
    public ICommand FocusCommand => _focus;
    public ICommand FitCommand => _fit;

    public void RefreshTargets()
    {
        var state = _editor.State;
        if (ReferenceEquals(_rowVersion, state.Snapshot.RowVersion))
        {
            return;
        }
        _rowVersion = state.Snapshot.RowVersion;
        Targets = state.Rois.Values
            .Select(roi => new RoiNavigationItem(roi.Id, roi.Name, roi.Order))
            .OrderBy(item => item.Order)
            .ToArray();
        _target = _target is null ? null : Targets.FirstOrDefault(item => item.Id == _target.Id);
        _refreshingTargets = true;
        try
        {
            PropertyChanged?.Invoke(this, new(nameof(Targets)));
            PropertyChanged?.Invoke(this, new(nameof(Target)));
        }
        finally
        {
            _refreshingTargets = false;
        }
        Refresh();
    }

    public void Refresh()
    {
        if (_target is not null && !Contains(_target.Id))
        {
            _target = null;
            PropertyChanged?.Invoke(this, new(nameof(Target)));
        }
        PropertyChanged?.Invoke(this, new(nameof(IsLocked)));
        PropertyChanged?.Invoke(this, new(nameof(CanNavigate)));
        _focus.Refresh();
        _fit.Refresh();
    }

    private bool Contains(Guid id) => _editor.State.Rois.ContainsKey(id);

    private sealed class NavigationCommand(Action execute, Func<bool> available) : ICommand
    {
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => available();
        public void Execute(object? parameter)
        {
            if (CanExecute(parameter))
            {
                execute();
            }
        }
        public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed record RoiNavigationItem(Guid Id, string Name, long Order);
