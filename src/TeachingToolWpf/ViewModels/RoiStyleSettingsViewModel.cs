using System.Windows.Media;
using TeachingToolWpf.Commands;
using TeachingToolWpf.Infrastructure;
using VisionCanvas.Core.Geometry;
using VisionCanvas.Rendering;

namespace TeachingToolWpf.ViewModels;

/// <summary>ROI 표시 설정과 핸들 크기를 게시한다. 문서와 편집 이력은 변경하지 않는다.</summary>
public sealed class RoiStyleSettingsViewModel : ObservableObject
{
    private string _error = "";
    private double _handleScale = HandleGeometry.DefaultScale;

    public double HandleScale
    {
        get => _handleScale;
        set
        {
            HandleGeometry.ValidateScale(value);
            Publish(Palette, value);
        }
    }

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
        Publish(next, HandleScale);
        RefreshDrafts();
    }

    private void ResetSettings()
    {
        Error = "";
        Publish(RoiStylePalette.Standard, HandleGeometry.DefaultScale);
        RefreshDrafts();
    }

    private void Publish(RoiStylePalette next, double handleScale)
    {
        if (Palette == next && HandleScale == handleScale)
        {
            return;
        }

        Palette = next;
        Set(ref _handleScale, handleScale, nameof(HandleScale));
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
