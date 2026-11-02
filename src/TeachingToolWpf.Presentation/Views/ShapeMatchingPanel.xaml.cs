using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TeachingToolWpf.ViewModels;

namespace TeachingToolWpf.Views;

public partial class ShapeMatchingPanel : UserControl
{
    private readonly Dictionary<ValidationError, TextBox> _numericErrors = [];
    private INotifyPropertyChanged? _observedSource;
    private bool _resetQueued;

    public ShapeMatchingPanel()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    private async void OnSaveInspectionLog(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ShapeMatchingViewModel model)
        {
            return;
        }
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "검사 로그 (*.json)|*.json",
            FileName = $"inspection-{DateTime.Now:yyyyMMdd-HHmmss}.json"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }
        await model.SaveInspectionLogsAsync(dialog.FileName);
    }

    private void OnNumericValidationError(object sender, ValidationErrorEventArgs e)
    {
        if (e.OriginalSource is not TextBox editor)
        {
            return;
        }
        if (e.Action == ValidationErrorEventAction.Added)
        {
            _numericErrors[e.Error] = editor;
        }
        else
        {
            _numericErrors.Remove(e.Error);
        }
        RefreshNumericValidation();
    }

    private void RefreshNumericValidation()
    {
        if (_observedSource is ShapeMatchingViewModel viewModel)
        {
            viewModel.HasInputErrors = _numericErrors.Count != 0;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => ObserveSource();

    // 탭 전환은 입력 초안을 폐기하는 동작이 아니다. 숨겨진 동안에도 실행 차단을 유지한다.
    private void OnUnloaded(object sender, RoutedEventArgs e) => UnsubscribeSource(clearInputErrors: false);

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsLoaded && e.OldValue is ShapeMatchingViewModel previous)
        {
            previous.HasInputErrors = false;
        }
        if (IsLoaded)
        {
            ObserveSource();
        }
        QueueInvalidInputReset();
    }

    private void ObserveSource()
    {
        UnsubscribeSource();
        _observedSource = DataContext as INotifyPropertyChanged;
        if (_observedSource is not null)
        {
            _observedSource.PropertyChanged += OnSourcePropertyChanged;
        }
        RefreshNumericValidation();
    }

    private void UnsubscribeSource(bool clearInputErrors = true)
    {
        if (_observedSource is not null)
        {
            _observedSource.PropertyChanged -= OnSourcePropertyChanged;
            if (clearInputErrors && _observedSource is ShapeMatchingViewModel viewModel)
            {
                viewModel.HasInputErrors = false;
            }
            _observedSource = null;
        }
    }

    private void OnSourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShapeMatchingViewModel.Method))
        {
            QueueInvalidInputReset();
        }
    }

    private void QueueInvalidInputReset()
    {
        if (_resetQueued || _numericErrors.Count == 0)
        {
            return;
        }
        _resetQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.DataBind, () =>
        {
            _resetQueued = false;
            // 방식별 바인딩이 갱신된 뒤 잘못 입력된 UI 초안만 현재 설정으로 복원한다.
            foreach (var editor in _numericErrors.Values.Distinct().ToArray())
            {
                editor.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
            }
            RefreshNumericValidation();
        });
    }
}
