// File: Views/LiquidPatternCard.xaml.cs
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TeachingToolWpf.Fluent2.ViewModels;
using TeachingToolWpf.ViewModels;

namespace TeachingToolWpf.Fluent2.Views;

public sealed partial class FluentPatternCard : UserControl
{
    private readonly Dictionary<ValidationError, TextBox> _numericErrors = [];
    private INotifyPropertyChanged? _observedSource;
    private bool _resetQueued;

    public FluentPatternCard()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    private ShapeMatchingViewModel? ResolveMatchingViewModel()
    {
        if (DataContext is Fluent2StudioViewModel studioVm)
        {
            return studioVm.Studio.Matching;
        }

        return DataContext as ShapeMatchingViewModel;
    }

    private async void OnSaveInspectionLog(object sender, RoutedEventArgs e)
    {
        ShapeMatchingViewModel? model = ResolveMatchingViewModel();
        if (model is null)
        {
            return;
        }

        Microsoft.Win32.SaveFileDialog dialog = new()
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

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ObserveSource();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        UnsubscribeSource(clearInputErrors: false);
    }

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
        _observedSource = ResolveMatchingViewModel();
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
            if (clearInputErrors && (_observedSource is ShapeMatchingViewModel viewModel))
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
        if (_resetQueued || (_numericErrors.Count == 0))
        {
            return;
        }

        _resetQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.DataBind, () =>
        {
            _resetQueued = false;
            foreach (TextBox editor in _numericErrors.Values.Distinct().ToArray())
            {
                editor.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
            }

            RefreshNumericValidation();
        });
    }
}
