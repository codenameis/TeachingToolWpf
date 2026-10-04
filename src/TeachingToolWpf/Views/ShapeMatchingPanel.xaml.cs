using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TeachingToolWpf.Presentation;

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
        var valid = _numericErrors.Count == 0;
        // Button은 이 값과 ICommand.CanExecute를 함께 적용한다.
        TrainButton.IsEnabled = valid;
        SearchButton.IsEnabled = valid;
        NumericInputError.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
        if (_observedSource is ShapeMatchingViewModel viewModel)
        {
            viewModel.HasInputErrors = !valid;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => ObserveSource();

    private void OnUnloaded(object sender, RoutedEventArgs e) => UnsubscribeSource();

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
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

    private void UnsubscribeSource()
    {
        if (_observedSource is not null)
        {
            _observedSource.PropertyChanged -= OnSourcePropertyChanged;
            if (_observedSource is ShapeMatchingViewModel viewModel)
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
