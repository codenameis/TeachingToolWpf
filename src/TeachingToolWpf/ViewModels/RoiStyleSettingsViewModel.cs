using System.Windows.Media;
using TeachingToolWpf.Commands;
using TeachingToolWpf.Infrastructure;
using VisionCanvas.Rendering;

namespace TeachingToolWpf.ViewModels;

/// <summary>Application display settings; publishes one immutable palette without editing the document.</summary>
public sealed class RoiStyleSettingsViewModel : ObservableObject
{
    private string _error = "";

    public RoiStyles Default { get; } = new("공통 표시");
    public RoiStyles Child { get; } = new("자식 ROI");
    public RoiStyles Parent { get; } = new("ROI 그룹");
    public RoiStyles Module { get; } = new("모듈 그룹");
    public RoiStyles Root { get; } = new("루트 그룹");
    public IReadOnlyList<RoiStyles> Roles
    {
        get;
    }

    public RoiStylePalette Palette
    {
        get;
        private set;
    } = RoiStylePalette.Standard;

    public string DefaultThickness
    {
        get => Default.Thickness;
        set => Default.Thickness = value;
    }

    public string ChildThickness
    {
        get => Child.Thickness;
        set => Child.Thickness = value;
    }

    public string ParentThickness
    {
        get => Parent.Thickness;
        set => Parent.Thickness = value;
    }

    public string Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }

    public Brush DefaultColor => DisplayStyleInput.Swatch(Palette.Default.Color);
    public Brush ChildColor => DisplayStyleInput.Swatch(Palette.Child.Color);
    public Brush ParentColor => DisplayStyleInput.Swatch(Palette.Parent.Color);

    public RelayCommand Apply
    {
        get;
    }

    public RelayCommand Reset
    {
        get;
    }

    public event Action? Changed;

    public RoiStyleSettingsViewModel()
    {
        Roles = [Default, Child, Parent, Module, Root];
        Apply = new(ApplySettings);
        Reset = new(ResetSettings);
        RefreshDrafts();
    }

    private void ApplySettings()
    {
        RoiStylePalette next;
        try
        {
            next = new(Default.Build(Palette.Default), Child.Build(Palette.Child), Parent.Build(Palette.Parent),
                Module.Build(Palette.Module), Root.Build(Palette.Root));
        }
        catch (ArgumentException ex)
        {
            Error = ex.Message;
            return;
        }
        Error = "";
        Publish(next);
        RefreshDrafts();
    }

    private void ResetSettings()
    {
        Error = "";
        Publish(RoiStylePalette.Standard);
        RefreshDrafts();
    }

    private void Publish(RoiStylePalette next)
    {
        if (Palette == next)
        {
            return;
        }

        Palette = next;
        Notify(nameof(Palette));
        Notify(nameof(DefaultColor));
        Notify(nameof(ChildColor));
        Notify(nameof(ParentColor));
        Changed?.Invoke();
    }

    private void RefreshDrafts()
    {
        Default.Refresh(Palette.Default);
        Child.Refresh(Palette.Child);
        Parent.Refresh(Palette.Parent);
        Module.Refresh(Palette.Module);
        Root.Refresh(Palette.Root);
        Notify(nameof(DefaultThickness));
        Notify(nameof(ChildThickness));
        Notify(nameof(ParentThickness));
    }
}
