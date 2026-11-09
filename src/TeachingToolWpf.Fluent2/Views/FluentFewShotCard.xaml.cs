// File: Views/LiquidFewShotCard.xaml.cs
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using TeachingToolWpf.Fluent2.ViewModels;
using TeachingToolWpf.ViewModels;

namespace TeachingToolWpf.Fluent2.Views;

public sealed partial class FluentFewShotCard : UserControl
{
    private readonly HashSet<ValidationError> _errors = [];

    public FluentFewShotCard()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    private EdgeFewShotViewModel? ResolveViewModel()
    {
        if (DataContext is EdgeFewShotViewModel direct)
        {
            return direct;
        }

        if (DataContext is Fluent2StudioViewModel studioVm)
        {
            return studioVm.Studio.Inspections.FewShot;
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
        if (e.OldValue is EdgeFewShotViewModel previous)
        {
            previous.HasInputErrors = false;
        }

        SyncErrors();
    }

    private void SyncErrors()
    {
        EdgeFewShotViewModel? vm = ResolveViewModel();
        if (vm is not null)
        {
            vm.HasInputErrors = _errors.Count > 0;
        }
    }
}
