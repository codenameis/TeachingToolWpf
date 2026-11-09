// File: Views/LiquidSuiteCard.xaml.cs
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using TeachingToolWpf.Fluent2.ViewModels;
using TeachingToolWpf.ViewModels;

namespace TeachingToolWpf.Fluent2.Views;

public sealed partial class FluentSuiteCard : UserControl
{
    private readonly HashSet<ValidationError> _errors = [];

    public FluentSuiteCard()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    private InspectionSuiteViewModel? ResolveViewModel()
    {
        if (DataContext is InspectionSuiteViewModel direct)
        {
            return direct;
        }

        if (DataContext is Fluent2StudioViewModel studioVm)
        {
            return studioVm.Studio.Inspections.Suite;
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
        if (e.OldValue is InspectionSuiteViewModel previous)
        {
            previous.SetInputError(false);
        }

        SyncErrors();
    }

    private void SyncErrors()
    {
        InspectionSuiteViewModel? vm = ResolveViewModel();
        if (vm is not null)
        {
            vm.SetInputError(_errors.Count > 0);
        }
    }
}
