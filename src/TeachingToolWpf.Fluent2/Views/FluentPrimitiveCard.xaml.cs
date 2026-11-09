// File: Views/LiquidPrimitiveCard.xaml.cs
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using TeachingToolWpf.Fluent2.ViewModels;
using TeachingToolWpf.ViewModels;

namespace TeachingToolWpf.Fluent2.Views;

public sealed partial class FluentPrimitiveCard : UserControl
{
    private readonly HashSet<ValidationError> _errors = [];

    public FluentPrimitiveCard()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    private PrimitiveShapeDetectionViewModel? ResolveViewModel()
    {
        if (DataContext is PrimitiveShapeDetectionViewModel direct)
        {
            return direct;
        }

        if (DataContext is Fluent2StudioViewModel studioVm)
        {
            return studioVm.Studio.Inspections.Shapes;
        }

        return null;
    }

    private void OnValidationError(object sender, ValidationErrorEventArgs e)
    {
        if (e.Action == ValidationErrorEventAction.Added)
        {
            _errors.Add(e.Error);
        }
        else
        {
            _errors.Remove(e.Error);
        }

        SyncErrors();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _errors.Clear();
        SyncErrors();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        _errors.Clear();
        if (e.OldValue is PrimitiveShapeDetectionViewModel previous)
        {
            previous.HasInputErrors = false;
        }

        SyncErrors();
    }

    private void SyncErrors()
    {
        PrimitiveShapeDetectionViewModel? vm = ResolveViewModel();
        if (vm is not null)
        {
            vm.HasInputErrors = _errors.Count > 0;
        }
    }
}
