using System.Windows.Media;
using VisionCanvas.Rendering;

namespace TeachingToolWpf.Presentation;

/// <summary>Application display settings; publishes one immutable palette without editing the document.</summary>
public sealed class RoiStyleSettingsViewModel : ObservableObject
{
    private string _error = "";

    public RoiStyleDraft Default { get; } = new("일반 ROI");
    public RoiStyleDraft Child { get; } = new("자식 ROI");
    public RoiStyleDraft Parent { get; } = new("부모 ROI");
    public IReadOnlyList<RoiStyleDraft> Roles
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
        Roles = [Default, Child, Parent];
        Apply = new(ApplySettings);
        Reset = new(ResetSettings);
        RefreshDrafts();
    }

    private void ApplySettings()
    {
        RoiStylePalette next;
        try
        {
            next = new(Default.Build(Palette.Default), Child.Build(Palette.Child), Parent.Build(Palette.Parent));
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
        Notify(nameof(DefaultThickness));
        Notify(nameof(ChildThickness));
        Notify(nameof(ParentThickness));
    }
}
